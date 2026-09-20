#!/usr/bin/env python3
"""promote-candidate.py — guarded release-candidate operations (M6-R1).

Subcommands (all take --candidate <dir> pointing at a downloaded
release-candidate artifact, except resolve-artifact):

  resolve-artifact  Selector preflight: authenticate candidate_run_id and
                    emit the single unexpired release-candidate artifact id
                    + server digest for the download step.
  verify-candidate  Read-only gate shared by stage/promote: recomputes the
                    candidate digest, checks dispatch inputs, reads back the
                    producer run/artifact/repository identity, and requires
                    the map + action.yml AT source_revision to authorize the
                    exact pair.
  stage-draft       Create or adopt the unpublished draft payload Release
                    and converge its asset set to exactly the candidate
                    archive. No public tag is created.
  verify-draft      Read-only exact-match readback of the staged draft.
  promote           Publish the already verified, maintainer-approved
                    candidate: payload tag -> publish draft -> version tag.
                    Never rebuilds; a changed pair is a new candidate.

Credentials: CONTRACTSCRIBE_RELEASE_TOKEN (publication identity — release,
tag and asset mutations plus draft visibility) and
CONTRACTSCRIBE_RELEASE_READ_TOKEN (read-only provenance/CI readbacks, the
job GITHUB_TOKEN). Neither is ever taken from argv or artifact metadata.

Ambiguity rule: once a mutation request is dispatched, a lost or failed
response is ambiguous — re-read and adopt only the exact desired state,
otherwise stop. No mutation is retried inside a run; recovery happens
through a fresh, separately authorized dispatch.

Marker contract: release stage=<op>.<step> result=ok|fail reason=<short>
"""
import argparse
import hashlib
import json
import os
import re
import socket
import stat
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BOUND_METADATA_JSON = 2 * 1024 * 1024
BOUND_DOWNLOAD = 256 * 1024 * 1024
BOUND_MAX_PAGES = 5
BOUND_REDIRECTS = 3
BOUND_CONNECT_SECONDS = 25
BOUND_DOWNLOAD_SECONDS = 300
BOUND_REFERENCE = 1024

API_ROOT_PRODUCTION = "https://api.github.com"
REPOSITORY = "SolusQuest/contract-scribe"
WORKFLOW_PATH = ".github/workflows/release.yml"
CI_WORKFLOW_PATH = ".github/workflows/ci.yml"
REQUIRED_CI_JOB = "action_packaged"
ARTIFACT_NAME = "release-candidate"
UPLOAD_HOST_PRODUCTION = "uploads.github.com"

