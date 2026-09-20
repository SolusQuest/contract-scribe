#!/usr/bin/env python3
"""check_release_workflow.py — static invariants for release.yml.

Text-oriented checks (no YAML dependency): the workflow must keep
publication unreachable from ordinary CI and keep credentials,
environments, and artifact selection out of caller-controlled inputs.
Exits nonzero with a FAIL line per violated invariant.
"""
import os
import re
import sys

REPO = os.path.abspath(
    os.path.join(os.path.dirname(__file__), "..", ".."))
WORKFLOW = os.path.join(REPO, ".github", "workflows", "release.yml")
CI = os.path.join(REPO, ".github", "workflows", "ci.yml")

FAILURES = []


def fail(check_name, detail):
    FAILURES.append(f"{check_name}: {detail}")


def block(text, start_marker, end_marker):
    start = text.find(start_marker)
    if start < 0:
        return None
    end = text.find(end_marker, start + len(start_marker)) \
        if end_marker else len(text)
    if end < 0:
        end = len(text)
    return text[start:end]


def job_block(text, name):
    """Return the job's text: from `  <name>:` to the next `  <key>:` at
    the same two-space indentation (job keys are 2-space indented)."""
    match = re.search(rf"^  {re.escape(name)}:\n", text, re.M)
    if not match:
        return None
    following = re.search(r"^  \S", text[match.end():], re.M)
    end = match.end() + following.start() if following else len(text)
    return text[match.start():end]


def step_envs(job):
    """Yield (step_name, env_body) for each named step that declares env."""
    if not job:
        return
    marks = list(re.finditer(r"- name: ([^\n]+)", job))
    for index, mark in enumerate(marks):
        end = marks[index + 1].start() if index + 1 < len(marks) \
            else len(job)
        step = job[mark.start():end]
        env = re.search(r"^\s+env:\n((?:\s+[A-Z_]+:[^\n]*\n)+)", step, re.M)
        if env:
            yield mark.group(1).strip(), env.group(1)


def run_bodies(job):
    """Yield the text following each step's `run:` key — the region where
    ${{ }} expressions are pre-evaluated and handed to the shell."""
    if not job:
        return
    for part in re.split(r"^      - ", job, flags=re.M)[1:]:
        match = re.search(r"^        run:", part, re.M)
        if match:
            yield part[match.end():]


