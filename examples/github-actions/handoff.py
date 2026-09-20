#!/usr/bin/env python3
"""handoff.py — caller-side checkpoint handoff helper for the ContractScribe
campaign workflow example (issue #186 / M6-A3).

Implements transport and activation logic only: sealed-slot handoff emission,
explicit caller activation authentication, artifact provenance/expiry/bounds/
digest checks, defensive extraction, checkpoint restoration outside the
checkout, and invocation-request rendering. All product acceptance — Campaign
State parsing, transitions, budgets, publication — remains in the C# CLI
invoked through the Action; this helper treats checkpoint bytes as opaque and
never interprets them.

Subcommands:

  gate        Producer admission: authenticate the current run (attempt 1,
              expected ref, repository) before credentials are materialized.
  select      Consumer admission: authenticate the current run, the caller
              activation, the producer run and the artifact metadata. Writes
              selection.json plus step outputs for the download step.
  verify      Post-download: verify the archive digest, defensively extract,
              check the sealed handoff, and restore the checkpoint privately.
  emit        Build the successor handoff directory (handoff.json +
              checkpoint.json) from the actual current checkpoint.
  activation  Render the paste-ready CONTRACTSCRIBE_ACTIVATION JSON after
              upload-artifact has bound the native artifact identity.
  request     Render the github-proposal-request-v1 JSON from caller
              configuration and verified repository facts.

Inputs arrive only through explicit arguments and the documented environment:
  CS_RUN_CONTEXT  JSON of the current run's runner-supplied facts
                  (runId/runNumber/runAttempt/event/ref/sha/repository/
                  repositoryId/workflowRef). The workflow maps github.*; tests
                  supply synthetic values.
  GITHUB_API_URL  REST API root (the runner value in production).
  CS_ACTIONS_API_URL  Test seam override for the API root. GITHUB_* names
                  are reserved on hosted runners — their env overrides are
                  ignored — so tests must use this name instead.
  GITHUB_TOKEN    read-only Actions API authority (the job's github.token).
  CS_ACTIVATION   the caller-owned activation JSON (select only).
  CS_CAMPAIGN     the caller-owned campaign claims JSON (request only).

Every failure prints a bounded `handoff.stop:<reason>` marker and exits 1.
"""

import hashlib
import json
import os
import re
import stat
import sys
import urllib.error
import urllib.request
import zipfile

BOUND_CHECKPOINT = 4 * 1024 * 1024      # CampaignStateContract product bound
BOUND_HANDOFF_JSON = 64 * 1024
BOUND_ARTIFACT = 5 * 1024 * 1024        # 4 MiB checkpoint + member + zip slack
BOUND_ACTIVATION = 8 * 1024
BOUND_CAMPAIGN = 8 * 1024
BOUND_API_RESPONSE = 4 * 1024 * 1024
BOUND_ZIP_MEMBER_NAME = 256
HTTP_TIMEOUT = 30
API_VERSION = "2026-03-10"
HANDOFF_NAME = "contract-scribe-handoff"
CONSUMER_EVENT = "schedule"
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
    ctx = env_json("CS_RUN_CONTEXT", BOUND_ACTIVATION)
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
    print("handoff.gate:ok")


# ---------------------------------------------------------------------------
# select — consumer admission (resume path, steps 1-4)
# ---------------------------------------------------------------------------

def parse_activation():
    value = env_json("CS_ACTIVATION", BOUND_ACTIVATION)
    require(set(value) == {"producer", "consumer"}, "activation-fields")
    producer, consumer = value["producer"], value["consumer"]
    require(isinstance(producer, dict) and isinstance(consumer, dict),
            "activation-shape")
    require(set(producer) == {"runId", "runNumber", "runAttempt",
                              "artifactId", "artifactDigest"},
            "activation-producer-fields")
    require(set(consumer) == {"runNumber", "event"}, "activation-consumer-fields")
    return {
        "producer": {
            "runId": int_field(producer, "runId"),
            "runNumber": int_field(producer, "runNumber"),
            "runAttempt": int_field(producer, "runAttempt"),
            "artifactId": int_field(producer, "artifactId"),
            "artifactDigest": str_field(producer, "artifactDigest"),
        },
        "consumer": {
            "runNumber": int_field(consumer, "runNumber"),
            "event": str_field(consumer, "event"),
        },
    }