_HEX40 = re.compile(r"[0-9a-f]{40}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
_RELEASE_VERSION = re.compile(r"v\d+\.\d+\.\d+")
_WRAPPER_NAME = re.compile(r"[a-z0-9][a-z0-9-]{0,63}")
_SAFE_REASON = re.compile(r"[a-z0-9-]{1,48}")

# Marker stage for failures that escape a command (transport faults etc.);
# each command sets it before touching the API.
_STAGE = "dispatch"

# Ambiguous transport statuses for create mutations: the write may have
# landed (or an exact object already exists), so re-read and adopt only the
# exact desired state.
_AMBIGUOUS = ("http-unreachable", "http-server", "http-rate-limit",
              "http-conflict")


def marker(stage, result, reason="-"):
    print(f"release stage={stage} result={result} reason={reason}",
          flush=True)


def fail(stage, reason):
    assert _SAFE_REASON.fullmatch(reason)
    marker(stage, "fail", reason)
    raise SystemExit(1)


def check(stage, condition, reason):
    if not condition:
        fail(stage, reason)


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def canonical_bytes(document):
    return (json.dumps(document, indent=2, sort_keys=True,
                       ensure_ascii=False) + "\n").encode("utf-8")


def _no_duplicates(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate-member")
        result[key] = value
    return result


def strict_loads(data):
    return json.loads(data.decode("utf-8"),
                      object_pairs_hook=_no_duplicates)


def load_json_file(path, what, limit=BOUND_METADATA_JSON):
    info = os.lstat(path)
    if os.path.islink(path) or not stat.S_ISREG(info.st_mode):
        fail("read", f"{what}-not-regular")
    if info.st_size > limit:
        fail("read", f"{what}-oversize")
    try:
        with open(path, "rb") as fh:
            return strict_loads(fh.read())
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError):
        fail("read", f"{what}-malformed")


def write_private(path, data):
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    flags |= getattr(os, "O_NOFOLLOW", 0)
    fd = os.open(path, flags, 0o600)
    with os.fdopen(fd, "wb") as out:
        out.write(data)


def write_receipt(path, document):
    """Receipts (staged.json/promotion.json) are overwritten atomically —
    re-running a recovered operation must not trip on a prior receipt."""
    tmp = path + ".tmp"
    with open(tmp, "wb") as out:
        out.write(canonical_bytes(document))
    os.replace(tmp, path)


def env_text(name):
    value = os.environ.get(name)
    if value is None or value == "" or "\x00" in value or "\n" in value:
        return None
    return value


def write_output(name, value):
    path = env_text("GITHUB_OUTPUT")
    if path:
        with open(path, "a", encoding="utf-8", newline="\n") as fh:
            fh.write(f"{name}={value}\n")


# ---------------------------------------------------------------------------
# Test seam (mirrors the action's gated loopback override)
# ---------------------------------------------------------------------------

def test_api_root():
    root = os.environ.get("CONTRACTSCRIBE_RELEASE_TEST_API_ROOT")
    if os.environ.get("CONTRACTSCRIBE_RELEASE_TEST") != "1":
        if root is not None:
            fail("test-gate", "test-env-without-mode")
        return None
    if root is None:
        return None
    if os.environ.get("GITHUB_ACTIONS") != "true":
        fail("test-gate", "outside-actions")
    if os.environ.get("GITHUB_REPOSITORY") != REPOSITORY:
        fail("test-gate", "foreign-repository")
    parsed = urllib.parse.urlsplit(root)
    host = parsed.hostname or ""
    if (parsed.scheme != "http" or parsed.username or parsed.password
            or parsed.query or parsed.fragment
            or host not in ("127.0.0.1", "::1")
            or parsed.port is None or parsed.port in (80, 443)):
        fail("test-gate", "api-root-not-loopback")
    return root.rstrip("/")


def api_root():
    return test_api_root() or API_ROOT_PRODUCTION


def is_api_origin(url, root):
    try:
        a = urllib.parse.urlsplit(root)
        b = urllib.parse.urlsplit(url)
    except ValueError:
        return False
    return (a.scheme, a.hostname, a.port) == (b.scheme, b.hostname, b.port)


def redirect_permitted(url):
    try:
        parsed = urllib.parse.urlsplit(url)
    except ValueError:
        return False
    if parsed.username or parsed.password or parsed.fragment:
        return False
    host = parsed.hostname or ""
    if os.environ.get("CONTRACTSCRIBE_RELEASE_TEST") == "1" \
            and parsed.scheme == "http":
        return host in ("127.0.0.1", "::1") \
            and parsed.port not in (None, 80, 443)
    if parsed.scheme != "https" or parsed.port not in (None, 443):
        return False
    if host in ("github.com", "api.github.com"):
        return True
    return host.endswith(".githubusercontent.com")


def upload_url_permitted(url):
    try:
        parsed = urllib.parse.urlsplit(url)
    except ValueError:
        return False
    if parsed.username or parsed.password or parsed.fragment:
        return False
    host = parsed.hostname or ""
    if os.environ.get("CONTRACTSCRIBE_RELEASE_TEST") == "1" \
            and parsed.scheme == "http":
        return host in ("127.0.0.1", "::1") \
            and parsed.port not in (None, 80, 443)
    return parsed.scheme == "https" and host == UPLOAD_HOST_PRODUCTION \
        and parsed.port in (None, 443)


# ---------------------------------------------------------------------------
# Bounded HTTP
# ---------------------------------------------------------------------------

class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def _opener():
    return urllib.request.build_opener(
        _NoRedirect, urllib.request.ProxyHandler({}))


def http_class(code):
    if code in (401, 403):
        return "http-auth"
    if code == 404:
        return "http-not-found"
    if code in (409, 410, 422):
        return "http-conflict"
    if code == 429:
        return "http-rate-limit"
    if 500 <= code <= 599:
        return "http-server"
    if 400 <= code <= 499:
        return "http-client"
    return "http-error"


def api_headers(token, accept="application/vnd.github+json"):
    headers = {
        "Accept": accept,
        "User-Agent": "contract-scribe-release",
        "X-GitHub-Api-Version": "2026-03-10",
    }
    if token:
        headers["Authorization"] = "Bearer " + token
    return headers


class ReleaseHttp(Exception):
    def __init__(self, reason):
        super().__init__(reason)
        self.reason = reason


def _parse(data, what):
    if len(data) > BOUND_METADATA_JSON:
        raise ReleaseHttp(f"{what}-oversize")
    try:
        return strict_loads(data)
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError):
        raise ReleaseHttp(f"{what}-malformed") from None


def _request(method, url, token, what, body=None):
    data = None
    headers = api_headers(token)
    if body is not None:
        data = canonical_bytes(body)
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=data, headers=headers,
                                     method=method)
    try:
        with _opener().open(request, timeout=BOUND_CONNECT_SECONDS) as resp:
            return resp.status, _parse(resp.read(BOUND_METADATA_JSON + 1),
                                       what)
    except urllib.error.HTTPError as error:
        if error.code in (404, 410):
            return error.code, None
        raise ReleaseHttp(http_class(error.code)) from None
    except (urllib.error.URLError, TimeoutError, OSError):
        raise ReleaseHttp("http-unreachable") from None


def get_json(url, token, what):
    """Bounded GET: one extra attempt on pure transport loss (reads carry
    no mutation)."""
    try:
        return _request("GET", url, token, what)
    except ReleaseHttp as error:
        if error.reason != "http-unreachable":
            raise
        return _request("GET", url, token, what)


