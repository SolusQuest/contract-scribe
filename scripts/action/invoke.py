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
import re
import signal
import subprocess
import sys
import threading
import time

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C

# Envelope contract per docs/20_architecture/github-proposal-cli.md:
# exactly these fields, in this order; explicit nulls where unavailable.
ENVELOPE_FIELDS = (
    "githubProposalEnvelopeVersion", "terminalLayer", "cliContractBaseline",
    "toolVersion", "campaignOperation", "publicationOperationId",
    "generationId", "outcome", "diagnosticCodes", "checkpointRevision",
    "pullRequestUrl", "publicationDiagnostic",
)
TERMINAL_LAYERS = ("usage", "preflight", "campaign", "publication",
                   "presentation")
CAMPAIGN_OPERATIONS = ("start", "resume")

# outcome suffix (github-proposal.<suffix>) -> permitted exit codes,
# taken verbatim from GitHubProposalPresentation.Exit. usage-layer failures
# emit outcome local-invalid with exit 2; admitted and recovered-*-partial
# are diagnostic codes, never outcome values.
OUTCOME_EXIT = {
    "published": (0,), "replayed": (0,), "no-op": (0,),
    "awaiting-review": (0,), "merged": (0,),
    "stale-base-after-create": (3,), "rate-limit": (3,), "conflict": (3,),
    "local-invalid": (2, 4), "stale": (4,), "human-change": (4,),
    "permission": (4,), "closed-unmerged": (4,),
    "host-failure": (5,),
    "cancelled": (6,),
    "timeout": (7,),
}

# Closed publication-diagnostic vocabularies (C# enum member names are the
# wire values verbatim). An undefined member suppresses the whole diagnostic
# upstream; the wrapper enforces the same closed sets.
DIAG_BOUNDARY = {
    "Reconcile", "Repository", "CoordinationRead", "CoordinationClaim",
    "CoordinationRecord", "CoordinationAdvanceStale",
    "CoordinationAdvanceContent", "CoordinationAdvanceRef", "GitInspect",
    "GitInspectPredecessor", "GitPrepare", "GitCreateContent",
    "GitAdvanceRef", "PullRequestPreflight", "PullRequestObserve",
    "PullRequestCreate", "PullRequestRecover",
}
DIAG_OWNER = {
    "Reconciler", "Transport", "Coordination", "GitData", "PullRequests",
}
DIAG_COORDINATION_FAILURE = {
    "InvalidInput", "MissingPredecessor", "DifferentOperation",
    "StageConflict", "TargetMoved", "HumanChange", "Conflict",
    "ObjectMismatch", "Bounds", "Unresolved", "Transport",
}
DIAG_PROPOSAL_FAILURE = {
    "InvalidInput", "Integrity", "Bounds", "Conflict", "Unresolved",
    "Transport",
}
DIAG_PR_OUTCOME = {
    "Absent", "Appendable", "HeldDraft", "Ready", "Merged",
    "ClosedUnmerged", "StaleDraft", "Conflict", "Unresolved", "Failed",
}
DIAG_TRANSPORT_CODE = {
    "InvalidRequest", "Authentication", "Permission", "NotFound",
    "Conflict", "Validation", "RateLimit", "Cancelled", "Timeout",
    "ResponseLost", "InvalidResponse", "HostFailure",
}
DIAG_DELIVERY = {"NotDispatched", "Read", "NeedsReadback", "Ambiguous"}
DIAG_OBJECT_KIND = {"Blob", "Tree", "Commit"}
DIAG_PREDICATE = {
    "InvalidCorrelation", "Cancelled", "UnhandledException",
    "RepositoryUnavailable", "DifferentOperation", "TargetMoved",
    "SuccessorMismatch", "AppendMismatch", "TransitionMismatch",
    "UnexpectedProposalRef", "ClaimMismatch", "CurrentMismatch",
    "ClaimedRefPresent", "StaleStage", "UnexpectedStage",
    "AppendCreateForbidden", "CompletionHeadChanged", "ObservationLimit",
    "LifecycleOutcome", "MissingOrUnexpectedComponent",
}

_PR_URL = re.compile(
    r"https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/pull/[1-9][0-9]*\Z")
_DIAG_CODE = re.compile(r"[a-z0-9]+([a-z0-9-]*[a-z0-9])?(\.[a-z0-9-]+)+")