def cmd_select(workdir):
    ctx = context()
    require(ctx["event"] == CONSUMER_EVENT, "consumer-event")
    api = _Api()
    run, expected_branch = own_run(api, ctx)
    wpath = workflow_path(ctx, run)
    activation = parse_activation()
    require(activation["consumer"]["runNumber"] == ctx["runNumber"],
            "activation-slot")
    require(activation["consumer"]["event"] == CONSUMER_EVENT,
            "activation-event")

    producer = api.get(
        f"/repos/{ctx['repository']}/actions/runs/{activation['producer']['runId']}")
    require(isinstance(producer, dict), "producer-run-absent")
    require(producer.get("id") == activation["producer"]["runId"],
            "producer-run-id")
    require(producer.get("run_number") == activation["producer"]["runNumber"],
            "producer-run-number")
    require(producer.get("run_attempt") == activation["producer"]["runAttempt"],
            "producer-run-attempt")
    require(producer.get("run_attempt") == 1, "producer-rerun")
    require(producer.get("status") == "completed", "producer-status")
    require(producer.get("conclusion") == "success", "producer-conclusion")
    require(producer.get("workflow_id") == run["workflow_id"],
            "producer-workflow")
    require(producer.get("path") == run["path"], "producer-workflow-path")
    require(producer.get("head_branch") == expected_branch, "producer-ref")
    require(SHA_RE.fullmatch(producer.get("head_sha") or ""),
            "producer-sha")
    prepo = producer.get("repository") or {}
    phead = producer.get("head_repository") or {}
    require(prepo.get("id") == ctx["repositoryId"], "producer-repo")
    require(phead.get("id") == ctx["repositoryId"], "producer-fork")

    listing = api.get(
        f"/repos/{ctx['repository']}/actions/runs/{activation['producer']['runId']}"
        f"/artifacts?name={HANDOFF_NAME}&per_page=100")
    require(isinstance(listing, dict), "artifact-list")
    artifacts = listing.get("artifacts")
    require(isinstance(artifacts, list), "artifact-list-shape")
    require(listing.get("total_count") == 1 and len(artifacts) == 1,
            "artifact-not-unique")
    require(artifacts[0].get("id") == activation["producer"]["artifactId"],
            "artifact-id-mismatch")

    artifact = api.get(
        f"/repos/{ctx['repository']}/actions/artifacts/{activation['producer']['artifactId']}")
    require(isinstance(artifact, dict), "artifact-absent")
    require(artifact.get("name") == HANDOFF_NAME, "artifact-name")
    wrun = artifact.get("workflow_run") or {}
    require(wrun.get("id") == activation["producer"]["runId"],
            "artifact-wrong-run")
    require(wrun.get("head_repository_id") == ctx["repositoryId"],
            "artifact-fork")
    require(wrun.get("repository_id") == ctx["repositoryId"],
            "artifact-repo")
    require(artifact.get("expired") is False, "artifact-expired")
    expires = artifact.get("expires_at") or ""
    require(isinstance(expires, str) and expires > _now(), "artifact-expiry")
    size = artifact.get("size_in_bytes")
    require(isinstance(size, int) and not isinstance(size, bool)
            and 0 < size <= BOUND_ARTIFACT, "artifact-size")
    digest = artifact.get("digest")
    require(isinstance(digest, str) and digest.startswith("sha256:")
            and SHA256_RE.fullmatch(digest[7:]), "artifact-digest")
    require(digest == activation["producer"]["artifactDigest"],
            "artifact-digest-mismatch")

    selection = {
        "artifactId": artifact["id"],
        "artifactDigest": digest,
        "producerRunId": producer["id"],
        "producer": {
            "repositoryId": ctx["repositoryId"],
            "repository": ctx["repository"],
            "workflowId": producer["workflow_id"],
            "workflowPath": producer["path"],
            "runId": producer["id"],
            "runNumber": producer["run_number"],
            "runAttempt": producer["run_attempt"],
            "event": producer["event"],
            "headRef": EXPECTED_REF_PREFIX + expected_branch,
            "headSha": producer["head_sha"],
        },
        "consumer": {
            "repositoryId": ctx["repositoryId"],
            "repository": ctx["repository"],
            "workflowId": run["workflow_id"],
            "runNumber": ctx["runNumber"],
            "event": CONSUMER_EVENT,
        },
        "activation": activation,
    }
    os.makedirs(workdir, exist_ok=True)
    path = os.path.join(workdir, "selection.json")
    write_private(path, json.dumps(selection, separators=(",", ":"),
                                   sort_keys=True).encode())
    output("artifact-id", str(artifact["id"]))
    output("producer-run-id", str(producer["id"]))
    output("artifact-digest", digest)
    print("handoff.select:ok")


def _now():
    import datetime
    return datetime.datetime.now(datetime.timezone.utc).strftime(
        "%Y-%m-%dT%H:%M:%SZ")