def list_json(url, token, what, key=None):
    """Bounded paginated enumeration (per_page=100, <=5 pages). Endpoints
    that wrap rows in {total_count, <key>} pass the member name as key."""
    rows = []
    sep = "&" if "?" in url else "?"
    for page in range(1, BOUND_MAX_PAGES + 1):
        status, doc = get_json(f"{url}{sep}per_page=100&page={page}",
                               token, what)
        if status != 200:
            raise ReleaseHttp(f"{what}-malformed")
        items = doc if key is None else \
            (doc.get(key) if isinstance(doc, dict) else None)
        if not isinstance(items, list):
            raise ReleaseHttp(f"{what}-malformed")
        rows.extend(items)
        if len(items) < 100:
            return rows
    raise ReleaseHttp(f"{what}-oversize")


def post_json(url, token, body, what):
    status, doc = _request("POST", url, token, what, body=body)
    if status not in (200, 201):
        raise ReleaseHttp(http_class(status))
    return doc


def patch_json(url, token, body, what):
    status, doc = _request("PATCH", url, token, what, body=body)
    if status != 200:
        raise ReleaseHttp(http_class(status))
    return doc


def upload_asset(url, token, file_path, size, what):
    """POST the archive bytes to the release's upload_url origin. The URL
    itself was validated against the upload-origin policy by the caller."""
    with open(file_path, "rb") as fh:
        data = fh.read(size)
    headers = api_headers(token, accept="application/vnd.github+json")
    headers["Content-Type"] = "application/octet-stream"
    headers["Content-Length"] = str(len(data))
    request = urllib.request.Request(url, data=data, headers=headers,
                                     method="POST")
    try:
        with _opener().open(request, timeout=BOUND_DOWNLOAD_SECONDS) as resp:
            return _parse(resp.read(BOUND_METADATA_JSON + 1), what)
    except urllib.error.HTTPError as error:
        if error.code in (404, 410):
            raise ReleaseHttp(http_class(error.code)) from None
        raise ReleaseHttp(http_class(error.code)) from None
    except (urllib.error.URLError, TimeoutError, OSError):
        raise ReleaseHttp("http-unreachable") from None


def download_asset(url, token, root, limit=BOUND_DOWNLOAD):
    """GET asset bytes with hop-by-hop redirect validation; Authorization
    is sent only to the API origin."""
    current = url
    hops = 0
    while True:
        headers = api_headers(
            token if is_api_origin(current, root) else None,
            accept="application/octet-stream")
        request = urllib.request.Request(current, headers=headers,
                                         method="GET")
        try:
            response = _opener().open(request, timeout=BOUND_CONNECT_SECONDS)
        except urllib.error.HTTPError as error:
            if error.code in (301, 302, 303, 307, 308):
                location = error.headers.get("Location")
                hops += 1
                if hops > BOUND_REDIRECTS or not location \
                        or not redirect_permitted(location):
                    raise ReleaseHttp("redirect-policy") from None
                current = location
                continue
            raise ReleaseHttp(http_class(error.code)) from None
        except (urllib.error.URLError, TimeoutError, OSError):
            raise ReleaseHttp("http-unreachable") from None
        break
    with response:
        declared = response.headers.get("Content-Length")
        if declared is not None:
            try:
                if int(declared) > limit:
                    raise ReleaseHttp("download-bound")
            except ValueError:
                raise ReleaseHttp("download-length") from None
        digest = hashlib.sha256()
        total = 0
        started = time.monotonic()
        while True:
            if time.monotonic() - started > BOUND_DOWNLOAD_SECONDS:
                raise ReleaseHttp("download-timeout")
            try:
                chunk = response.read(min(1 << 20, limit + 1 - total))
            except (TimeoutError, OSError, socket.timeout):
                raise ReleaseHttp("download-timeout") from None
            if not chunk:
                break
            total += len(chunk)
            digest.update(chunk)
            if total > limit:
                raise ReleaseHttp("download-bound")
        if declared is not None and total != int(declared):
            raise ReleaseHttp("download-truncated")
    return digest.hexdigest()


# ---------------------------------------------------------------------------
# Candidate loading / identity
# ---------------------------------------------------------------------------

def candidate_digest(identity):
    return hashlib.sha256(canonical_bytes(identity)).hexdigest()


def load_candidate(directory):
    path = os.path.join(directory, "candidate.json")
    document = load_json_file(path, "candidate")
    check("candidate", isinstance(document, dict)
          and document.get("candidateVersion") == 1
          and isinstance(document.get("identity"), dict)
          and isinstance(document.get("provenance"), dict),
          "candidate-shape")
    sidecar = os.path.join(directory, "candidate.sha256")
    check("candidate", os.path.isfile(sidecar), "candidate-sidecar")
    with open(sidecar, "rb") as fh:
        digest = fh.read(4096).decode("utf-8", "replace").split()[0]
    check("candidate", digest == candidate_digest(document["identity"]),
          "candidate-sidecar")
    return document


def tool_version(identity):
    name = identity["assetName"]
    prefix, suffix = "contract-scribe-", "-linux-x64.tar.gz"
    check("candidate", name.startswith(prefix) and name.endswith(suffix)
          and len(name) > len(prefix) + len(suffix), "asset-name")
    return name[len(prefix):-len(suffix)]


