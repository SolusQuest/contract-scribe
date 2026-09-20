#!/usr/bin/env python3
"""check_workflow.py — structural validation of the caller-owned example
workflow (tests/action-workflows).

Parses examples/github-actions/contract-scribe-campaign.yml with PyYAML and
asserts the ContractScribe invariants that need a real parse: trigger shape,
job activation predicates, least-privilege permissions, pinned third-party
actions, credential confinement, step ordering, artifact hygiene and the
sealed-slot choreography.

Usage: check_workflow.py <workflow.yml>   (exit 0 + 'check_workflow:ok')
"""

import re
import sys

try:
    import yaml
except ImportError:
    print("check_workflow: PyYAML required", file=sys.stderr)
    sys.exit(1)

SHA_RE = re.compile(r"^[0-9a-f]{40}$")
THIRD_PARTY = {"actions/checkout", "actions/upload-artifact",
               "actions/download-artifact", "actions/setup-dotnet"}
ACTION_REPO = "SolusQuest/contract-scribe"
ACTION_PLACEHOLDER = "<RELEASE_TAG_OR_SHA>"
HELPER = ".github/contract-scribe/handoff.py"


def fail(reason):
    print(f"check_workflow.stop:{reason}", file=sys.stderr)
    sys.exit(1)


def require(cond, reason):
    if not cond:
        fail(reason)


def step_names(steps):
    return [s.get("name", "") for s in steps]


def uses_of(step):
    return step.get("uses", "")


def run_of(step):
    return step.get("run", "") or ""


def check_pin(uses):
    repo, _, ref = uses.partition("@")
    if repo in THIRD_PARTY:
        require(SHA_RE.match(ref), f"unpinned-{repo}")
        return
    if repo == ACTION_REPO:
        # The Action ref is a caller replacement point: the placeholder or a
        # concrete immutable release revision — never a moving branch/tag.
        require(ref == ACTION_PLACEHOLDER or SHA_RE.match(ref)
                or re.match(r"^v\d+\.\d+\.\d+$", ref),
                "action-ref-moving")
        return
    if uses.startswith("./"):
        fail("local-uses")
    fail(f"unknown-action-{repo}")


def check_step_order(names, expected_order):
    positions = []
    for needle in expected_order:
        found = [i for i, n in enumerate(names) if needle in n]
        require(len(found) == 1, f"step-missing-{needle}")
        positions.append(found[0])
    require(positions == sorted(positions), "step-order")


def collect_strings(node, out):
    if isinstance(node, str):
        out.append(node)
    elif isinstance(node, dict):
        for k, v in node.items():
            if isinstance(k, str):
                out.append(k)
            collect_strings(v, out)
    elif isinstance(node, list):
        for item in node:
            collect_strings(item, out)
    return out