# ---------------------------------------------------------------------------
# verify — post-download bytes + seal check + private restore
# ---------------------------------------------------------------------------

def cmd_verify(archive, selection_path, state_dir):
    ctx = context()
    selection = strict_json(open(selection_path, "rb").read(),
                            BOUND_HANDOFF_JSON * 4, "selection")
    require(set(selection) == {"artifactId", "artifactDigest", "producerRunId",
                               "producer", "consumer", "activation"},
            "selection-fields")

    data = open(archive, "rb").read(BOUND_ARTIFACT + 1)
    require(len(data) <= BOUND_ARTIFACT, "archive-oversize")
    actual = "sha256:" + hashlib.sha256(data).hexdigest()
    require(actual == selection["artifactDigest"], "archive-digest")

    members = {}
    with zipfile.ZipFile(archive) as zf:
        infos = zf.infolist()
        require(len(infos) == 2, "zip-count")
        for info in infos:
            name = info.filename
            require(name in ("handoff.json", "checkpoint.json")
                    and "/" not in name and "\\" not in name
                    and not name.startswith(".") and len(name) <=
                    BOUND_ZIP_MEMBER_NAME and not info.is_dir(),
                    "zip-member")
            # external_attr>>16 carries Unix mode bits when present;
            # plain writestr archives store permission bits only (type 0).
            # Accept type-0 (ordinary) and regular files; reject symlink/
            # fifo/other special types.
            ftype = (info.external_attr >> 16) & 0o170000
            require(ftype in (0, 0o100000), "zip-member-type")
            require(info.file_size <= BOUND_CHECKPOINT, "zip-member-size")
            members[name] = zf.read(info)
    require(set(members) == {"handoff.json", "checkpoint.json"},
            "zip-members")

    handoff = strict_json(members["handoff.json"], BOUND_HANDOFF_JSON,
                          "handoff")
    require(set(handoff) == {"producer", "checkpoint", "consumer"},
            "handoff-fields")
    require(handoff["producer"] == selection["producer"], "handoff-producer")
    require(handoff["consumer"] == selection["consumer"], "handoff-seal")
    require(selection["consumer"]["runNumber"] == ctx["runNumber"],
            "seal-run-number")
    require(selection["consumer"]["event"] == ctx["event"], "seal-event")
    require(selection["consumer"]["repositoryId"] == ctx["repositoryId"],
            "seal-repo")
    require(selection["activation"]["consumer"]["runNumber"] ==
            ctx["runNumber"], "activation-slot")
    require(selection["activation"]["consumer"]["event"] == ctx["event"],
            "activation-event")

    checkpoint = members["checkpoint.json"]
    require(len(checkpoint) <= BOUND_CHECKPOINT, "checkpoint-oversize")
    sha = hashlib.sha256(checkpoint).hexdigest()
    expect = handoff["checkpoint"]
    require(isinstance(expect, dict) and set(expect) == {"sha256", "bytes"},
            "checkpoint-fields")
    require(expect["sha256"] == sha and expect["bytes"] == len(checkpoint),
            "checkpoint-digest")

    os.makedirs(state_dir, mode=0o700, exist_ok=True)
    os.chmod(state_dir, 0o700)
    state = os.path.join(state_dir, "checkpoint.json")
    write_private(state, checkpoint)
    output("state-path", state)
    print("handoff.verify:ok")


# ---------------------------------------------------------------------------
# emit — build the successor handoff directory
# ---------------------------------------------------------------------------

def cmd_emit(checkpoint_path, handoff_dir):
    ctx = context()
    api = _Api()
    run, expected_branch = own_run(api, ctx)
    wpath = workflow_path(ctx, run)

    st = os.stat(checkpoint_path)
    require(stat.S_ISREG(st.st_mode), "checkpoint-not-file")
    data = open(checkpoint_path, "rb").read(BOUND_CHECKPOINT + 1)
    require(len(data) <= BOUND_CHECKPOINT, "checkpoint-oversize")

    handoff = {
        "producer": {
            "repositoryId": ctx["repositoryId"],
            "repository": ctx["repository"],
            "workflowId": run["workflow_id"],
            "workflowPath": wpath,
            "runId": run["id"],
            "runNumber": run["run_number"],
            "runAttempt": run["run_attempt"],
            "event": run["event"],
            "headRef": EXPECTED_REF_PREFIX + expected_branch,
            "headSha": run["head_sha"],
        },
        "checkpoint": {
            "sha256": hashlib.sha256(data).hexdigest(),
            "bytes": len(data),
        },
        "consumer": {
            "repositoryId": ctx["repositoryId"],
            "repository": ctx["repository"],
            "workflowId": run["workflow_id"],
            "runNumber": run["run_number"] + 1,
            "event": CONSUMER_EVENT,
        },
    }
    os.makedirs(handoff_dir, exist_ok=True)
    write_private(os.path.join(handoff_dir, "handoff.json"),
                  json.dumps(handoff, separators=(",", ":"),
                             sort_keys=True).encode())
    write_private(os.path.join(handoff_dir, "checkpoint.json"), data)
    output("handoff-dir", handoff_dir)
    print("handoff.emit:ok")


