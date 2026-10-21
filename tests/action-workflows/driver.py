#!/usr/bin/env python3
"""Exercise trusted trigger admission without selecting campaign lifecycle."""
import copy
import json
import os
from pathlib import Path
import subprocess
import sys
import time

HERE = Path(__file__).resolve().parent
HELPER = Path(os.environ.get("CALLER_PY", HERE.parent.parent / "examples/github-actions/caller.py"))
FAKE = Path(os.environ.get("FAKE_API_PY", HERE / "fake_actions_api.py"))


def main():
    assert len(sys.argv) == 3 and sys.argv[1] == "run", "usage: driver.py run WORK"
    work = Path(sys.argv[2]).resolve()
    work.mkdir(parents=True, exist_ok=True)
    sha = "a" * 40
    workflow = ".github/workflows/contract-scribe-campaign.yml"
    context = dict(runId=91001, runNumber=500, runAttempt=1, event="workflow_dispatch",
                   ref="refs/heads/main", sha=sha, repository="Owner/repo", repositoryId=7001,
                   workflowRef="Owner/repo/" + workflow + "@refs/heads/main")
    run = dict(id=91001, run_number=500, run_attempt=1, event="workflow_dispatch", status="in_progress",
               conclusion=None, head_branch="main", head_sha=sha, workflow_id=55, path=workflow,
               repository={"id": 7001, "full_name": "Owner/repo"}, head_repository={"id": 7001})
    cfg = dict(repository={"id": 7001, "full_name": "Owner/repo", "default_branch": "main"},
               expected_token="synthetic-actions-read", runs={"91001": run}, artifacts={}, run_artifacts={})
    config, ready, log = (work / name for name in ("config.json", "ready", "requests.log"))
    config.write_text(json.dumps(cfg))
    ready.unlink(missing_ok=True)
    server = subprocess.Popen([sys.executable, str(FAKE), str(config), str(ready), str(log)])
    try:
        deadline = time.monotonic() + 10
        while not ready.exists():
            assert server.poll() is None and time.monotonic() < deadline, "fake API startup"
            time.sleep(.02)
        env = dict(os.environ, CS_ACTIONS_API_URL=json.loads(ready.read_text())["endpoint"],
                   GITHUB_TOKEN="synthetic-actions-read", CS_RUN_CONTEXT=json.dumps(context))
        count = 0

        def check(arguments, overrides=None, expected=None):
            nonlocal count
            result = subprocess.run([sys.executable, str(HELPER), *arguments], env=env | (overrides or {}),
                                    capture_output=True, text=True, timeout=15)
            assert result.returncode == (0 if expected is None else 1), result
            if expected:
                assert "caller.stop:" + expected in result.stderr, result
            count += 1

        for event in ("workflow_dispatch", "schedule"):
            event_cfg = copy.deepcopy(cfg)
            event_cfg["runs"]["91001"]["event"] = event
            config.write_text(json.dumps(event_cfg))
            for _ in range(2):
                check(["gate"], {"CS_RUN_CONTEXT": json.dumps(context | {"event": event})})
        config.write_text(json.dumps(cfg))
        for field, value, reason in (("head_sha", "b" * 40, "own-run-sha"),
                                     ("run_attempt", 2, "own-run-attempt"),
                                     ("head_branch", "feature", "own-run-ref-mismatch"),
                                     ("event", "pull_request", "own-run-event"),
                                     ("repository", {"id": 7}, "own-run-repo"),
                                     ("path", ".github/workflows/other.yml", "workflow-ref-path")):
            bad = copy.deepcopy(cfg)
            bad["runs"]["91001"][field] = value
            config.write_text(json.dumps(bad))
            check(["gate"], expected=reason)
        for event in ("pull_request", "pull_request_target", "repository_dispatch"):
            bad = copy.deepcopy(cfg)
            bad["runs"]["91001"]["event"] = event
            config.write_text(json.dumps(bad))
            check(["gate"], {"CS_RUN_CONTEXT": json.dumps(context | {"event": event})}, "gate-event")
        config.write_text(json.dumps(cfg))
        check(["gate"], {"CS_RUN_CONTEXT": '{"runId":1,"runId":2}'}, "cs_run_context-json")
        check(["gate"], {"CS_RUN_CONTEXT": json.dumps(context | {"workflowRef": "other"})}, "workflow-ref-path")
        for retired in ("request", "select", "verify", "emit", "activation", "start", "resume"):
            check([retired], expected="unknown-command")
        check(["request", "--base-oid", sha, "--out", str(work / "request.json")], expected="usage")
        assert not (work / "request.json").exists()
        assert "artifacts" not in log.read_text()
        print(f"caller admission: {count} passed")
    finally:
        server.terminate()
        server.wait(timeout=5)


if __name__ == "__main__":
    main()