def expected_map_payload(identity):
    return {
        "repository": identity["repository"],
        "releaseTag": identity["releaseTag"],
        "toolVersion": tool_version(identity),
        "sourceRevision": identity["payloadSourceRevision"],
        "runtimeIdentifier": "linux-x64",
        "assetName": identity["assetName"],
        "sha256": identity["archiveSha256"],
        "assets": [identity["assetName"]],
    }


def run_git(repo, *args):
    env = dict(os.environ)
    env["GIT_TERMINAL_PROMPT"] = "0"
    proc = subprocess.run(["git", "-C", repo, *args], env=env,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          stdin=subprocess.DEVNULL)
    return proc.returncode, proc.stdout.decode("utf-8", "replace")


def release_body(identity):
    return (
        f"ContractScribe payload candidate for release "
        f"{identity['releaseVersion']}.\n\n"
        f"- toolVersion: {tool_version(identity)}\n"
        f"- payloadSourceRevision: {identity['payloadSourceRevision']}\n"
        f"- sourceRevision: {identity['sourceRevision']}\n"
        f"- archiveSha256: {identity['archiveSha256']}\n")


def release_matches(identity, release, draft_expected):
    """Exact-match predicate over every frozen release field."""
    if not isinstance(release, dict) or not isinstance(release.get("id"),
                                                      int):
        return False
    if release.get("draft") is not draft_expected:
        return False
    if release.get("prerelease") is not identity["prerelease"]:
        return False
    if release.get("tag_name") != identity["releaseTag"]:
        return False
    if release.get("name") != identity["releaseName"]:
        return False
    if release.get("target_commitish") != identity["payloadSourceRevision"]:
        return False
    body = release.get("body")
    if not isinstance(body, str):
        return False
    return hashlib.sha256(body.encode("utf-8")).hexdigest() \
        == identity["releaseBodySha256"]


def git_ref(root, tag):
    return (f"{root}/repos/{REPOSITORY}/git/ref/tags/"
            + urllib.parse.quote(tag, safe=""))


def release_token():
    token = os.environ.get("CONTRACTSCRIBE_RELEASE_TOKEN")
    check("credential", token is not None and "\x00" not in token,
          "token-missing")
    return token


def read_token():
    token = os.environ.get("CONTRACTSCRIBE_RELEASE_READ_TOKEN")
    check("credential", token is not None and "\x00" not in token,
          "read-token-missing")
    return token


# ---------------------------------------------------------------------------
# resolve-artifact — selector preflight before any download
# ---------------------------------------------------------------------------

def cmd_resolve_artifact(args):
    global _STAGE
    _STAGE = "resolve-artifact"
    stage = _STAGE
    read = read_token()
    root = api_root()
    repo_base = f"{root}/repos/{REPOSITORY}"

    run_id = args.candidate_run_id
    check(stage, isinstance(run_id, str) and run_id.isdigit(), "run-id")
    status, run = get_json(f"{repo_base}/actions/runs/{run_id}",
                           read, "run")
    check(stage, status == 200 and isinstance(run, dict)
          and run.get("event") == "workflow_dispatch"
          and run.get("path") == WORKFLOW_PATH
          and run.get("status") == "completed"
          and run.get("conclusion") == "success"
          and run.get("head_branch") == "main"
          and isinstance(run.get("repository"), dict)
          and run["repository"].get("full_name") == REPOSITORY,
          "run-readback")
    artifacts = list_json(f"{repo_base}/actions/runs/{run_id}/artifacts",
                          read, "artifacts", key="artifacts")
    matches = [a for a in artifacts
               if a.get("name") == ARTIFACT_NAME
               and a.get("expired") is False]
    check(stage, len(matches) == 1, "artifact-count")
    artifact = matches[0]
    check(stage, isinstance(artifact.get("id"), int)
          and isinstance(artifact.get("digest"), str)
          and artifact["digest"].startswith("sha256:")
          and _SHA256.fullmatch(artifact["digest"][len("sha256:"):]),
          "artifact-shape")
    write_output("artifact-id", str(artifact["id"]))
    write_output("artifact-digest", artifact["digest"])
    marker(stage, "ok", f"artifact={artifact['id']}")
    print(f"{artifact['id']}")


# ---------------------------------------------------------------------------
# verify-candidate — the shared pre-mutation gate
# ---------------------------------------------------------------------------

