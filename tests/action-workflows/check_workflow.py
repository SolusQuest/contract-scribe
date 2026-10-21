#!/usr/bin/env python3
"""Validate the automatic caller-owned campaign workflow structurally."""
import re
import sys
from pathlib import Path
import yaml


def require(condition, reason):
    if not condition:
        raise SystemExit("check_workflow.stop:" + reason)


def strings(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for key, child in value.items():
            yield from strings(key)
            yield from strings(child)
    elif isinstance(value, list):
        for child in value:
            yield from strings(child)


def main():
    require(len(sys.argv) == 2, "usage")
    doc = yaml.safe_load(Path(sys.argv[1]).read_text())
    trigger = doc.get("on", doc.get(True))
    require(set(trigger) in ({"workflow_dispatch"}, {"workflow_dispatch", "schedule"}), "trusted-triggers")
    require(trigger["workflow_dispatch"] == {}, "no-lifecycle-inputs")
    if "schedule" in trigger:
        require(isinstance(trigger["schedule"], list) and trigger["schedule"] and
                all(set(item) == {"cron"} and isinstance(item["cron"], str)
                    for item in trigger["schedule"]), "schedule-shape")
    require(doc["permissions"] == {}, "workflow-permissions")
    require(doc["concurrency"]["cancel-in-progress"] is False, "no-cancel")
    require("vars.CONTRACTSCRIBE_CAMPAIGN_LINEAGE" in doc["concurrency"]["group"], "campaign-concurrency")
    require(set(doc["jobs"]) == {"invoke"}, "single-job")
    job = doc["jobs"]["invoke"]
    require(job["permissions"] == {"actions": "read", "contents": "write", "pull-requests": "write"}, "job-permissions")
    require(job["timeout-minutes"] == 30, "recommended-timeout")
    require(all(text in job["if"] for text in ("workflow_dispatch", "schedule", "vars.CONTRACTSCRIBE_CAMPAIGN")), "activation")
    for scope in (doc, job):
        require(not any("runner." in text for text in strings(scope.get("env", {}))), "runner-env-context")
    require(doc["env"]["CS_CAMPAIGN"] == "${{ vars.CONTRACTSCRIBE_CAMPAIGN }}", "stable-campaign")
    require(doc["env"]["CS_HELPER"].endswith("/caller.py"), "caller-helper")
    steps = job["steps"]
    require(len(steps) == 4, "step-count")
    checkout, exact, gate, invoke = steps
    require(re.fullmatch(r"actions/checkout@[0-9a-f]{40}", checkout["uses"]), "checkout-pin")
    require(checkout["with"] == {"ref": "${{ github.sha }}", "persist-credentials": False}, "checkout-authority")
    require('git rev-parse HEAD' in exact["run"] and '${{ github.sha }}' in exact["run"], "exact-head")
    require(gate["env"] == {"GITHUB_TOKEN": "${{ github.token }}"} and gate["run"].endswith('" gate'), "admission-gate")
    require(re.fullmatch(r"SolusQuest/contract-scribe@(?:<RELEASE_TAG_OR_SHA>|[0-9a-f]{40}|v\d+\.\d+\.\d+)", invoke["uses"]), "action-pin")
    require("operation" not in invoke["with"] and "request" not in invoke["with"], "no-manual-operation")
    require(invoke["with"]["campaign"] == "${{ env.CS_CAMPAIGN }}", "campaign-input")
    require(invoke["with"]["github-token"] == "${{ github.token }}", "job-publication-token")
    require(invoke["with"]["configuration"] == "${{ env.CS_CONFIGURATION }}", "configuration-layer")
    for step in steps[:-1]:
        require(not any("secrets." in text for text in strings(step)), "credential-confinement")
    all_text = "\n".join(strings(doc))
    require(all(forbidden not in all_text for forbidden in ("CONTRACTSCRIBE_ACTIVATION", "actions/cache",
        "upload-artifact", "download-artifact", "checkpoint.json", "CS_STATE_DIR", "CS_OPERATION",
        "--request", "--base-oid", "request-json")), "no-manual-handoff")
    require("secrets.CONTRACTSCRIBE_GITHUB_TOKEN" not in all_text, "no-stored-publication-token")
    print("check_workflow:ok")


if __name__ == "__main__":
    main()
