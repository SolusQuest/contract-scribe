#!/usr/bin/env python3
"""invoke.py — run the installed ContractScribe CLI and export its result.

Runs as the step entry process (action.yml execs it), so the runner's
cancellation signals land here directly. The CLI runs in its own process
group (start_new_session); INT/TERM forward to that group, wait briefly,
then SIGKILL — inside the runner's 7.5s+2.5s grace window. The CLI's exit
code is preserved verbatim; its stdout is validated as the documented
single-envelope contract before any output is written. A malformed product
stream is a wrapper failure (action.envelope-*), never repaired or rerun.

The acquisition credential is scrubbed from the child environment; the
product credentials are masked before the child starts.
"""

import json
import os
import signal
import subprocess
import sys
import threading
import time

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C

ENVELOPE_FIELDS = {
    "githubProposalEnvelopeVersion": int,
    "terminalLayer": str,
    "cliContractBaseline": str,
    "toolVersion": str,
    "campaignOperation": str,
    "publicationOperationId": str,
    "generationId": str,
    "outcome": str,
    "diagnosticCodes": list,
    "checkpointRevision": object,      # int | null
    "pullRequestUrl": object,          # str | null
    "publicationDiagnostic": object,   # object | null
}

# outcome -> permitted exit codes, per the github-proposal contract.
OUTCOME_EXIT = {
    "github-proposal.published": (0,),
    "github-proposal.replayed": (0,),
    "github-proposal.no-work": (0,),
    "github-proposal.awaiting-review": (0,),
    "github-proposal.merged": (0,),
    "github-proposal.conflict": (3,),
    "github-proposal.permission": (4,),
    "github-proposal.local-invalid": (4,),
    "github-proposal.preflight": (4,),
    "github-proposal.failed": (5,),
    "github-proposal.cancelled": (6,),
    "github-proposal.timeout": (7,),
    "github-proposal.contract-error": (1, 5),
}


def fail_invoke(reason):
    C.marker("action-invoke", "stage=invoke", "fail", reason)
    C.write_output("action-status", f"action.invoke-{reason}")
    raise SystemExit(1)


def fail_envelope(reason):
    C.marker("action-invoke", "stage=envelope", "fail", reason)
    C.write_output("action-status", f"action.envelope-{reason}")
    raise SystemExit(1)


def _drain(stream, sink, cap):
    """Read the child stream into sink[:cap]; keep draining overflow so the
    child never blocks on a full pipe."""
    total = 0
    while True:
        chunk = stream.read(65536)
        if not chunk:
            break
        if total < cap:
            take = min(cap - total, len(chunk))
            sink.extend(chunk[:take])
            total += take
    stream.close()


def run_cli(argv, env, cwd):
    child = subprocess.Popen(
        argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        stdin=subprocess.DEVNULL, env=env, cwd=cwd,
        start_new_session=True)
    out_buf, err_buf = bytearray(), bytearray()
    threads = [
        threading.Thread(target=_drain,
                         args=(child.stdout, out_buf, C.BOUND_STDOUT)),
        threading.Thread(target=_drain,
                         args=(child.stderr, err_buf, C.BOUND_STDERR)),
    ]
    for thread in threads:
        thread.daemon = True
        thread.start()

    # Escalation budget inside the runner's 7.5s (INT) + 2.5s (TERM) grace:
    # forward the received signal, wait <=4s, then TERM, then KILL at ~5.5s.
    state = {"signalled": False, "phase": 0, "deadline": None}

    def _kill_group(sig):
        try:
            os.killpg(child.pid, sig)
        except (ProcessLookupError, PermissionError):
            pass

    def on_signal(signum, _frame):
        now = time.monotonic()
        if state["phase"] == 0:
            state["signalled"] = True
            state["phase"] = 1
            _kill_group(signum)
            state["deadline"] = now + 4.0
        elif state["phase"] == 1:
            state["phase"] = 2
            _kill_group(signal.SIGTERM)
            state["deadline"] = now + 1.5
        else:
            _kill_group(signal.SIGKILL)

    previous_int = signal.signal(signal.SIGINT, on_signal)
    previous_term = signal.signal(signal.SIGTERM, on_signal)
    try:
        while child.poll() is None:
            if state["deadline"] is not None \
                    and time.monotonic() > state["deadline"]:
                if state["phase"] == 1:
                    state["phase"] = 2
                    _kill_group(signal.SIGTERM)
                    state["deadline"] = time.monotonic() + 1.5
                else:
                    _kill_group(signal.SIGKILL)
                    state["phase"] = 3
                    state["deadline"] = None
            time.sleep(0.05)
        rc = child.wait()
    finally:
        signal.signal(signal.SIGINT, previous_int)
        signal.signal(signal.SIGTERM, previous_term)
    for thread in threads:
        thread.join(timeout=5)
    # No owned process group may survive the wrapper.
    try:
        os.killpg(child.pid, 0)
        orphan = True
    except (ProcessLookupError, PermissionError):
        orphan = False
    return rc, bytes(out_buf), bytes(err_buf), state["signalled"], orphan