# ---------------------------------------------------------------------------
# activation — render the next-hop CONTRACTSCRIBE_ACTIVATION template
# ---------------------------------------------------------------------------

def cmd_activation(handoff_dir, artifact_id, artifact_digest):
    handoff = strict_json(
        open(os.path.join(handoff_dir, "handoff.json"), "rb").read(),
        BOUND_HANDOFF_JSON, "handoff")
    require(set(handoff) == {"producer", "checkpoint", "consumer"},
            "handoff-fields")
    producer, consumer = handoff["producer"], handoff["consumer"]
    # upload-artifact emits a bare hex digest while the Actions REST API
    # reports "sha256:<hex>"; normalize to the REST representation once so
    # the installed activation matches authenticated artifact metadata.
    digest = artifact_digest
    if digest.startswith("sha256:"):
        digest = digest[7:]
    require(SHA256_RE.fullmatch(digest), "artifact-digest-format")
    digest = "sha256:" + digest
    require(str(int(artifact_id)) == artifact_id and int(artifact_id) > 0,
            "artifact-id-format")
    activation = {
        "producer": {
            "runId": producer["runId"],
            "runNumber": producer["runNumber"],
            "runAttempt": producer["runAttempt"],
            "artifactId": int(artifact_id),
            "artifactDigest": digest,
        },
        "consumer": {
            "runNumber": consumer["runNumber"],
            "event": consumer["event"],
        },
    }
    document = json.dumps(activation, separators=(",", ":"), sort_keys=True)
    output("activation-json", document)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    block = ("### contract-scribe handoff\n\n"
             "Handoff emitted. To authorize the next scheduled consumer, set "
             "repository variable `CONTRACTSCRIBE_ACTIVATION` to exactly:\n\n"
             "```json\n" + document + "\n```\n")
    if summary:
        with open(summary, "a", encoding="utf-8", newline="\n") as fh:
            fh.write(block)
    print(document)
    print("handoff.activation:ok")


# ---------------------------------------------------------------------------
# request — render github-proposal-request-v1 from caller claims + facts
# ---------------------------------------------------------------------------

def cmd_request(base_oid, state_path, out_path):
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
    owner = str_field(campaign, "repositoryOwner")
    name = str_field(campaign, "repositoryName")
    require(re.fullmatch(r"[A-Za-z0-9_.-]+", owner), "campaign-owner")
    require(re.fullmatch(r"[A-Za-z0-9_.-]+", name), "campaign-name")
    require(owner + "/" + name == ctx["repository"], "campaign-repo")
    require(SHA_RE.fullmatch(base_oid), "base-oid")
    require(os.path.isabs(state_path), "state-path-relative")
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
        "state": state_path,
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


# ---------------------------------------------------------------------------
# plumbing
# ---------------------------------------------------------------------------

def write_private(path, data):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "wb") as fh:
        fh.write(data)
    try:
        os.chmod(path, 0o600)
    except OSError:
        pass


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
    if len(argv) < 2:
        stop("usage")
    command = argv[1]
    if command == "gate":
        cmd_gate()
    elif command == "select":
        require(len(argv) == 4 and argv[2] == "--workdir", "select-usage")
        cmd_select(argv[3])
    elif command == "verify":
        require(len(argv) == 8 and argv[2] == "--archive"
                and argv[4] == "--selection" and argv[6] == "--state-dir",
                "verify-usage")
        cmd_verify(argv[3], argv[5], argv[7])
    elif command == "emit":
        require(len(argv) == 6 and argv[2] == "--checkpoint"
                and argv[4] == "--handoff-dir", "emit-usage")
        cmd_emit(argv[3], argv[5])
    elif command == "activation":
        require(len(argv) == 8 and argv[2] == "--handoff-dir"
                and argv[4] == "--artifact-id"
                and argv[6] == "--artifact-digest", "activation-usage")
        cmd_activation(argv[3], argv[5], argv[7])
    elif command == "request":
        require(len(argv) == 8 and argv[2] == "--base-oid"
                and argv[4] == "--state" and argv[6] == "--out",
                "request-usage")
        cmd_request(argv[3], argv[5], argv[7])
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
