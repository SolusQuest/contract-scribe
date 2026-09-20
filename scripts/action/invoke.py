#!/usr/bin/env python3
"""invoke.py — run the installed ContractScribe CLI and export its result.

Runs as the step entry process (action.yml execs it), so the runner's
cancellation signals land here directly. The CLI runs in its own process
group (start_new_session); INT/TERM forward to that group, wait briefly,
then SIGKILL — inside the runner's 7.5s+2.5s grace window. The CLI's exit
code is preserved verbatim; its stdout is validated for the physical
envelope shape before any output is written. A malformed product stream is
a wrapper failure (action.envelope-*), never repaired or rerun.

The acquisition credential is scrubbed from the child environment; the
product credentials are masked before the child starts.
"""

import json
import os
import re
import signal
import subprocess
import sys
import threading
import time

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C

# Envelope keys per docs/20_architecture/github-proposal-cli.md. The
# wrapper validates physical shape only — these keys must be present so the
# outputs can be projected, but their values' product semantics are never
# re-judged here; extra keys are tolerated and pass through in `result`.
ENVELOPE_FIELDS = (
    "githubProposalEnvelopeVersion", "terminalLayer", "cliContractBaseline",
    "toolVersion", "campaignOperation", "publicationOperationId",
    "generationId", "outcome", "diagnosticCodes", "checkpointRevision",
    "pullRequestUrl", "publicationDiagnostic",
)

_PR_URL = re.compile(
    r"https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/pull/[1-9][0-9]*\Z")
_DIAG_CODE = re.compile(r"[a-z0-9]+([a-z0-9-]*[a-z0-9])?(\.[a-z0-9-]+)+")
_OUTCOME = re.compile(r"github-proposal\.[a-z0-9]+(-[a-z0-9]+)*\Z")


def fail_invoke(reason):
    C.marker("action-invoke", "stage=invoke", "fail", reason)
    C.write_output("action-status", f"action.invoke-{reason}")
    raise SystemExit(1)


def fail_envelope(reason):
    C.marker("action-invoke", "stage=envelope", "fail", reason)
    C.write_output("action-status", f"action.envelope-{reason}")
    raise SystemExit(1)


def _drain(stream, sink, cap, state):
    """Read the child stream into sink[:cap]; keep draining so the child
    never blocks, but record every byte beyond the cap as overflow."""
    total = 0
    while True:
        chunk = stream.read(65536)
        if not chunk:
            break
        if total < cap:
            take = min(cap - total, len(chunk))
            sink.extend(chunk[:take])
        total += len(chunk)
    state["overflow"] = max(0, total - cap)
    stream.close()


def run_cli(argv, env, cwd):
    child = subprocess.Popen(
        argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        stdin=subprocess.DEVNULL, env=env, cwd=cwd,
        start_new_session=True)
    out_buf, err_buf = bytearray(), bytearray()
    out_state, err_state = {"overflow": 0}, {"overflow": 0}
    threads = [
        threading.Thread(target=_drain,
                         args=(child.stdout, out_buf, C.BOUND_STDOUT,
                               out_state)),
        threading.Thread(target=_drain,
                         args=(child.stderr, err_buf, C.BOUND_STDERR,
                               err_state)),
    ]
    for thread in threads:
        thread.daemon = True
        thread.start()

    # One absolute escalation budget from first signal: forward INT/TERM,
    # TERM at +4s, KILL at +5.5s. The same deadline governs a descendant
    # that survives the root's exit — the handlers stay installed and the
    # group is supervised until it is gone, before any pipe-drain join.
    state = {"signalled": False, "phase": 0, "deadline": None}

    def _kill_group(sig):
        try:
            os.killpg(child.pid, sig)
        except (ProcessLookupError, PermissionError):
            pass  # process group already gone — nothing to signal

    def _group_alive():
        try:
            os.killpg(child.pid, 0)
            return True
        except (ProcessLookupError, PermissionError):
            return False

    def on_signal(signum, _frame):
        if state["phase"] == 0:
            state["signalled"] = True
            state["phase"] = 1
            _kill_group(signum)
            state["deadline"] = time.monotonic() + 4.0
        elif state["phase"] == 1:
            state["phase"] = 2
            _kill_group(signal.SIGTERM)
            state["deadline"] = time.monotonic() + 1.5
        else:
            _kill_group(signal.SIGKILL)

    previous_int = signal.signal(signal.SIGINT, on_signal)
    previous_term = signal.signal(signal.SIGTERM, on_signal)
    orphan = False
    rc = None
    try:
        while True:
            # Escalate on the single shared deadline: TERM at expiry, then
            # KILL 1.5s later, then a 1s verdict window. One timeline covers
            # both the live root and any descendant holding the group.
            if state["deadline"] is not None \
                    and time.monotonic() > state["deadline"]:
                if state["phase"] == 1:
                    state["phase"] = 2
                    _kill_group(signal.SIGTERM)
                    state["deadline"] = time.monotonic() + 1.5
                elif state["phase"] == 2:
                    state["phase"] = 3
                    _kill_group(signal.SIGKILL)
                    state["deadline"] = time.monotonic() + 1.0
                else:
                    orphan = True
                    break
            if child.poll() is None:
                time.sleep(0.05)
                continue
            if rc is None:
                rc = child.wait()
            if not _group_alive():
                break
            # Root exited but a descendant holds the group: bound it on the
            # same escalation timeline rather than waiting on held pipes.
            if state["phase"] == 0:
                state["phase"] = 1
                _kill_group(signal.SIGTERM)
                state["deadline"] = time.monotonic() + 2.0
            time.sleep(0.05)
    finally:
        signal.signal(signal.SIGINT, previous_int)
        signal.signal(signal.SIGTERM, previous_term)
    if rc is None:
        rc = child.poll()
        if rc is None:
            rc = -signal.SIGKILL  # unkillable root: bounded verdict only
    for thread in threads:
        thread.join(timeout=5)
    return (rc, bytes(out_buf), bytes(err_buf), state["signalled"], orphan,
            out_state["overflow"], err_state["overflow"])