def main():
    require(len(sys.argv) == 2, "usage")
    with open(sys.argv[1], encoding="utf-8") as fh:
        doc = yaml.safe_load(fh)
    require(isinstance(doc, dict), "not-a-mapping")

    # -- triggers ------------------------------------------------------------
    # YAML 1.1 (PyYAML) parses the `on:` key as boolean True.
    on = doc.get("on", doc.get(True))
    require(isinstance(on, dict), "on-shape")
    require(set(on) == {"workflow_dispatch", "schedule"}, "on-triggers")
    schedule = on["schedule"]
    require(isinstance(schedule, list) and len(schedule) == 1
            and schedule[0].get("cron") == "0 0 30 2 *", "schedule-inert")

    # -- workflow-level hygiene ----------------------------------------------
    require(doc.get("permissions") == {}, "top-permissions")
    concurrency = doc.get("concurrency") or {}
    require("contract-scribe-" in str(concurrency.get("group")),
            "concurrency-group")
    require(concurrency.get("cancel-in-progress") is False,
            "concurrency-cancel")

    jobs = doc.get("jobs") or {}
    require(set(jobs) == {"start", "resume"}, "job-set")

    start = jobs["start"]
    resume = jobs["resume"]
    require("matrix" not in start and "matrix" not in resume, "matrix")
    require(start.get("permissions") == {"contents": "read"},
            "start-permissions")
    require(resume.get("permissions") ==
            {"contents": "read", "actions": "read"}, "resume-permissions")
    require("workflow_dispatch" in str(start.get("if", "")),
            "start-event-gate")
    require("'schedule'" in str(resume.get("if", ""))
            and "CONTRACTSCRIBE_ACTIVATION" in str(resume.get("if", "")),
            "resume-activation-gate")
    require("secrets" not in str(start.get("if", ""))
            and "secrets" not in str(resume.get("if", "")), "if-secrets")

    for job_name, job, order in (
            ("start", start,
             ["Check out trusted revision", "Assert exact workflow head",
              "Producer admission gate", "Build invocation request",
              "ContractScribe github-proposal", "Emit",
              "Upload", "Render next activation"]),
            ("resume", resume,
             ["Check out trusted revision", "Assert exact workflow head",
              "Authenticate activation", "Download sealed producer handoff",
              "Verify handoff and restore checkpoint",
              "Build invocation request", "ContractScribe github-proposal",
              "Emit", "Upload", "Render next activation"])):
        steps = job.get("steps") or []
        require(all(isinstance(s, dict) for s in steps), "step-shape")
        check_step_order(step_names(steps), order)
        for step in steps:
            uses = uses_of(step)
            if uses:
                check_pin(uses)
            require("continue-on-error" not in step, "continue-on-error")
            require("retry" not in str(step).lower(), "retry-construct")

        # Credential confinement: secrets.* only inside the Action step with:.
        action_steps = [s for s in steps
                        if uses_of(s).startswith(ACTION_REPO + "@")]
        require(len(action_steps) == 1, "action-step-count")
        for step in steps:
            strings = collect_strings(
                {k: v for k, v in step.items() if k not in ("with",)},
                [])
            if step is not action_steps[0]:
                strings += collect_strings(step.get("with", {}), [])
            blob = " ".join(strings)
            require("secrets." not in blob, f"secrets-outside-action")

        wit = action_steps[0].get("with", {})
        require(wit.get("github-token") ==
                "${{ secrets.CONTRACTSCRIBE_GITHUB_TOKEN }}",
                "publication-token")
        require(wit.get("provider-api-key") ==
                "${{ secrets.CONTRACTSCRIBE_PROVIDER_API_KEY }}",
                "provider-token")
        require(wit.get("operation") in
                ("github-proposal-start", "github-proposal-resume"),
                "operation")
        require(wit.get("repository-root") == "${{ github.workspace }}",
                "repository-root")
        require(wit.get("request") ==
                "${{ steps.request.outputs.request-json }}",
                "request-wiring")

        upload = [s for s in steps
                  if uses_of(s).startswith("actions/upload-artifact@")]
        require(len(upload) == 1, "upload-count")
        uwit = upload[0].get("with", {})
        require(uwit.get("name") == "contract-scribe-handoff", "upload-name")
        require(uwit.get("if-no-files-found") == "error", "upload-missing")
        require(uwit.get("overwrite") is False, "upload-overwrite")
        retention = uwit.get("retention-days")
        require(isinstance(retention, int) and 1 <= retention <= 30,
                "upload-retention")

        # The Action step must not appear before the request or after emit.
        names = step_names(steps)
        invoke = next(i for i, s in enumerate(steps)
                      if uses_of(s).startswith(ACTION_REPO + "@"))
        require("request" in names[invoke - 1].lower(), "request-before-action")
        require(steps[invoke + 1].get("run", "").find("emit") >= 0,
                "emit-after-action")

    # -- resume-specific -------------------------------------------------------
    rnames = step_names(resume.get("steps"))
    check_step_order(rnames, ["Authenticate activation",
                              "Download sealed producer handoff",
                              "Verify handoff and restore checkpoint",
                              "Build invocation request",
                              "ContractScribe github-proposal resume"])
    rsteps = resume["steps"]
    download = [s for s in rsteps
                if uses_of(s).startswith("actions/download-artifact@")]
    require(len(download) == 1, "download-count")
    dwit = download[0].get("with", {})
    require(dwit.get("artifact-ids") ==
            "${{ steps.select.outputs.artifact-id }}", "download-ids")
    require(dwit.get("run-id") ==
            "${{ steps.select.outputs.producer-run-id }}", "download-runid")
    require(dwit.get("github-token") == "${{ github.token }}",
            "download-token")
    require(dwit.get("digest-mismatch") == "error", "download-digest")
    require(dwit.get("skip-decompress") is True, "download-skip")
    require("name" not in dwit and "pattern" not in dwit
            and "merge-multiple" not in dwit, "download-latest")

    select_idx = next(i for i, s in enumerate(rsteps)
                      if "Authenticate activation" in s.get("name", ""))
    verify_idx = next(i for i, s in enumerate(rsteps)
                      if "Verify handoff" in s.get("name", ""))
    invoke_idx = next(i for i, s in enumerate(rsteps)
                      if uses_of(s).startswith(ACTION_REPO + "@"))
    require(select_idx < verify_idx < invoke_idx, "resume-order")

    select_run = run_of(rsteps[select_idx])
    require("select" in select_run and "--workdir" in select_run,
            "select-invocation")
    verify_run = run_of(rsteps[verify_idx])
    require("verify" in verify_run and "--state-dir" in verify_run,
            "verify-invocation")

    # The restored state must live outside the checkout.
    require("runner.temp" in str(doc.get("env", {})), "state-outside")

    # -- no latest-state selection / caches / batching --------------------------
    # (parsed content only — comments are not inspected)
    all_strings = collect_strings(doc, [])
    blob = "\n".join(all_strings)
    for forbidden in ("actions/cache", "merge-multiple", "pull_request",
                      "id-token:", "attest", "continue-on-error"):
        require(forbidden not in blob, f"forbidden-{forbidden}")
    env_block = str(doc.get("env", {}))
    require("secrets." not in env_block, "env-secrets")
    require("GITHUB_TOKEN" not in env_block, "env-token")

    # Every step's run lines: only the helper, the rev-parse assertion, and
    # no secrets interpolation anywhere outside the Action step's with:.
    for job in (start, resume):
        for step in job["steps"]:
            run = run_of(step)
            if run:
                require("secrets." not in run, "secrets-in-run")

    print("check_workflow:ok")


if __name__ == "__main__":
    main()