def main():
    try:
        with open(WORKFLOW, encoding="utf-8") as fh:
            text = fh.read()
    except OSError:
        print("FAIL release.yml: missing")
        return 1

    on_start = re.search(r"^on:\n", text, re.M)
    on_end = re.search(r"^permissions:", text, re.M)
    on = text[on_start.end():on_end.start()] \
        if on_start and on_end else ""
    for trigger in ("push", "schedule", "pull_request_target",
                    "workflow_call", "repository_dispatch",
                    "workflow_run", "release"):
        if re.search(rf"^\s+{trigger}\s*:", on, re.M):
            fail("triggers", f"forbidden trigger {trigger}")
    if "workflow_dispatch" not in on or "pull_request" not in on:
        fail("triggers", "workflow_dispatch + filtered pull_request required")

    perms = block(text, "permissions:", "jobs:") or ""
    if not re.search(r"permissions:\s*\{\s*\}", perms):
        fail("permissions", "top-level permissions must be {}")

    for sha in re.findall(r"uses:\s*\S+@([0-9a-f]+)", text):
        if len(sha) != 40:
            fail("pins", f"action pin is not a full sha: {sha}")
    for ref in re.findall(r"uses:\s*(\S+)", text):
        if "@" not in ref or not re.search(r"@[0-9a-f]{40}$", ref):
            fail("pins", f"unpinned or non-sha action: {ref}")

    inputs = block(text, "  workflow_dispatch:", "  pull_request:") or ""
    if not re.search(r"operation:\n(?:.*\n)*?\s+required:\s*true", inputs):
        fail("inputs", "operation must be a required dispatch input")
    for name in ("source_revision", "payload_source_revision", "wrapper",
                 "release_version", "candidate_run_id", "candidate_digest",
                 "qualified_release_id", "qualified_asset_id",
                 "approval_reference", "governance_reference"):
        if not re.search(rf"^\s+{name}:", inputs, re.M):
            fail("inputs", f"missing dispatch input {name}")

    # No caller-controlled authority: environment names, tokens and API
    # roots must never come from inputs.
    if re.search(r"environment:\s*\$\{\{", text):
        fail("authority", "environment name must be a literal")
    if re.search(r"CONTRACTSCRIBE_RELEASE_TOKEN:\s*\$\{\{\s*inputs\.", text):
        fail("authority", "publication token must not come from inputs")
    if re.search(r"\$\{\{\s*inputs\.[^}]*\}\}.*(api.github.com|API_ROOT)",
                 text):
        fail("authority", "api root must not come from inputs")

    offline = job_block(text, "offline")
    if not offline:
        fail("offline", "missing offline job")
    else:
        if "contents: write" in offline or "environment:" in offline \
                or "secrets." in offline:
            fail("offline", "offline job must stay read-only")
        if "verify-release.sh" not in offline:
            fail("offline", "offline job must run verify-release.sh")

    candidate = job_block(text, "candidate")
    if not candidate:
        fail("candidate", "missing candidate job")
    else:
        if "contents: write" in candidate or "actions: write" in candidate:
            fail("candidate", "candidate job must not hold write perms")
        if "secrets." in candidate or "environment:" in candidate:
            fail("candidate", "candidate job must carry no secret/environment")
        if "CONTRACTSCRIBE_RELEASE_TOKEN" in candidate:
            fail("candidate", "candidate job must never see the token")
        if "prepare-candidate.py" not in candidate:
            fail("candidate", "candidate job must run prepare-candidate.py")
        # Same env boundary as stage/promote: untrusted dispatch inputs
        # must never reach a shell run body in the producer job either.
        for body in run_bodies(candidate):
            if re.search(r"\$\{\{\s*inputs\.", body):
                fail("candidate",
                     "dispatch inputs must reach run steps via env")

    for name, env_name, var_name in (
            ("stage", "release-candidate", "RELEASE_DRAFT_ENABLED"),
            ("promote", "release-publication", "RELEASE_PROMOTION_ENABLED")):
        job = job_block(text, name)
        if not job:
            fail(name, f"missing {name} job")
            continue
        if "contents: write" in job:
            fail(name, "GITHUB_TOKEN must stay read-only")
        if "actions: read" not in job:
            fail(name, "cross-run artifact/CI readback needs actions:read")
        if f"environment: {env_name}" not in job:
            fail(name, f"environment {env_name} required")
        if f"vars.{var_name} == 'true'" not in job:
            fail(name, f"repository variable {var_name} gate required")
        for gate in ("github.event_name == 'workflow_dispatch'",
                     "github.ref == 'refs/heads/main'",
                     "github.repository == 'SolusQuest/contract-scribe'"):
            if gate not in job:
                fail(name, f"missing job gate: {gate}")
        # Stale-approval predicates must be the exact fail-closed forms —
        # a weakened/negated/logging-only variant must not satisfy this.
        if 'github.run_attempt }}" = "1"' not in job:
            fail(name, "run_attempt == 1 assertion required")
        if 'github.actor }}" = "${{ github.triggering_actor' not in job:
            fail(name, "actor == triggering_actor assertion required")
        for body in run_bodies(job):
            if re.search(r"\$\{\{\s*inputs\.", body):
                fail(name, "dispatch inputs must reach run steps via env")
        if "persist-credentials: false" not in job:
            fail(name, "checkout must not persist credentials")
        if "digest-mismatch: error" not in job:
            fail(name, "artifact download must enforce digest-mismatch")
        if "resolve-artifact" not in job:
            fail(name, "artifact id must be derived from the verified run")
        if "promote-candidate.py" not in job:
            fail(name, f"{name} must run promote-candidate.py")
        if "build-payload.sh" in job or "dotnet publish" in job:
            fail(name, f"{name} must never rebuild")
        # The publication token may appear only on the mutation step env.
        mutation_envs = [body for head, body in step_envs(job)
                         if "stage the unpublished" in head.lower()
                         or "promote the exact" in head.lower()]
        if not mutation_envs:
            fail(name, "mutation step must carry the token env")
        for body in mutation_envs:
            if "CONTRACTSCRIBE_RELEASE_TOKEN" not in body \
                    or "secrets.RELEASE_PUBLICATION_TOKEN" not in body:
                fail(name, "mutation step must bind "
                           "secrets.RELEASE_PUBLICATION_TOKEN")
        for head, body in step_envs(job):
            if "CONTRACTSCRIBE_RELEASE_TOKEN" in body \
                    and body not in mutation_envs:
                fail(name, f"token leaked into non-mutation step: {head}")
        header = job.split("    steps:", 1)[0]
        if re.search(r"^    env:\n", header, re.M):
            fail(name, "job-level env must not hold the token")
        if "secrets.GITHUB_TOKEN" in job and "contents: write" in job:
            fail(name, "automatic token must not be publication-capable")

    try:
        with open(CI, encoding="utf-8") as fh:
            ci = fh.read()
    except OSError:
        ci = ""
    for needle in ("release.yml", "promote-candidate", "prepare-candidate"):
        if needle in ci:
            fail("ci", f"ci.yml must not invoke {needle}")

    try:
        with open(os.path.join(
                REPO, "docs", "10_workflow", "release-policy.md"),
                encoding="utf-8") as fh:
            policy = fh.read()
        with open(os.path.join(
                REPO, "docs", "10_workflow", "release-runbook.md"),
                encoding="utf-8") as fh:
            runbook = fh.read()
    except OSError:
        fail("docs", "release-policy.md and release-runbook.md must exist")
    else:
        if "release-runbook" not in policy:
            fail("docs", "release-policy must reference the runbook")
        for needle in ("rollback", "withdrawal", "partial",
                       "release-candidate", "release-publication",
                       "RELEASE_DRAFT_ENABLED", "RELEASE_PROMOTION_ENABLED",
                       "RELEASE_PUBLICATION_TOKEN", "workflow_dispatch",
                       "payload-map.json", "action_packaged",
                       "qualified", "run_attempt"):
            if needle not in runbook:
                fail("docs", f"runbook must cover {needle}")

    if FAILURES:
        for entry in FAILURES:
            print(f"FAIL {entry}")
        return 1
    print("check-release-workflow: ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
