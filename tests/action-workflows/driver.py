#!/usr/bin/env python3
"""Exercise explicit invocation admission and ref-backed request rendering offline."""
import copy
import json
import os
from pathlib import Path
import subprocess
import sys
import time

HERE = Path(__file__).resolve().parent
HELPER = Path(os.environ.get("HANDOFF_PY", HERE.parent.parent / "examples/github-actions/handoff.py"))
FAKE = Path(os.environ.get("FAKE_API_PY", HERE / "fake_actions_api.py"))


def main():
    assert len(sys.argv) == 3 and sys.argv[1] == "run", "usage: driver.py run WORK"
    work = Path(sys.argv[2]).resolve()
    work.mkdir(parents=True, exist_ok=True)
    sha = "a" * 40
    repository = {"id": 7001, "full_name": "Owner/repo", "default_branch": "main"}
    workflow = ".github/workflows/contract-scribe-campaign.yml"
    context = dict(runId=91001, runNumber=500, runAttempt=1, event="workflow_dispatch",
                   ref="refs/heads/main", sha=sha, repository="Owner/repo", repositoryId=7001,
                   workflowRef="Owner/repo/" + workflow + "@refs/heads/main")
    run = dict(id=91001, run_number=500, run_attempt=1, event="workflow_dispatch", status="in_progress",
               conclusion=None, head_branch="main", head_sha=sha, workflow_id=55, path=workflow,
               repository={"id": 7001, "full_name": "Owner/repo"}, head_repository={"id": 7001})
    cfg = dict(repository=repository, expected_token="synthetic-actions-read", runs={"91001": run},
               artifacts={}, run_artifacts={})
    config, ready, log = (work / name for name in ("config.json", "ready", "requests.log"))
    config.write_text(json.dumps(cfg))
    ready.unlink(missing_ok=True)
    server = subprocess.Popen([sys.executable, str(FAKE), str(config), str(ready), str(log)])
    try:
        deadline = time.monotonic() + 10
        while not ready.exists():
            assert server.poll() is None and time.monotonic() < deadline, "fake API startup"
            time.sleep(.02)
        endpoint = json.loads(ready.read_text())["endpoint"]
        campaign = dict(snapshot="snapshot.example", operationId="operation.example", generationId="generation.example",
                        targetRef="refs/heads/main", repositoryOwner="Owner", repositoryName="repo",
                        policyCeilings=dict(maximumDocumentationBlocks=8, maximumDistinctChangedFiles=4,
                                            maximumCumulativePatchBytes=65536))
        env = dict(os.environ, CS_ACTIONS_API_URL=endpoint, GITHUB_TOKEN="synthetic-actions-read",
                   CS_RUN_CONTEXT=json.dumps(context), CS_CAMPAIGN=json.dumps(campaign),
                   CS_CAMPAIGN_LINEAGE="campaign.example", CS_OPERATION="start")
        request = work / "request.json"
        args = ["request", "--base-oid", sha, "--out", str(request)]
        count = 0

        def check(arguments, overrides=None, expected=None):
            nonlocal count
            result = subprocess.run([sys.executable, str(HELPER), *arguments], env=env | (overrides or {}),
                                    capture_output=True, text=True, timeout=15)
            assert result.returncode == (0 if expected is None else 1), result
            if expected:
                assert "handoff.stop:" + expected in result.stderr, result
            count += 1

        for operation in ("start", "resume"):
            check(["gate"], {"CS_OPERATION": operation})
            check(args, {"CS_OPERATION": operation})
            rendered = json.loads(request.read_text())
            assert rendered["checkpointStore"] == "coordination-ref" and "state" not in rendered
            assert rendered["github"]["expectedBaseCommitOid"] == sha
        for operation in ("", "auto", "schedule"):
            check(["gate"], {"CS_OPERATION": operation}, "operation-selection")
            check(args, {"CS_OPERATION": operation}, "operation-selection")
        for field, value in (("head_sha", "b" * 40), ("run_attempt", 2), ("head_branch", "other"),
                             ("event", "schedule"), ("repository", {"id": 999, "full_name": "Owner/repo"})):
            changed = copy.deepcopy(cfg)
            changed["runs"]["91001"][field] = value
            config.write_text(json.dumps(changed))
            # These cases must fail before invocation credentials reach the Action.
            result = subprocess.run([sys.executable, str(HELPER), "gate"], env=env,
                                    capture_output=True, text=True, timeout=15)
            assert result.returncode == 1 and "handoff.stop:" in result.stderr, result
            count += 1
        config.write_text(json.dumps(cfg))
        for field, value, reason in (("targetRef", "refs/heads/other", "campaign-target"),
                                     ("repositoryName", "other", "campaign-repo"),
                                     ("operationId", "", "field-operationId-type")):
            changed = campaign | {field: value}
            check(args, {"CS_CAMPAIGN": json.dumps(changed)}, reason)
        check(args, {"CS_CAMPAIGN": '{"snapshot":1,"snapshot":2}'}, "cs_campaign-json")
        check(["request", "--base-oid", "b" * 40, "--out", str(request)], expected="base-context-mismatch")
        check(["request", "--base-oid", sha, "--state", str(work / "checkpoint.json"), "--out", str(request)], expected="request-usage")
        for retired in ("select", "verify", "emit", "activation"):
            check([retired], expected="unknown-command")
        assert not (work / "checkpoint.json").exists()
        assert "/artifacts" not in log.read_text()
        print(f"invocation driver: {count} passed")
    finally:
        server.terminate()
        server.wait(timeout=10)


if __name__ == "__main__":
    main()