PUBLICATION_DIAGNOSTIC_FIELDS = (
    "boundary", "owner", "coordinationFailure", "proposalFailure",
    "pullRequestOutcome", "transportCode", "transportHttpStatus",
    "delivery", "recoveryCode", "recoveryHttpStatus", "objectKind",
    "predicate",
)


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
    # Supervise the whole owned process group, not just the root: a
    # descendant that survives the root's exit is still bounded — TERM
    # once, then KILL — before the orphan verdict is reported.
    orphan = False
    group_deadline = time.monotonic() + 4.0
    while True:
        try:
            os.killpg(child.pid, 0)
        except (ProcessLookupError, PermissionError):
            break
        if time.monotonic() > group_deadline:
            orphan = True
            break
        _kill_group(signal.SIGTERM if time.monotonic()
                    < group_deadline - 2.0 else signal.SIGKILL)
        time.sleep(0.05)
    return (rc, bytes(out_buf), bytes(err_buf), state["signalled"], orphan,
            out_state["overflow"], err_state["overflow"])


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
    try:
        envelope = json.loads(stdout_bytes[:-1].decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        fail_envelope("malformed")
    if not isinstance(envelope, dict) \
            or list(envelope.keys()) != list(ENVELOPE_FIELDS):
        fail_envelope("shape")
    v = envelope["githubProposalEnvelopeVersion"]
    if not isinstance(v, int) or isinstance(v, bool) or v != 1:
        fail_envelope("version")
    if envelope["terminalLayer"] not in TERMINAL_LAYERS:
        fail_envelope("layer")
    for name in ("cliContractBaseline", "toolVersion"):
        if not isinstance(envelope[name], str) or not envelope[name]:
            fail_envelope("field-" + name)
    op = envelope["campaignOperation"]
    if op is not None and op not in CAMPAIGN_OPERATIONS:
        fail_envelope("field-campaignOperation")
    for name in ("publicationOperationId", "generationId"):
        if envelope[name] is not None \
                and not isinstance(envelope[name], str):
            fail_envelope("field-" + name)
    codes = envelope["diagnosticCodes"]
    if not isinstance(codes, list) or len(codes) > 16 \
            or not all(isinstance(c, str) and len(c) <= 96
                       and _DIAG_CODE.fullmatch(c) for c in codes):
        fail_envelope("diagnostic-codes")
    rev = envelope["checkpointRevision"]
    if rev is not None and (not isinstance(rev, int) or isinstance(rev, bool)):
        fail_envelope("field-checkpointRevision")
    url = envelope["pullRequestUrl"]
    if url is not None \
            and (not isinstance(url, str) or not _PR_URL.fullmatch(url)):
        fail_envelope("pull-request-url")
    diag = envelope["publicationDiagnostic"]
    if diag is not None:
        if not isinstance(diag, dict) \
                or list(diag.keys()) != list(PUBLICATION_DIAGNOSTIC_FIELDS) \
                or diag["boundary"] not in DIAG_BOUNDARY \
                or diag["owner"] not in DIAG_OWNER:
            fail_envelope("publication-diagnostic-shape")
        enum_fields = (
            ("coordinationFailure", DIAG_COORDINATION_FAILURE),
            ("proposalFailure", DIAG_PROPOSAL_FAILURE),
            ("pullRequestOutcome", DIAG_PR_OUTCOME),
            ("transportCode", DIAG_TRANSPORT_CODE),
            ("delivery", DIAG_DELIVERY),
            ("recoveryCode", DIAG_TRANSPORT_CODE),
            ("objectKind", DIAG_OBJECT_KIND),
            ("predicate", DIAG_PREDICATE),
        )
        for field, vocabulary in enum_fields:
            if diag[field] is not None and diag[field] not in vocabulary:
                fail_envelope("publication-diagnostic-enum")
        for status_key in ("transportHttpStatus", "recoveryHttpStatus"):
            status = diag[status_key]
            if status is not None and (not isinstance(status, int)
                                       or isinstance(status, bool)
                                       or not 100 <= status <= 599):
                fail_envelope("publication-diagnostic")
    outcome = envelope["outcome"]
    if not isinstance(outcome, str) \
            or not outcome.startswith("github-proposal.") \
            or OUTCOME_EXIT.get(outcome[len("github-proposal."):]) is None:
        fail_envelope("outcome-unknown")
    permitted = OUTCOME_EXIT[outcome[len("github-proposal."):]]
    if rc not in permitted:
        fail_envelope("exit-mismatch")
    # usage-layer results are the only exit-2 producers and always carry
    # local-invalid; publicationDiagnostic exists only on failure paths.
    if (envelope["terminalLayer"] == "usage") != (rc == 2):
        fail_envelope("layer-exit")
    if envelope["terminalLayer"] == "usage" \
            and outcome != "github-proposal.local-invalid":
        fail_envelope("layer-exit")
    if rc == 0 and diag is not None:
        fail_envelope("diagnostic-on-success")
    if rc == 0 and stderr_bytes:
        fail_envelope("stderr-shape")
    # A controlled stderr diagnostic is one bounded line whose leading code
    # is one of the envelope's declared diagnostic codes — never raw product
    # bytes, never arbitrary text.
    if stderr_bytes:
        line = stderr_bytes[:-1].decode("utf-8", "strict")
        code, sep, message = line.partition(": ")
        if not sep or code not in envelope["diagnosticCodes"] \
                or len(line) > 512 or not all(0x20 <= ord(c) <= 0x7E
                                              for c in message):
            fail_envelope("stderr-content")
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

    # Cancellation semantics: the product's own cancelled envelope (exit 6)
    # is preserved verbatim; a signal that killed the child after it emitted
    # a valid non-cancelled result maps to 130.
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
