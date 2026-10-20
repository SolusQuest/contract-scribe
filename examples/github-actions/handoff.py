#!/usr/bin/env python3
"""Validate an explicitly selected workflow invocation and render its request.

The CLI owns durable checkpoints in the campaign coordination ref. This helper
never downloads, uploads, restores, or interprets checkpoint artifacts. CS_OPERATION
is the caller's workflow_dispatch choice (start or resume); CS_CAMPAIGN and
CS_CAMPAIGN_LINEAGE supply reviewed claims. CS_RUN_CONTEXT contains github.* facts.
The existing read-only Actions API admission checks authenticate the current run.
"""

import json
import os
import re
import sys
import urllib.error
import urllib.request

BOUND_CONTEXT = 8 * 1024
BOUND_CAMPAIGN = 8 * 1024
BOUND_API_RESPONSE = 4 * 1024 * 1024
HTTP_TIMEOUT = 30
API_VERSION = "2026-03-10"
EXPECTED_REF_PREFIX = "refs/heads/"

IDENT_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._:-]*")
SHA_RE = re.compile(r"[0-9a-f]{40}")
SHA256_RE = re.compile(r"[0-9a-f]{64}")


class Stop(Exception):
    def __init__(self, reason):
        self.reason = reason
        super().__init__(reason)


def stop(reason):
    raise Stop(reason)


def require(condition, reason):
    if not condition:
        stop(reason)


def strict_json(raw, bound, what):
    """Parse a bounded JSON document: no duplicate keys, no NaN/Infinity."""
    if isinstance(raw, str):
        raw = raw.encode("utf-8")
    require(len(raw) <= bound, f"{what}-oversize")
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        stop(f"{what}-utf8")
    require("\x00" not in text, f"{what}-nul")

    def _object(pairs):
        out = {}
        for key, value in pairs:
            if key in out:
                raise ValueError("duplicate-key")
            out[key] = value
        return out

    def _constant(_value):
        raise ValueError("non-finite")

    try:
        return json.loads(text, object_pairs_hook=_object,
                          parse_constant=_constant)
    except (ValueError, UnicodeDecodeError):
        stop(f"{what}-json")


def env_json(name, bound):
    raw = os.environ.get(name)
    require(raw is not None and raw.strip(), f"{name}-absent")
    return strict_json(raw, bound, name.lower())


def context():
    """The current run's runner-supplied facts (github.* mapped to JSON)."""
    ctx = env_json("CS_RUN_CONTEXT", BOUND_CONTEXT)
    require(set(ctx) == {"runId", "runNumber", "runAttempt", "event", "ref",
                         "sha", "repository", "repositoryId", "workflowRef"},
            "context-fields")
    ctx["runId"] = int_field(ctx, "runId")
    ctx["runNumber"] = int_field(ctx, "runNumber")
    ctx["runAttempt"] = int_field(ctx, "runAttempt")
    ctx["repositoryId"] = int_field(ctx, "repositoryId")
    for key in ("event", "ref", "sha", "repository", "workflowRef"):
        require(isinstance(ctx[key], str) and ctx[key], f"context-{key}")
    require(SHA_RE.fullmatch(ctx["sha"]), "context-sha")
    require(re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+",
                         ctx["repository"]), "context-repository")
    return ctx


def int_field(obj, key):
    value = obj.get(key)
    if isinstance(value, bool):
        stop(f"field-{key}-type")
    if isinstance(value, int):
        return value
    if isinstance(value, str) and re.fullmatch(r"[0-9]+", value):
        return int(value)
    stop(f"field-{key}-type")


def str_field(obj, key, pattern=None):
    value = obj.get(key)
    require(isinstance(value, str) and value, f"field-{key}-type")
    if pattern is not None:
        require(pattern.fullmatch(value), f"field-{key}-format")
    return value


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