def verify_candidate(args):
    stage = "verify"
    candidate = load_candidate(args.candidate)
    identity = candidate["identity"]
    provenance = candidate["provenance"]

    digest = candidate_digest(identity)
    check(stage, digest == args.candidate_digest, "candidate-digest")
    check(stage, identity.get("sourceRevision") == args.source_revision
          and _HEX40.fullmatch(args.source_revision or ""),
          "source-revision")
    check(stage, identity.get("payloadSourceRevision")
          == args.payload_source_revision
          and _HEX40.fullmatch(args.payload_source_revision or ""),
          "payload-source-revision")
    check(stage, identity.get("releaseVersion") == args.release_version
          and _RELEASE_VERSION.fullmatch(args.release_version or ""),
          "release-version")
    check(stage, isinstance(identity.get("wrapper"), dict)
          and identity["wrapper"].get("name") == args.wrapper
          and _WRAPPER_NAME.fullmatch(args.wrapper or ""),
          "wrapper-name")
    check(stage, identity.get("repository") == REPOSITORY, "repository-name")
    check(stage, isinstance(identity.get("repositoryId"), str)
          and identity["repositoryId"].isdigit(), "repository-id")

    # Local artifact re-verification: the downloaded bytes must match the
    # frozen identity exactly.
    archive_path = os.path.join(args.candidate, identity["assetName"])
    check(stage, os.path.isfile(archive_path), "archive-missing")
    check(stage, sha256_file(archive_path) == identity["archiveSha256"],
          "archive-sha256")
    check(stage, os.lstat(archive_path).st_size == identity["archiveBytes"],
          "archive-bytes")
    manifest_path = archive_path[:-len(".tar.gz")] + ".payload.json"
    check(stage, os.path.isfile(manifest_path), "manifest-missing")
    check(stage, sha256_file(manifest_path) == identity["manifestSha256"],
          "manifest-sha256")

    read = read_token()
    root = api_root()
    repo_base = f"{root}/repos/{REPOSITORY}"

    status, repo_doc = get_json(repo_base, read, "repository")
    check(stage, status == 200 and isinstance(repo_doc, dict)
          and str(repo_doc.get("id")) == identity["repositoryId"]
          and repo_doc.get("full_name") == REPOSITORY,
          "repository-readback")

    run_id = args.candidate_run_id
    check(stage, isinstance(run_id, str) and run_id.isdigit(), "run-id")
    check(stage, provenance.get("runId") == run_id, "run-id-mismatch")
    status, run = get_json(f"{repo_base}/actions/runs/{run_id}",
                           read, "run")
    check(stage, status == 200 and isinstance(run, dict)
          and run.get("event") == "workflow_dispatch"
          and run.get("path") == WORKFLOW_PATH
          and run.get("status") == "completed"
          and run.get("conclusion") == "success"
          and run.get("head_branch") == "main"
          and run.get("head_sha") == provenance.get("sha")
          and str(run.get("run_attempt"))
          == str(provenance.get("runAttempt"))
          and isinstance(run.get("repository"), dict)
          and str(run["repository"].get("id")) == identity["repositoryId"]
          and isinstance(run.get("head_repository"), dict)
          and str(run["head_repository"].get("id"))
          == identity["repositoryId"],
          "run-readback")

    artifacts = list_json(f"{repo_base}/actions/runs/{run_id}/artifacts",
                          read, "artifacts", key="artifacts")
    matches = [a for a in artifacts
               if a.get("name") == ARTIFACT_NAME and a.get("expired") is False]
    check(stage, len(matches) == 1, "artifact-count")
    artifact = matches[0]
    check(stage, isinstance(artifact.get("digest"), str)
          and artifact["digest"].startswith("sha256:")
          and _SHA256.fullmatch(artifact["digest"][len("sha256:"):]),
          "artifact-digest")
    check(stage, isinstance(artifact.get("workflow_run"), dict)
          and str(artifact["workflow_run"].get("id")) == run_id
          and artifact["workflow_run"].get("head_sha")
          == provenance.get("sha")
          and str(artifact["workflow_run"].get("head_repository_id"))
          == identity["repositoryId"],
          "artifact-run")

    # Wrapper/map state AT source_revision (the published tag target), not
    # the dispatch checkout: the public tag must expose the authorized pair.
    code, action_text = run_git(args.repo, "show",
                                f"{args.source_revision}:action.yml")
    check(stage, code == 0, "action-unreadable")
    check(stage, hashlib.sha256(action_text.encode("utf-8")).hexdigest()
          == identity["wrapper"]["actionYmlSha256"], "action-sha256")
    action_name = None
    for line in action_text.splitlines():
        match = re.match(r"^name:\s*'?\"?([A-Za-z0-9_-]+)'?\"?\s*$", line)
        if match:
            action_name = match.group(1)
            break
    check(stage, action_name == identity["wrapper"]["actionYmlName"],
          "action-name")

    code, map_text = run_git(
        args.repo, "show",
        f"{args.source_revision}:scripts/action/payload-map.json")
    check(stage, code == 0, "map-unreadable")
    try:
        map_doc = strict_loads(map_text.encode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError):
        fail(stage, "map-malformed")
    check(stage, isinstance(map_doc, dict)
          and map_doc.get("mapVersion") == 1
          and map_doc.get("wrapper") == identity["wrapper"]["name"]
          and map_doc.get("payload") == expected_map_payload(identity),
          "map-pair")

    # Workflow revision: the candidate was produced by this exact workflow.
    check(stage, env_text("GITHUB_WORKFLOW_SHA") is not None
          and env_text("GITHUB_WORKFLOW_SHA")
          == provenance.get("workflowSha"), "workflow-revision")

    # Both revisions must remain ancestors of the current main (HEAD).
    for rev in (identity["sourceRevision"], identity["payloadSourceRevision"]):
        code, _ = run_git(args.repo, "merge-base", "--is-ancestor",
                          rev, "HEAD")
        check(stage, code == 0, "not-main-reachable")

    marker(stage, "ok", digest)
    return candidate