def parse_envelope(stdout_bytes, stderr_bytes, rc):
    """Validate the documented physical output shape; return the envelope."""
    if not stdout_bytes:
        fail_envelope("missing")
    if len(stdout_bytes) > C.BOUND_ENVELOPE:
        fail_envelope("oversize")
    if not stdout_bytes.endswith(b"\n") or stdout_bytes.count(b"\n") != 1:
        fail_envelope("multiple-objects")
    if stderr_bytes and (stderr_bytes.count(b"\n") != 1
                         or not stderr_bytes.endswith(b"\n")):
        fail_envelope("stderr-shape")
    envelope = C.load_json_bytes(stdout_bytes[:-1], "envelope")
    if not isinstance(envelope, dict) or set(envelope) != set(ENVELOPE_FIELDS):
        fail_envelope("shape")
    for field, kind in ENVELOPE_FIELDS.items():
        value = envelope[field]
        if kind is object:
            if field == "checkpointRevision" and \
                    value is not None and not isinstance(value, int):
                fail_envelope("field-type")
            if field == "pullRequestUrl" and \
                    value is not None and not isinstance(value, str):
                fail_envelope("field-type")
            if field == "publicationDiagnostic" and \
                    value is not None and not isinstance(value, dict):
                fail_envelope("field-type")
        elif not isinstance(value, kind):
            fail_envelope("field-type")
    if envelope["githubProposalEnvelopeVersion"] != 1:
        fail_envelope("version")
    if not all(isinstance(c, str) for c in envelope["diagnosticCodes"]):
        fail_envelope("field-type")
    permitted = OUTCOME_EXIT.get(envelope["outcome"])
    if permitted is None:
        fail_envelope("outcome-unknown")
    if rc not in permitted:
        fail_envelope("exit-mismatch")
    if rc == 0 and stderr_bytes:
        fail_envelope("stderr-shape")
    return envelope


def emit_outputs(envelope, rc):
    """Lossless product outputs: the complete envelope verbatim plus exact
    convenience copies of every field. Empty when the value is absent."""
    C.write_output("result", envelope["_raw"], limit=C.BOUND_ENVELOPE)
    C.write_output("outcome", envelope["outcome"])
    C.write_output("exit-code", str(rc))
    C.write_output("terminal-layer", envelope["terminalLayer"])
    C.write_output("diagnostic-codes", ",".join(envelope["diagnosticCodes"]))
    C.write_output("checkpoint-revision",
                   "" if envelope["checkpointRevision"] is None
                   else str(envelope["checkpointRevision"]))
    C.write_output("pull-request-url", envelope["pullRequestUrl"] or "")
    C.write_output("tool-version", envelope["toolVersion"])
    C.write_output("campaign-operation", envelope["campaignOperation"])
    C.write_output("publication-operation-id",
                   envelope["publicationOperationId"])
    C.write_output("generation-id", envelope["generationId"])
    C.write_output("publication-diagnostic",
                   "" if envelope["publicationDiagnostic"] is None
                   else json.dumps(envelope["publicationDiagnostic"],
                                   separators=(",", ":")))


def main():
    plan = C.load_plan()
    acquired = C.load_json_file(C.work_file("acquired.json"),
                                C.BOUND_METADATA_JSON, "acquired")
    version = "contract-scribe-" + acquired["payload"]["toolVersion"] \
        + "-linux-x64"
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

    env = dict(os.environ)
    env.pop("CONTRACTSCRIBE_ACQUISITION_TOKEN", None)

    rc, stdout_b, stderr_b, signalled, orphan = run_cli(
        argv, env, plan["cwd"])
    if orphan:
        fail_invoke("orphan-survived")

    envelope = parse_envelope(stdout_b, stderr_b, rc)
    envelope["_raw"] = stdout_b[:-1].decode("utf-8", "replace")
    emit_outputs(envelope, rc)
    C.write_output("action-status", "ok")
    C.marker("action-invoke", "stage=invoke", "ok")

    # Cancellation semantics: the product's own cancelled envelope (exit 6)
    # is preserved verbatim; a signal that killed the child before it could
    # report maps to 130.
    if signalled and rc != 6:
        raise SystemExit(130)
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
