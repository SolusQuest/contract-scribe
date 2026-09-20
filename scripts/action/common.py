#!/usr/bin/env python3
"""common.py — shared helpers for the ContractScribe composite Action.

Contract: docs/20_architecture/action-interface.md. Stdlib only.

Marker lines (the only structured log contract):
  action-acquire stage=<name> result=ok|fail reason=<short>
  action-cache   check=hit|miss result=ok|fail reason=<short>
  payload-install stage=<name> result=ok|fail reason=<short>   (frozen A1 set)

Failure convention: every stage failure prints its marker then exits nonzero.
The product entrypoint is never reached when a stage fails.
"""

import json
import os
import re
import stat
import urllib.error
import urllib.parse
import urllib.request

# --- Frozen payload bounds (ADR 0005 / payload.json consumer bounds) ---
BOUND_COMPRESSED = 256 * 1024 * 1024
BOUND_EXPANDED = 512 * 1024 * 1024
BOUND_FILE = 128 * 1024 * 1024
BOUND_COUNT = 4096
BOUND_PATH = 1024
BOUND_DEPTH = 16
BOUND_METADATA = 4 * 1024 * 1024

# --- Action transport bounds ---
BOUND_INLINE_OBJECT = 64 * 1024          # request / configuration-override inputs
BOUND_METADATA_JSON = 2 * 1024 * 1024    # release API documents
BOUND_STDOUT = 1024 * 1024               # captured CLI stdout
BOUND_STDERR = 4 * 1024                  # captured CLI stderr
BOUND_ENVELOPE = 16 * 1024               # the single product envelope object
BOUND_OUTPUT_VALUE = 1024                # each GITHUB_OUTPUT value
BOUND_SUMMARY = 4 * 1024                 # step summary
BOUND_PATH_INPUT = 4096                  # path-valued inputs
BOUND_MAX_PAGES = 5                      # release enumeration pages
BOUND_REDIRECTS = 3                      # download redirect chain
BOUND_DOWNLOAD_SECONDS = 300
BOUND_CONNECT_SECONDS = 25

SYNTHETIC_ACQUISITION_TOKEN = "contract-scribe-synthetic-acquisition-only"
SYNTHETIC_PRODUCT_TOKEN = "contract-scribe-synthetic-transport-only"

API_ROOT_PRODUCTION = "https://api.github.com"
DOWNLOAD_ORIGIN_HOSTS = ("github.com", "api.github.com")
DOWNLOAD_ORIGIN_SUFFIX = ".githubusercontent.com"

_SAFE_OUTPUT_NAME = re.compile(r"[a-z][a-z0-9_-]*")
_SAFE_ENV_NAME = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
_SAFE_ENV_VALUE = re.compile(r"[^\x00-\x1f\x7f]{1,4096}")
_HEX40 = re.compile(r"[0-9a-f]{40}")


class ActionFailure(Exception):
    """A bounded, marked failure: prints the stage marker then exits."""

    def __init__(self, marker_line, exit_code=1):
        super().__init__(marker_line)
        self.marker_line = marker_line
        self.exit_code = exit_code


def http_class(code):
    """HTTP status → the closed action.<stage>-<reason> vocabulary. Raw
    numeric codes are observations, not contract vocabulary."""
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


def marker(kind, stage, result, reason="-"):
    print(f"{kind} {stage} result={result} reason={reason}", flush=True)


def fail(kind, stage, reason, exit_code=1):
    marker(kind, stage, "fail", reason)
    # Any shared-helper failure inside a stage entrypoint still produces the
    # closed wrapper status so emit can classify it.
    owner = os.environ.get("CS_ACTION_STAGE")
    if owner and _SAFE_ENV_NAME.fullmatch(owner):
        write_output("action-status", f"action.{owner}-{reason}")
    raise SystemExit(exit_code)


def marker_kv(kind, fields, result, reason="-"):
    body = " ".join(fields)
    print(f"{kind} {body} result={result} reason={reason}", flush=True)


# ---------------------------------------------------------------------------
# Private filesystem helpers
# ---------------------------------------------------------------------------

_O_NOFOLLOW = getattr(os, "O_NOFOLLOW", 0)


def private_dir(path):
    """Create a 0700 private directory; it must not already exist."""
    os.mkdir(path, 0o700)
    os.chmod(path, 0o700)
    return path