def common_args(parser):
    parser.add_argument("--candidate", required=True,
                        help="Directory holding the downloaded candidate")
    parser.add_argument("--repo", required=True,
                        help="Checkout used for git readbacks (dispatch ref)")
    parser.add_argument("--source-revision", required=True)
    parser.add_argument("--payload-source-revision", required=True)
    parser.add_argument("--release-version", required=True)
    parser.add_argument("--wrapper", required=True)
    parser.add_argument("--candidate-digest", required=True)
    parser.add_argument("--candidate-run-id", required=True)


# ---------------------------------------------------------------------------
# Release operations
# ---------------------------------------------------------------------------

def find_release(root, token, identity):
    """Draft-capable resolution: drafts are invisible to the by-tag route,
    so enumerate (bounded) and collect exact tag_name matches."""
    releases = list_json(f"{root}/repos/{REPOSITORY}/releases",
                         token, "releases")
    return [r for r in releases
            if r.get("tag_name") == identity["releaseTag"]]


def ref_state(root, token, tag):
    """Return (exists, sha) for refs/tags/<tag>; sha is the commit only
    when object.type == 'commit' (annotated tag objects fail closed)."""
    status, doc = get_json(git_ref(root, tag), token, "ref")
    if status == 404:
        return False, None
    if status != 200 or not isinstance(doc, dict):
        raise ReleaseHttp("ref-malformed")
    obj = doc.get("object")
    if not isinstance(obj, dict) or obj.get("type") != "commit":
        raise ReleaseHttp("ref-type")
    sha = obj.get("sha", "")
    if not _HEX40.fullmatch(sha):
        raise ReleaseHttp("ref-sha")
    return True, sha


def ensure_ref(root, token, tag, sha, stage):
    """Checked-absence or exact-match creation of a git tag ref. Never
    moves an existing ref."""
    exists, current = ref_state(root, token, tag)
    if exists:
        check(stage, current == sha, "ref-conflict")
        return
    try:
        post_json(f"{root}/repos/{REPOSITORY}/git/refs", token,
                  {"ref": f"refs/tags/{tag}", "sha": sha}, "ref-create")
    except ReleaseHttp as error:
        if error.reason in _AMBIGUOUS:
            # The write may have landed (or an exact object already
            # exists): adopt only the exact desired state, else stop.
            exists, current = ref_state(root, token, tag)
            check(stage, exists and current == sha, "ref-ambiguous")
            return
        fail(stage, error.reason)
    exists, current = ref_state(root, token, tag)
    check(stage, exists and current == sha, "ref-readback")


def asset_digest(root, token, asset, expected_sha, stage):
    """Verify one release asset against the frozen sha256: prefer the
    server-provided digest, else download and hash."""
    digest_field = asset.get("digest")
    if isinstance(digest_field, str) \
            and digest_field.startswith("sha256:") \
            and _SHA256.fullmatch(digest_field[len("sha256:"):]):
        check(stage, digest_field[len("sha256:"):] == expected_sha,
              "asset-digest")
        return
    url = asset.get("url")
    check(stage, isinstance(url, str)
          and is_api_origin(url, root), "asset-url")
    check(stage, download_asset(url, token, root) == expected_sha,
          "asset-digest")


def check_assets(root, token, identity, release, stage):
    """Read-only: the release's asset set must be exactly {assetName} with
    the frozen digest. Returns the asset id."""
    assets = list_json(
        f"{root}/repos/{REPOSITORY}/releases/{release['id']}/assets",
        token, "assets")
    expected = identity["assetName"]
    same = [a for a in assets if a.get("name") == expected]
    check(stage, len(same) == 1, "asset-missing")
    check(stage, not [a for a in assets if a.get("name") != expected],
          "asset-extra")
    asset = same[0]
    check(stage, isinstance(asset.get("id"), int)
          and asset.get("size") == identity["archiveBytes"]
          and asset["size"] > 0, "asset-size")
    asset_digest(root, token, asset, identity["archiveSha256"], stage)
    return asset["id"]


def converge_assets(root, token, identity, release, directory, stage):
    """Bring the release's asset set to exactly {assetName}. Anything
    unexpected is a conflict stop — no delete/replace ever."""
    assets = list_json(
        f"{root}/repos/{REPOSITORY}/releases/{release['id']}/assets",
        token, "assets")
    expected = identity["assetName"]
    same = [a for a in assets if a.get("name") == expected]
    check(stage, len(same) <= 1, "asset-duplicate")
    check(stage, not [a for a in assets if a.get("name") != expected],
          "asset-extra")
    if same:
        asset = same[0]
        check(stage, isinstance(asset.get("id"), int)
              and asset.get("size") == identity["archiveBytes"]
              and asset["size"] > 0, "asset-size")
        asset_digest(root, token, asset, identity["archiveSha256"], stage)
        return asset["id"]

    upload_url = release.get("upload_url")
    check(stage, isinstance(upload_url, str), "upload-url")
    upload_url = upload_url.split("{")[0]
    check(stage, upload_url_permitted(upload_url), "upload-origin")
    archive_path = os.path.join(directory, expected)
    url = (upload_url + ("&" if "?" in upload_url else "?")
           + "name=" + urllib.parse.quote(expected, safe=""))
    try:
        upload_asset(url, token, archive_path,
                     identity["archiveBytes"], "asset-upload")
    except ReleaseHttp as error:
        if error.reason in _AMBIGUOUS:
            # The upload may have landed: adopt only the exact desired
            # state (exactly one asset with the frozen digest), else stop.
            return check_assets(root, token, identity, release, stage)
        fail(stage, error.reason)
    return check_assets(root, token, identity, release, stage)


