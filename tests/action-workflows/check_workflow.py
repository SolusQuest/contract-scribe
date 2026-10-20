#!/usr/bin/env python3
"""Validate the explicit caller-owned ref-checkpoint workflow structurally."""
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
    require(set(trigger) == {"workflow_dispatch"}, "explicit-trigger")
    selection = trigger["workflow_dispatch"]["inputs"]
    require(set(selection) == {"operation"}, "operation-input")
    require(selection["operation"]["type"] == "choice" and selection["operation"]["required"] is True
            and selection["operation"]["options"] == ["start", "resume"], "operation-choice")
    require(doc["permissions"] == {}, "workflow-permissions")
    require(doc["concurrency"]["cancel-in-progress"] is False, "no-cancel")
    require("vars.CONTRACTSCRIBE_CAMPAIGN_LINEAGE" in doc["concurrency"]["group"], "campaign-concurrency")
    require(set(doc["jobs"]) == {"invoke"}, "single-explicit-job")
    job = doc["jobs"]["invoke"]
    require(job["permissions"] == {"actions": "read", "contents": "write", "pull-requests": "write"}, "job-permissions")
    require("workflow_dispatch" in job["if"] and "vars.CONTRACTSCRIBE_CAMPAIGN" in job["if"], "activation")
    for scope in (doc, job):
        require(not any("runner." in text for text in strings(scope.get("env", {}))), "runner-env-context")
    require(doc["env"]["CS_OPERATION"] == "${{ inputs.operation }}", "explicit-operation")
    steps = job["steps"]
    require(len(steps) == 5, "step-count")
    checkout, exact, gate, request, invoke = steps
    require(re.fullmatch(r"actions/checkout@[0-9a-f]{40}", checkout["uses"]), "checkout-pin")
    require(checkout["with"] == {"ref": "${{ github.sha }}", "persist-credentials": False}, "checkout-authority")
    require('git rev-parse HEAD' in exact["run"] and '${{ github.sha }}' in exact["run"], "exact-head")
    require(gate["env"] == {"GITHUB_TOKEN": "${{ github.token }}"} and gate["run"].endswith('" gate'), "admission-gate")
    require('" request' in request["run"] and "--base-oid" in request["run"] and "--state" not in request["run"], "ref-request")
    require(re.fullmatch(r"SolusQuest/contract-scribe@(?:<RELEASE_TAG_OR_SHA>|[0-9a-f]{40}|v\d+\.\d+\.\d+)", invoke["uses"]), "action-pin")
    require(invoke["with"]["operation"] == "github-proposal-${{ inputs.operation }}", "selected-operation")
    require(invoke["with"]["github-token"] == "${{ github.token }}", "job-publication-token")
    require(invoke["with"]["configuration"] == "${{ env.CS_CONFIGURATION }}", "configuration-layer")
    for step in steps[:-1]:
        require(not any("secrets." in text for text in strings(step)), "credential-confinement")
    all_text = "\n".join(strings(doc))
    require(all(forbidden not in all_text for forbidden in ("CONTRACTSCRIBE_ACTIVATION", "actions/cache",
        "upload-artifact", "download-artifact", "checkpoint.json", "CS_STATE_DIR")), "no-artifact-checkpoint")
    require("secrets.CONTRACTSCRIBE_GITHUB_TOKEN" not in all_text, "no-stored-publication-token")
    print("check_workflow:ok")


if __name__ == "__main__":
    main()