def require_private_dir(path):
    info = os.lstat(path)
    if not stat.S_ISDIR(info.st_mode) or stat.S_IMODE(info.st_mode) & 0o077:
        fail("action-prepare", "stage=workdir", "private-directory")


def write_private(path, data, mode=0o600):
    """Write bytes to a new private file (O_EXCL, O_NOFOLLOW)."""
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | _O_NOFOLLOW, mode)
    with os.fdopen(fd, "wb") as out:
        out.write(data)
    return path


def read_bounded(path, limit, what="file"):
    info = os.lstat(path)
    if os.path.islink(path) or not stat.S_ISREG(info.st_mode):
        fail("action", "stage=read", f"{what}-not-regular")
    if info.st_size > limit:
        fail("action", "stage=read", f"{what}-oversize")
    with open(path, "rb") as fh:
        return fh.read()


def load_json_bytes(data, what):
    try:
        return json.loads(data.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        fail("action", "stage=parse", f"{what}-malformed")


def load_json_file(path, limit, what):
    return load_json_bytes(read_bounded(path, limit, what), what)


# ---------------------------------------------------------------------------
# Environment / inputs
# ---------------------------------------------------------------------------

def env_input(name):
    """Read a CS_INPUT_* value; absent or empty returns None."""
    value = os.environ.get(name)
    if value is None or value == "":
        return None
    if "\x00" in value:
        fail("action-prepare", "stage=input", "nul-byte")
    return value


def test_mode_enabled():
    return os.environ.get("CONTRACTSCRIBE_ACTION_TEST") == "1"


def test_api_root():
    """Return the test-only API root override under the strict test gate.

    The override is honored only when the complete gate holds; any partial or
    violating combination fails closed. It can never select a non-loopback
    origin or carry a non-synthetic credential.
    """
    root = os.environ.get("CONTRACTSCRIBE_ACTION_TEST_API_ROOT")
    if not test_mode_enabled():
        if root is not None or os.environ.get("CONTRACTSCRIBE_ACTION_TEST_MAP"):
            fail("action-prepare", "stage=test-gate", "test-env-without-mode")
        return None
    if root is None:
        return None
    if os.environ.get("GITHUB_ACTIONS") != "true":
        fail("action-prepare", "stage=test-gate", "outside-actions")
    if os.environ.get("GITHUB_REPOSITORY") != "SolusQuest/contract-scribe":
        fail("action-prepare", "stage=test-gate", "foreign-repository")
    parsed = urllib.parse.urlsplit(root)
    host = parsed.hostname or ""
    if (parsed.scheme != "http" or parsed.username or parsed.password
            or parsed.query or parsed.fragment
            or host not in ("127.0.0.1", "::1")
            or parsed.port is None or parsed.port in (80, 443)):
        fail("action-prepare", "stage=test-gate", "api-root-not-loopback")
    return root.rstrip("/")


def test_map_path():
    """Return the test-only map override under the strict test gate.

    Honored only when the API-root seam is also active (a test map can never
    aim at the production API); the path must be absolute and a regular file.
    Outside test mode any presence fails closed (see test_api_root)."""
    path = os.environ.get("CONTRACTSCRIBE_ACTION_TEST_MAP")
    if not test_mode_enabled() or path is None:
        return None
    if test_api_root() is None:
        fail("action-prepare", "stage=test-gate", "test-map-without-api-root")
    if not os.path.isabs(path) or os.path.islink(path) or not os.path.isfile(path):
        fail("action-prepare", "stage=test-gate", "test-map-invalid")
    return path


def api_root():
    return test_api_root() or API_ROOT_PRODUCTION


def redirect_permitted(url):
    """Validate one redirect/download URL under the route policy."""
    try:
        parsed = urllib.parse.urlsplit(url)
    except ValueError:
        return False
    if parsed.username or parsed.password or parsed.fragment:
        return False
    host = parsed.hostname or ""
    if test_mode_enabled() and parsed.scheme == "http":
        return host in ("127.0.0.1", "::1") and parsed.port not in (None, 80, 443)
    if parsed.scheme != "https" or parsed.port not in (None, 443):
        return False
    if host in DOWNLOAD_ORIGIN_HOSTS:
        return True
    return host.endswith(DOWNLOAD_ORIGIN_SUFFIX)


def is_api_origin(url, root):
    """True when url shares the scheme://host[:port] origin of the API root."""
    try:
        a = urllib.parse.urlsplit(root)
        b = urllib.parse.urlsplit(url)
    except ValueError:
        return False
    return (a.scheme, a.hostname, a.port) == (b.scheme, b.hostname, b.port)


# ---------------------------------------------------------------------------
# Bounded HTTP (no ambient proxy, no cookie store, manual redirect control)
# ---------------------------------------------------------------------------

class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def _opener():
    return urllib.request.build_opener(_NoRedirect, urllib.request.ProxyHandler({}))


def api_headers(token=None, accept="application/vnd.github+json"):
    headers = {
        "Accept": accept,
        "User-Agent": "contract-scribe-action",
        "X-GitHub-Api-Version": "2026-03-10",
    }
    if token:
        headers["Authorization"] = "Bearer " + token
    return headers


def get_json(url, token=None, what="metadata"):
    """GET one bounded JSON document. Redirects are never followed for API
    reads (the fixed API origin answers directly)."""
    request = urllib.request.Request(url, headers=api_headers(token), method="GET")
    try:
        with _opener().open(request, timeout=BOUND_CONNECT_SECONDS) as response:
            data = response.read(BOUND_METADATA_JSON + 1)
    except urllib.error.HTTPError as error:
        raise ActionFailure(http_class(error.code)) from None
    except (urllib.error.URLError, TimeoutError, OSError):
        raise ActionFailure("http-unreachable") from None
    if len(data) > BOUND_METADATA_JSON:
        raise ActionFailure(f"{what}-oversize")
    try:
        return json.loads(data.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise ActionFailure(f"{what}-malformed") from None


def download_asset(url, token, origin_root, sink, limit=BOUND_COMPRESSED):
    """Stream a release asset to `sink` (a binary file object).

    The asset API endpoint answers 200 (body) or 302 (signed storage URL).
    Authorization is sent only on the original API origin — never forwarded
    across a redirect. Every hop is validated by redirect_permitted().
    """
    current = url
    hops = 0
    while True:
        headers = api_headers(token if is_api_origin(current, origin_root) else None,
                              accept="application/octet-stream")
        request = urllib.request.Request(current, headers=headers, method="GET")
        try:
            response = _opener().open(request, timeout=BOUND_CONNECT_SECONDS)
        except urllib.error.HTTPError as error:
            if error.code in (301, 302, 303, 307, 308):
                location = error.headers.get("Location")
                hops += 1
                if hops > BOUND_REDIRECTS or not location:
                    raise ActionFailure("redirect-policy") from None
                if not redirect_permitted(location):
                    raise ActionFailure("redirect-origin") from None
                current = location
                continue
            raise ActionFailure(http_class(error.code)) from None
        except (urllib.error.URLError, TimeoutError, OSError):
            raise ActionFailure("http-unreachable") from None
        break
    import socket
    import time
    with response:
        declared = response.headers.get("Content-Length")
        if declared is not None:
            try:
                if int(declared) > limit:
                    raise ActionFailure("download-bound")
            except ValueError:
                raise ActionFailure("download-length") from None
        total = 0
        started = time.monotonic()
        while True:
            if time.monotonic() - started > BOUND_DOWNLOAD_SECONDS:
                raise ActionFailure("download-timeout")
            try:
                chunk = response.read(min(1 << 20, limit + 1 - total))
            except (TimeoutError, OSError, socket.timeout):
                raise ActionFailure("download-timeout") from None
            if not chunk:
                break
            total += len(chunk)
            if total > limit:
                raise ActionFailure("download-bound") from None
            sink.write(chunk)
        if declared is not None and total != int(declared):
            raise ActionFailure("download-truncated") from None
    return total


# ---------------------------------------------------------------------------
# GitHub step-output / env plumbing (fixed names, validated bounded values)
# ---------------------------------------------------------------------------

def write_output(name, value, limit=BOUND_OUTPUT_VALUE):
    if not _SAFE_OUTPUT_NAME.fullmatch(name):
        fail("action-emit", "stage=output", "output-name")
    if value is None:
        value = ""
    if not isinstance(value, str) or len(value) > limit \
            or "\x00" in value or "\r" in value:
        fail("action-emit", "stage=output", "output-value")
    path = os.environ.get("GITHUB_OUTPUT")
    if path:
        with open(path, "a", encoding="utf-8", newline="\n") as fh:
            if "\n" in value:
                # Multiline values use the delimiter form. The delimiter is a
                # wrapper-generated token; if it collides with content, suffix
                # until unique (closed-charset, bounded).
                delim = "CS_OUTPUT_DELIM"
                while delim in value:
                    delim += "X"
                fh.write(name + "<<" + delim + "\n" + value + "\n" + delim + "\n")
            else:
                fh.write(name + "=" + value + "\n")
    else:
        safe = value if "\n" not in value else value.replace("\n", "\\n")
        print(f"action-output {name}={safe}", flush=True)


def write_env(name, value):
    """$GITHUB_ENV carrier — only for wrapper-generated identifiers. The name
    is fixed code, the value carries no caller-controlled bytes and no control
    characters."""
    if not _SAFE_ENV_NAME.fullmatch(name) or not _SAFE_ENV_VALUE.fullmatch(value or ""):
        fail("action-prepare", "stage=env", "env-unsafe")
    path = os.environ.get("GITHUB_ENV")
    if path:
        with open(path, "a", encoding="utf-8", newline="\n") as fh:
            fh.write(name + "=" + value + "\n")


def annotate_error(text):
    """One bounded ::error:: annotation; closed characters only. The two
    parentheses are part of the frozen '<outcome> (exit <code>)' format."""
    clean = "".join(c for c in text if c.isascii() and (c.isalnum() or c in " ._:-()"))
    print(f"::error::{clean[:512]}", flush=True)


def _escape_workflow_command(value):
    """Percent-encode the bytes that would corrupt a workflow command."""
    return value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def mask(value):
    if value:
        print(f"::add-mask::{_escape_workflow_command(value)}", flush=True)


def add_path_line(path_value):
    path = os.environ.get("GITHUB_PATH")
    if path:
        with open(path, "a", encoding="utf-8", newline="\n") as fh:
            fh.write(path_value + "\n")


def work_dir():
    value = os.environ.get("CS_WORK_DIR")
    if not value or "\x00" in value or "\n" in value or "\r" in value:
        fail("action", "stage=workdir", "missing")
    return value


def work_file(name):
    return os.path.join(work_dir(), name)


def plan_path():
    return work_file("plan.json")


def load_plan():
    return load_json_file(plan_path(), BOUND_METADATA_JSON, "plan")


# ---------------------------------------------------------------------------
# Owned subprocesses — cancellation-safe child execution
# ---------------------------------------------------------------------------

def run_owned(argv, timeout=30):
    """Run a child in its own process group with cancellation forwarding.

    The step entry process receives the runner's signal; nested children must
    be owned explicitly: forward INT/TERM to the child's group, escalate after
    a bounded grace, and reap. Returns (returncode, stdout, stderr). Raises
    SystemExit(130) when a signal terminated the run.
    """
    import signal
    import subprocess
    import time

    child = subprocess.Popen(
        argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        stdin=subprocess.DEVNULL, start_new_session=True)
    state = {"signalled": False, "deadline": None}

    def on_signal(signum, _frame):
        state["signalled"] = True
        try:
            os.killpg(child.pid, signum)
        except (ProcessLookupError, PermissionError):
            pass  # group already gone
        state["deadline"] = time.monotonic() + 4.0

    previous = {}
    for sig in (signal.SIGINT, signal.SIGTERM):
        previous[sig] = signal.signal(sig, on_signal)
    try:
        deadline = time.monotonic() + timeout
        while child.poll() is None:
            if state["deadline"] is not None \
                    and time.monotonic() > state["deadline"]:
                try:
                    os.killpg(child.pid, signal.SIGKILL)
                except (ProcessLookupError, PermissionError):
                    pass  # group already gone
                state["deadline"] = None
            if time.monotonic() > deadline:
                try:
                    os.killpg(child.pid, signal.SIGKILL)
                except (ProcessLookupError, PermissionError):
                    pass  # group already gone
                break
            time.sleep(0.05)
        out, err = child.communicate()
    finally:
        for sig, handler in previous.items():
            signal.signal(sig, handler)
    rc = child.wait()
    if state["signalled"]:
        # run_owned replaced the owning stage's signal handler, so the stage
        # cannot report cancellation itself — write its cancelled status here
        # or the always-running emit step would report action-status=ok.
        stage = os.environ.get("CS_ACTION_STAGE")
        if stage and _SAFE_ENV_NAME.fullmatch(stage):
            write_output("action-status", f"action.{stage}-cancelled")
        raise SystemExit(130)
    return rc, out, err