def cmd_stage_draft(args):
    global _STAGE
    _STAGE = "stage-draft"
    stage = _STAGE
    candidate = verify_candidate(args)
    identity = candidate["identity"]
    token = release_token()
    root = api_root()
    repo_base = f"{root}/repos/{REPOSITORY}"

    # No public tag may exist while the candidate is a draft — checked
    # before AND after staging (draft ref materialization is verified,
    # never assumed).
    exists, _ = ref_state(root, token, identity["releaseTag"])
    check(stage, not exists, "tag-present")
    exists, _ = ref_state(root, token, identity["releaseVersion"])
    check(stage, not exists, "tag-present")

    found = find_release(root, token, identity)
    check(stage, len(found) <= 1, "release-ambiguous")
    if found:
        release = found[0]
        check(stage, release_matches(identity, release, True),
              "release-conflict")
        recovered = True
    else:
        body = release_body(identity)
        check(stage, hashlib.sha256(body.encode("utf-8")).hexdigest()
              == identity["releaseBodySha256"], "body-sha256")
        try:
            release = post_json(f"{repo_base}/releases", token, {
                "tag_name": identity["releaseTag"],
                "target_commitish": identity["payloadSourceRevision"],
                "name": identity["releaseName"],
                "body": body,
                "draft": True,
                "prerelease": False,
            }, "release-create")
            check(stage, isinstance(release, dict)
                  and isinstance(release.get("id"), int),
                  "release-create")
            recovered = False
        except ReleaseHttp as error:
            if error.reason in _AMBIGUOUS:
                after = find_release(root, token, identity)
                check(stage, len(after) == 1
                      and release_matches(identity, after[0], True),
                      "release-ambiguous")
                release = after[0]
                recovered = True
            else:
                fail(stage, error.reason)

    asset_id = converge_assets(root, token, identity, release,
                               args.candidate, stage)

    exists, _ = ref_state(root, token, identity["releaseTag"])
    check(stage, not exists, "tag-created")
    exists, _ = ref_state(root, token, identity["releaseVersion"])
    check(stage, not exists, "tag-created")

    receipt = {
        "repositoryId": identity["repositoryId"],
        "candidateDigest": candidate_digest(identity),
        "releaseId": release["id"],
        "assetId": asset_id,
        "releaseTag": identity["releaseTag"],
        "assetName": identity["assetName"],
        "assetSha256": identity["archiveSha256"],
        "recovered": bool(recovered),
    }
    write_receipt(os.path.join(args.candidate, "staged.json"), receipt)
    write_output("staged-release-id", str(release["id"]))
    write_output("staged-asset-id", str(asset_id))
    marker(stage, "ok", f"release={release['id']}")


def cmd_verify_draft(args):
    global _STAGE
    _STAGE = "verify-draft"
    stage = _STAGE
    candidate = verify_candidate(args)
    identity = candidate["identity"]
    token = release_token()
    root = api_root()
    found = find_release(root, token, identity)
    check(stage, len(found) == 1, "release-missing")
    release = found[0]
    check(stage, release_matches(identity, release, True),
          "release-mismatch")
    asset_id = check_assets(root, token, identity, release, stage)
    if args.qualified_release_id is not None:
        check(stage, str(release["id"]) == args.qualified_release_id,
              "release-id")
        check(stage, str(asset_id) == args.qualified_asset_id,
              "asset-id")
    marker(stage, "ok", f"release={release['id']}")


def ci_gate(root, read, identity, stage):
    """Require a completed-success ci.yml run for head_sha==source_revision
    containing a successful action_packaged job."""
    runs = list_json(
        f"{root}/repos/{REPOSITORY}/actions/workflows/"
        f"{CI_WORKFLOW_PATH}/runs?head_sha={identity['sourceRevision']}"
        "&status=completed", read, "ci-runs", key="workflow_runs")
    for run in runs:
        if run.get("conclusion") != "success":
            continue
        jobs = list_json(
            f"{root}/repos/{REPOSITORY}/actions/runs/{run['id']}/jobs",
            read, "ci-jobs", key="jobs")
        good = [j for j in jobs
                if j.get("name") == REQUIRED_CI_JOB
                and j.get("status") == "completed"
                and j.get("conclusion") == "success"]
        if good:
            marker(stage + ".ci-gate", "ok", f"run={run['id']}")
            return {"runId": run["id"], "runAttempt": run.get("run_attempt")}
    fail(stage, "ci-gate")