class _Api:
    """Bounded Actions REST reader. Token-bearing calls never follow
    redirects and never echo response bodies."""

    def __init__(self):
        root = (os.environ.get("CS_ACTIONS_API_URL")
                or os.environ.get("GITHUB_API_URL", ""))
        require(root.startswith("http://127.0.0.1")
                or root.startswith("http://[::1]")
                or root == "https://api.github.com"
                or re.fullmatch(r"https://[A-Za-z0-9.-]+/api/v3", root),
                "api-root")
        self.root = root.rstrip("/")
        self.token = os.environ.get("GITHUB_TOKEN", "")
        require(self.token != "", "api-token-absent")
        handlers = [_NoRedirect()]
        # Loopback (the test seam) must never route through an ambient
        # system proxy; the real api.github.com keeps normal detection.
        if root.startswith("http://127.0.0.1") or root.startswith("http://[::1]"):
            handlers.append(urllib.request.ProxyHandler({}))
        self.opener = urllib.request.build_opener(*handlers)

    def get(self, path):
        request = urllib.request.Request(
            self.root + path,
            headers={"Authorization": "Bearer " + self.token,
                     "Accept": "application/vnd.github+json",
                     "X-GitHub-Api-Version": API_VERSION,
                     "User-Agent": "contract-scribe-handoff"})
        try:
            with self.opener.open(request, timeout=HTTP_TIMEOUT) as resp:
                body = resp.read(BOUND_API_RESPONSE + 1)
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return None
            stop(f"api-status-{error.code}")
        except (urllib.error.URLError, OSError, ValueError):
            stop("api-transport")
        require(len(body) <= BOUND_API_RESPONSE, "api-response-oversize")
        return strict_json(body, BOUND_API_RESPONSE, "api-response")


def repo_record(api, repository):
    record = api.get(f"/repos/{repository}")
    require(isinstance(record, dict), "repo-record")
    require(isinstance(record.get("id"), int), "repo-id")
    require(isinstance(record.get("default_branch"), str)
            and record["default_branch"], "repo-default-branch")
    require(record.get("full_name") == repository, "repo-full-name")
    return record


def own_run(api, ctx):
    """Authenticate the current run against runner-supplied context.

    Returns the run record plus the expected branch. Requires attempt 1 and
    the repository default branch; callers additionally constrain the event.
    """
    repo = repo_record(api, ctx["repository"])
    require(repo["id"] == ctx["repositoryId"], "own-repo-id")
    run = api.get(f"/repos/{ctx['repository']}/actions/runs/{ctx['runId']}")
    require(isinstance(run, dict), "own-run-absent")
    require(run.get("id") == ctx["runId"], "own-run-id")
    require(run.get("run_number") == ctx["runNumber"], "own-run-number")
    require(run.get("run_attempt") == ctx["runAttempt"], "own-run-attempt")
    require(run.get("event") == ctx["event"], "own-run-event")
    require(run.get("head_sha") == ctx["sha"], "own-run-sha")
    require(ctx["ref"].startswith(EXPECTED_REF_PREFIX), "own-run-ref")
    require(run.get("head_branch") ==
            ctx["ref"][len(EXPECTED_REF_PREFIX):], "own-run-ref-mismatch")
    require(run.get("run_attempt") == 1, "own-run-attempt-one")
    expected_branch = repo["default_branch"]
    require(run.get("head_branch") == expected_branch, "own-run-branch")
    require(isinstance(run.get("workflow_id"), int), "own-workflow-id")
    require(isinstance(run.get("path"), str) and run["path"],
            "own-workflow-path")
    repo_obj = run.get("repository") or {}
    require(repo_obj.get("id") == ctx["repositoryId"], "own-run-repo")
    require(repo_obj.get("full_name") == ctx["repository"], "own-run-repo-name")
    return run, expected_branch


def workflow_path(ctx, run):
    """The workflow file path, cross-checked between the authenticated run
    record and the runner-supplied workflow_ref."""
    path = run["path"]
    ref = ctx["workflowRef"]
    expected = ctx["repository"] + "/" + path + "@"
    require(ref.startswith(expected), "workflow-ref-path")
    return path


# ---------------------------------------------------------------------------
# gate — producer admission (start path)
# ---------------------------------------------------------------------------

def cmd_gate():
    ctx = context()
    api = _Api()
    run, _branch = own_run(api, ctx)
    workflow_path(ctx, run)
    require(ctx["event"] == "workflow_dispatch", "gate-event")
    operation()
    print("handoff.gate:ok")


def operation():
    selected = os.environ.get("CS_OPERATION")
    require(selected in ("start", "resume"), "operation-selection")
    return selected


