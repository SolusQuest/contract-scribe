#!/usr/bin/env python3
"""emit.py — merge step results into the Action's public outputs/summary.

Always runs (`if: always()`). Inputs: the upstream steps' `action_status`
outputs plus every product output the invoke step exported (forwarded via
CS_OUT_* env). Produces the Action's declared outputs, a bounded fixed-format
step summary, and exactly one ::error:: annotation on failure. Never invents
a product outcome: with no valid product envelope, product outputs stay
empty and only action_status describes the failure.
"""

import os
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C

PRODUCT_OUTPUTS = (
    "result", "outcome", "exit-code", "terminal-layer", "diagnostic-codes",
    "checkpoint-revision", "pull-request-url", "tool-version",
    "campaign-operation", "publication-operation-id", "generation-id",
    "publication-diagnostic",
)
WRAPPER_OUTPUTS = ("payload-version", "payload-sha256", "install-dir")

STATUS_ORDER = ("cs_status_guard", "cs_status_prepare", "cs_status_acquire",
                "cs_status_install", "cs_status_invoke")


def first_failure():
    for name in STATUS_ORDER:
        value = os.environ.get(name.upper(), "")
        if value and value != "ok":
            return value
    return None


def main():
    failure = first_failure()
    action_status = failure or "ok"
    C.write_output("action-status", action_status)

    for name in PRODUCT_OUTPUTS + WRAPPER_OUTPUTS:
        value = os.environ.get("CS_OUT_" + name.upper().replace("-", "_"), "")
        C.write_output(name, value, limit=C.BOUND_ENVELOPE)

    operation = os.environ.get("CS_INPUT_OPERATION", "-")
    outcome = os.environ.get("CS_OUT_OUTCOME", "") or "-"
    exit_code = os.environ.get("CS_OUT_EXIT_CODE", "") or "-"
    payload = os.environ.get("CS_OUT_PAYLOAD_VERSION", "") or "-"
    rows = [
        "| field | value |", "|---|---|",
        f"| operation | `{operation}` |",
        f"| action-status | `{action_status}` |",
        f"| outcome | `{outcome}` |",
        f"| exit-code | `{exit_code}` |",
        f"| payload | `{payload}` |",
    ]
    summary = "### contract-scribe\n\n" + "\n".join(rows) + "\n"
    summary = summary[:C.BOUND_SUMMARY]
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        with open(path, "a", encoding="utf-8", newline="\n") as fh:
            fh.write(summary)

    # Annotation contract (frozen): exactly one ::error:: annotation iff the
    # step failed — wrapper failure carries only the closed action.<stage>-
    # <reason> code; product failure carries outcome + exit code. Success and
    # no-envelope-cases carry nothing beyond the table.
    if failure:
        C.annotate_error(f"contract-scribe: {failure}")
    elif exit_code not in ("-", "0"):
        C.annotate_error(f"contract-scribe: {outcome} (exit {exit_code})")


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:
        C.annotate_error("contract-scribe: action.emit-exception")
        C.write_output("action-status", "action.emit-exception")
        raise SystemExit(1)