def parse_envelope(stdout_bytes, stderr_bytes, rc):
    """Validate only the physical envelope shape the wrapper depends on to
    project outputs. Product semantics (outcome/exit/layer/diagnostic
    consistency, message texts) are the CLI's contract and are never
    re-judged here; extra keys are tolerated and pass through in `result`."""
    if not stdout_bytes:
        fail_envelope("missing")
    if len(stdout_bytes) > C.BOUND_ENVELOPE:
        fail_envelope("oversize")
    if not stdout_bytes.endswith(b"\n") or stdout_bytes.count(b"\n") != 1:
        fail_envelope("multiple-objects")
    try:
        envelope = json.loads(stdout_bytes[:-1].decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        fail_envelope("malformed")
    if not isinstance(envelope, dict) \
            or not set(ENVELOPE_FIELDS) <= set(envelope):
        fail_envelope("shape")
    v = envelope["githubProposalEnvelopeVersion"]
    if not isinstance(v, int) or isinstance(v, bool) or v != 1:
        fail_envelope("version")

    def _clean(text):
        return len(text) <= 512 and not any(
            ord(c) < 0x20 or ord(c) == 0x7F for c in text)

    def _str(name, optional=False):
        value = envelope[name]
        if value is None and optional:
            return
        if not isinstance(value, str) or (not value and not optional) \
                or not _clean(value):
            fail_envelope("field-" + name)

    for name in ("terminalLayer", "cliContractBaseline", "toolVersion"):
        _str(name)
    for name in ("campaignOperation", "publicationOperationId", "generationId"):
        _str(name, optional=True)
    outcome = envelope["outcome"]
    if not isinstance(outcome, str) or not _OUTCOME.fullmatch(outcome):
        fail_envelope("outcome-shape")
    codes = envelope["diagnosticCodes"]
    if not isinstance(codes, list) or len(codes) > 16 \
            or not all(isinstance(c, str) and len(c) <= 96
                       and _DIAG_CODE.fullmatch(c) for c in codes):
        fail_envelope("diagnostic-codes")
    rev = envelope["checkpointRevision"]
    if rev is not None and (isinstance(rev, bool) or not isinstance(rev, int)):
        fail_envelope("field-checkpointRevision")
    url = envelope["pullRequestUrl"]
    if url is not None \
            and (not isinstance(url, str) or not _PR_URL.fullmatch(url)):
        fail_envelope("pull-request-url")
    diag = envelope["publicationDiagnostic"]
    if diag is not None and not isinstance(diag, dict):
        fail_envelope("publication-diagnostic-shape")

    # stderr: physical shape only — success is silent, a controlled failure
    # carries at most one bounded control-free line whose text is
    # product-owned and never interpreted here.
    if stderr_bytes:
        if rc == 0 or stderr_bytes.count(b"\n") != 1 \
                or not stderr_bytes.endswith(b"\n"):
            fail_envelope("stderr-shape")
        try:
            line = stderr_bytes[:-1].decode("utf-8")
        except UnicodeDecodeError:
            fail_envelope("stderr-encoding")
        if not _clean(line):
            fail_envelope("stderr-bound")
    return envelope


def emit_outputs(envelope, stdout_bytes, rc):
    """Lossless product outputs: the complete envelope verbatim (including
    the contract's trailing LF) plus exact convenience copies of every
    field. Empty when the value is absent."""
    C.write_output("result", stdout_bytes.decode("utf-8", "replace"),
                   limit=C.BOUND_ENVELOPE)
    C.write_output("outcome", envelope["outcome"])
    C.write_output("exit-code", str(rc))
    C.write_output("terminal-layer", envelope["terminalLayer"])
    C.write_output("diagnostic-codes", ",".join(envelope["diagnosticCodes"]))
    C.write_output("checkpoint-revision",
                   "" if envelope["checkpointRevision"] is None
                   else str(envelope["checkpointRevision"]))
    C.write_output("pull-request-url", envelope["pullRequestUrl"] or "")
    C.write_output("tool-version", envelope["toolVersion"])
    C.write_output("campaign-operation", envelope["campaignOperation"] or "")
    C.write_output("publication-operation-id",
                   envelope["publicationOperationId"] or "")
    C.write_output("generation-id", envelope["generationId"] or "")
    C.write_output("publication-diagnostic",
                   "" if envelope["publicationDiagnostic"] is None
                   else json.dumps(envelope["publicationDiagnostic"],
                                   separators=(",", ":")))


def main():
    os.environ["CS_ACTION_STAGE"] = "invoke"
    plan = C.load_plan()
    # The acquisition record must exist and parse before invocation.
    C.load_json_file(C.work_file("acquired.json"),
                     C.BOUND_METADATA_JSON, "acquired")
    root = plan["installRoot"]
    current = os.path.join(root, "current")
    entry = os.path.join(current, "ContractScribe.Cli.dll")
    if not os.path.islink(current) or not os.path.isfile(entry):
        fail_invoke("not-installed")

    C.mask(os.environ.get("CONTRACTSCRIBE_PROVIDER_API_KEY"))
    C.mask(os.environ.get("CONTRACTSCRIBE_GITHUB_TOKEN"))

    argv = [plan["dotnet"], entry, "github-proposal", plan["operation"],
            "--repository-root", plan["repositoryRoot"],
            "--input", plan["input"],
            "--policy", plan["policy"],
            "--request", plan["request"]]
    if plan.get("configuration"):
        argv += ["--configuration", plan["configuration"]]
    if plan.get("configurationOverride"):
        argv += ["--configuration-override", plan["configurationOverride"]]

    # Child environment: inherited ambient variables are not authority — the
    # documented credential channels are set only from the owning step env.
    # Empty values are dropped so "input absent" reaches the CLI as "unset".
    env = dict(os.environ)
    env.pop("CONTRACTSCRIBE_ACQUISITION_TOKEN", None)
    for name in ("CONTRACTSCRIBE_PROVIDER_API_KEY", "CONTRACTSCRIBE_GITHUB_TOKEN"):
        if not env.get(name):
            env.pop(name, None)

    (rc, stdout_b, stderr_b, signalled, orphan,
     out_over, err_over) = run_cli(argv, env, plan["cwd"])
    if orphan:
        fail_invoke("orphan-survived")
    if out_over or err_over:
        fail_envelope("oversize")

    try:
        envelope = parse_envelope(stdout_b, stderr_b, rc)
    except SystemExit:
        # Cancellation before a valid envelope is wrapper cancellation (130);
        # a legitimately emitted envelope is still preserved below.
        if signalled:
            C.marker("action-invoke", "stage=invoke", "cancelled")
            C.write_output("action-status", "action.invoke-cancelled")
            raise SystemExit(130)
        raise

    # A validated controlled-failure diagnostic line is preserved as
    # diagnostic-only on wrapper stderr — never as an annotation.
    if stderr_b:
        sys.stderr.write(stderr_b.decode("utf-8", "replace"))
        sys.stderr.flush()

    emit_outputs(envelope, stdout_b, rc)
    C.write_output("action-status", "ok")
    C.marker("action-invoke", "stage=invoke", "ok")

    # Cancellation semantics: a validly emitted envelope is preserved
    # verbatim — the CLI's exit code is returned regardless of whether the
    # step was signalled. `signalled` maps to 130 only when no valid
    # envelope exists (the except-SystemExit branch above).
    raise SystemExit(rc)


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:
        try:
            fail_invoke("exception")
        except Exception:
            raise SystemExit(1)