def cmd_request(base_oid, out_path):
    operation()
    ctx = context()
    campaign = env_json("CS_CAMPAIGN", BOUND_CAMPAIGN)
    require(set(campaign) == {"snapshot", "operationId", "generationId",
                              "targetRef", "policyCeilings",
                              "repositoryOwner", "repositoryName"},
            "campaign-fields")
    # The lineage is a separate caller authority (it also drives the
    # per-campaign concurrency group), not a member of the claims document.
    lineage = os.environ.get("CS_CAMPAIGN_LINEAGE") or ""
    require(IDENT_RE.fullmatch(lineage), "campaign-lineage")
    for key in ("operationId", "generationId"):
        require(IDENT_RE.fullmatch(str_field(campaign, key)),
                f"campaign-{key}")
    snapshot = str_field(campaign, "snapshot")
    require(len(snapshot) <= 512, "campaign-snapshot")
    target_ref = str_field(campaign, "targetRef")
    require(target_ref.startswith(EXPECTED_REF_PREFIX), "campaign-targetref")
    # The caller supplies the checked-out HEAD as expectedBaseCommitOid, so
    # the campaign target must be the run's checked-out ref; a different
    # targetRef would bind the request to the wrong base.
    require(target_ref == ctx["ref"], "campaign-target")
    owner = str_field(campaign, "repositoryOwner")
    name = str_field(campaign, "repositoryName")
    require(re.fullmatch(r"[A-Za-z0-9_.-]+", owner), "campaign-owner")
    require(re.fullmatch(r"[A-Za-z0-9_.-]+", name), "campaign-name")
    require(owner + "/" + name == ctx["repository"], "campaign-repo")
    require(SHA_RE.fullmatch(base_oid), "base-oid")
    require(base_oid == ctx["sha"], "base-context-mismatch")
    ceilings = campaign["policyCeilings"]
    require(isinstance(ceilings, dict)
            and set(ceilings) == {"maximumDocumentationBlocks",
                                  "maximumDistinctChangedFiles",
                                  "maximumCumulativePatchBytes"},
            "campaign-ceilings")
    for key, value in ceilings.items():
        require(isinstance(value, int) and not isinstance(value, bool)
                and 0 < value <= 1 << 30, f"ceiling-{key}")
    request = {
        "githubProposalRequestVersion": 1,
        "campaignLineage": lineage,
        "snapshot": campaign["snapshot"],
        "checkpointStore": "coordination-ref",
        "github": {
            "repositoryOwner": owner,
            "repositoryName": name,
            "targetRef": target_ref,
            "expectedBaseCommitOid": base_oid,
            "operationId": campaign["operationId"],
            "generationId": campaign["generationId"],
            "policy": {
                "maximumDocumentationBlocks":
                    ceilings["maximumDocumentationBlocks"],
                "maximumDistinctChangedFiles":
                    ceilings["maximumDistinctChangedFiles"],
                "maximumCumulativePatchBytes":
                    ceilings["maximumCumulativePatchBytes"],
            },
            "transition": "initial",
        },
    }
    write_private(out_path, json.dumps(request, separators=(",", ":"),
                                       sort_keys=True).encode())
    output("request-path", out_path)
    with open(out_path, encoding="utf-8") as fh:
        output("request-json", fh.read(), multiline=True)
    print("handoff.request:ok")


def write_private(path, data):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "wb") as fh:
        fh.write(data)
    try:
        os.chmod(path, 0o600)
    except OSError:
        pass  # platform without POSIX modes; the store remains private



def output(name, value, multiline=False):
    path = os.environ.get("GITHUB_OUTPUT")
    if not path:
        return
    with open(path, "a", encoding="utf-8", newline="\n") as fh:
        if multiline:
            fh.write(f"{name}<<CSOUT\n{value}\nCSOUT\n")
        else:
            require("\n" not in value and "\r" not in value, "output-value")
            fh.write(f"{name}={value}\n")


def main(argv):
    require(len(argv) >= 2, "usage")
    if argv[1] == "gate":
        require(len(argv) == 2, "gate-usage")
        cmd_gate()
    elif argv[1] == "request":
        require(len(argv) == 6 and argv[2] == "--base-oid" and argv[4] == "--out", "request-usage")
        cmd_request(argv[3], argv[5])
    else:
        stop("unknown-command")


if __name__ == "__main__":
    try:
        main(sys.argv)
    except Stop as error:
        print(f"handoff.stop:{error.reason}", file=sys.stderr)
        raise SystemExit(1)
    except SystemExit:
        raise
    except Exception:
        print("handoff.stop:exception", file=sys.stderr)
        raise SystemExit(1)