def cmd_promote(args):
    global _STAGE
    _STAGE = "promote"
    stage = _STAGE
    candidate = verify_candidate(args)
    identity = candidate["identity"]
    read = read_token()
    token = release_token()
    root = api_root()
    repo_base = f"{root}/repos/{REPOSITORY}"

    check(stage, bool(args.approval_reference)
          and len(args.approval_reference) <= BOUND_REFERENCE,
          "approval-reference")
    check(stage, bool(args.governance_reference)
          and len(args.governance_reference) <= BOUND_REFERENCE,
          "governance-reference")
    check(stage, isinstance(args.qualified_release_id, str)
          and args.qualified_release_id.isdigit(), "release-id")
    check(stage, isinstance(args.qualified_asset_id, str)
          and args.qualified_asset_id.isdigit(), "asset-id")

    ci = ci_gate(root, read, identity, stage)

    found = find_release(root, token, identity)
    check(stage, len(found) == 1, "release-ambiguous")
    release = found[0]
    check(stage, str(release["id"]) == args.qualified_release_id,
          "release-id")
    # The staged draft or an already-published identical release (partial
    # recovery state) both qualify; anything else is a mismatch.
    draft_state = release.get("draft")
    check(stage, draft_state in (True, False)
          and release_matches(identity, release, bool(draft_state)),
          "release-mismatch")
    asset_id = check_assets(root, token, identity, release, stage)
    check(stage, str(asset_id) == args.qualified_asset_id, "asset-id")

    # Preflight BOTH tag refs before any mutation: absent or already exact
    # is fine, anything else fails closed with zero mutation.
    for tag, want in ((identity["releaseTag"],
                       identity["payloadSourceRevision"]),
                      (identity["releaseVersion"],
                       identity["sourceRevision"])):
        exists, current = ref_state(root, token, tag)
        check(stage, not exists or current == want, "ref-conflict")

    # Publish order: payload tag -> publish draft -> version tag last, so a
    # failure never leaves a resolvable consumer version without its
    # payload. Interruption at any point is a documented partial state that
    # a fresh dispatch recovers by exact-match adoption.
    ensure_ref(root, token, identity["releaseTag"],
               identity["payloadSourceRevision"], stage)

    if draft_state:
        try:
            patch_json(f"{repo_base}/releases/{release['id']}", token,
                       {"draft": False}, "release-publish")
        except ReleaseHttp as error:
            if error.reason in _AMBIGUOUS:
                after = find_release(root, token, identity)
                check(stage, len(after) == 1
                      and str(after[0]["id"]) == args.qualified_release_id
                      and release_matches(identity, after[0], False),
                      "release-ambiguous")
            else:
                fail(stage, error.reason)
        else:
            after = find_release(root, token, identity)
            check(stage, len(after) == 1
                  and str(after[0]["id"]) == args.qualified_release_id
                  and release_matches(identity, after[0], False),
                  "release-readback")

    ensure_ref(root, token, identity["releaseVersion"],
               identity["sourceRevision"], stage)

    evidence = {
        "repositoryId": identity["repositoryId"],
        "candidateDigest": candidate_digest(identity),
        "releaseId": release["id"],
        "assetId": asset_id,
        "releaseTag": identity["releaseTag"],
        "versionTag": identity["releaseVersion"],
        "releaseVersion": identity["releaseVersion"],
        "sourceRevision": identity["sourceRevision"],
        "payloadSourceRevision": identity["payloadSourceRevision"],
        "assetSha256": identity["archiveSha256"],
        "approvalReference": args.approval_reference,
        "governanceReference": args.governance_reference,
        "ciRun": ci,
        "promotionRunId": env_text("GITHUB_RUN_ID"),
        "promotionRunAttempt": env_text("GITHUB_RUN_ATTEMPT"),
    }
    write_receipt(os.path.join(args.candidate, "promotion.json"), evidence)
    marker(stage, "ok", f"release={release['id']}")


def main():
    parser = argparse.ArgumentParser(
        description="Guarded release-candidate operations.")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("resolve-artifact")
    p.add_argument("--candidate-run-id", required=True)
    p = sub.add_parser("verify-candidate")
    common_args(p)
    p = sub.add_parser("stage-draft")
    common_args(p)
    p = sub.add_parser("verify-draft")
    common_args(p)
    p.add_argument("--qualified-release-id", default=None)
    p.add_argument("--qualified-asset-id", default=None)
    p = sub.add_parser("promote")
    common_args(p)
    p.add_argument("--qualified-release-id", required=True)
    p.add_argument("--qualified-asset-id", required=True)
    p.add_argument("--approval-reference", required=True)
    p.add_argument("--governance-reference", required=True)

    args = parser.parse_args()
    try:
        {"resolve-artifact": cmd_resolve_artifact,
         "verify-candidate": lambda a: verify_candidate(a),
         "stage-draft": cmd_stage_draft,
         "verify-draft": cmd_verify_draft,
         "promote": cmd_promote}[args.command](args)
    except ReleaseHttp as error:
        fail(_STAGE, error.reason)


if __name__ == "__main__":
    main()
