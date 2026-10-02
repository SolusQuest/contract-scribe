#!/usr/bin/env python3
"""Exercise PR preparation/staging against the matrix's real Git and HTTP fakes."""
import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys


def main():
    scripts, work, fixture, state_path, api, builder = sys.argv[1:7]
    scripts, work, fixture, state_path = map(Path, (scripts, work, fixture, state_path))
    request_log = work / "fake-requests.log"
    state = json.loads(state_path.read_text())
    repository = state["repository"]
    repo_id = str(repository["id"])
    base = subprocess.check_output(["git", "-C", str(fixture), "rev-parse", "main"], text=True).strip()
    branch = "pr-qualification"
    number = "204"
    env = {**os.environ, "CONTRACTSCRIBE_RELEASE_TEST": "1",
           "CONTRACTSCRIBE_RELEASE_TEST_API_ROOT": api,
           "CONTRACTSCRIBE_RELEASE_TEST_BUILDER": builder,
           "GITHUB_ACTIONS": "true", "GITHUB_REPOSITORY": repository["full_name"],
           "GITHUB_REPOSITORY_ID": repo_id, "GITHUB_REF": "refs/heads/" + branch,
           "GITHUB_RUN_ID": str(state["runs"][0]["id"]), "GITHUB_RUN_ATTEMPT": "1",
           "CONTRACTSCRIBE_RELEASE_READ_TOKEN": state["expected_read_token"],
           "CONTRACTSCRIBE_RELEASE_TOKEN": state["expected_release_token"]}

    def git(*args):
        return subprocess.check_output(["git", "-C", str(fixture), *args], text=True).strip()

    def save(value):
        temporary = state_path.with_suffix(".tmp")
        temporary.write_text(json.dumps(value))
        os.replace(temporary, state_path)

    def bind(head):
        env.update(GITHUB_SHA=head, GITHUB_WORKFLOW_SHA=head)
        state["runs"][0].update(head_sha=head, head_branch=branch)
        state["artifacts"][0]["workflow_run"]["head_sha"] = head
        state["refs"] = {"heads/main": base}
        state["pull_requests"] = {number: {"number": int(number), "state": "open",
            "head": {"ref": branch, "sha": head, "repo": repository},
            "base": {"ref": "main", "sha": base, "repo": repository}}}
        state["ci_runs"] = {head: [{"id": 888, "path": ".github/workflows/ci.yml",
            "head_sha": head, "status": "completed", "conclusion": "success",
            "jobs": [{"name": "action_packaged", "status": "completed", "conclusion": "success"}]}]}
        save(state)

    def run(script, args, allowed=True, overrides=None, reason=None):
        result = subprocess.run([sys.executable, str(scripts / script), *map(str, args)],
                                env={**env, **(overrides or {})}, capture_output=True, text=True)
        print(result.stdout, end="")
        assert (result.returncode == 0) == allowed, result.stdout + result.stderr
        if reason:
            assert reason in result.stdout, result.stdout

    git("checkout", "-qb", branch)
    try:
        workflow = fixture / ".github/workflows/qualification.yml"
        workflow.parent.mkdir(parents=True, exist_ok=True)
        workflow.write_text("name: unmerged-workflow\n")
        git("add", ".github/workflows/qualification.yml")
        git("commit", "-qm", "PR payload with changed workflows")
        payload = git("rev-parse", "HEAD")
        bind(payload)
        provisional, final = work / "pr-provisional", work / "pr-final"

        def prepare(out, source, version="v0.1.0-internal.2"):
            return ["--repo", fixture, "--output", out, "--source-revision", source,
                    "--payload-source-revision", payload, "--release-version", version,
                    "--wrapper", "contract-scribe-action", "--pull-request", number]

        run("prepare-candidate.py", prepare(work / "pr-normal-denied", payload, "v0.1.0"),
            False, reason="qualification-draft-only")
        run("prepare-candidate.py", prepare(provisional, payload))
        shutil.copyfile(provisional / "payload-map.json", fixture / "scripts/action/payload-map.json")
        git("add", "scripts/action/payload-map.json")
        git("commit", "-qm", "PR authorized payload map")
        head = git("rev-parse", "HEAD")
        bind(head)
        run("prepare-candidate.py", prepare(final, head))
        candidate = json.loads((final / "candidate.json").read_text())
        digest = (final / "candidate.sha256").read_text().split()[0]
        args = ["--candidate", final, "--repo", fixture, "--source-revision", head,
                "--payload-source-revision", payload, "--release-version", "v0.1.0-internal.2",
                "--wrapper", "contract-scribe-action", "--candidate-digest", digest,
                "--candidate-run-id", env["GITHUB_RUN_ID"], "--pull-request", number]
        # All negative admissions execute the production command, with an
        # actual HTTP request log proving there was no release/tag mutation.
        negatives = [
            ("closed", lambda s: s["pull_requests"][number].update(state="closed")),
            ("fork", lambda s: s["pull_requests"][number]["head"].update(repo={"id": 99, "full_name": "other/fork"})),
            ("head-drift", lambda s: s["pull_requests"][number]["head"].update(sha="f" * 40)),
            ("base-drift", lambda s: s["refs"].update({"heads/main": "f" * 40})),
            ("producer-branch", lambda s: s["runs"][0].update(head_branch="other")),
            ("producer-head", lambda s: s["runs"][0].update(head_sha=base)),
            ("ci-failed", lambda s: s["ci_runs"][head][0].update(conclusion="failure")),
            ("ci-wrong-head", lambda s: s["ci_runs"][head][0].update(head_sha=base)),
        ]
        for label, mutate in negatives:
            changed = copy.deepcopy(state)
            mutate(changed)
            save(changed)
            request_log.write_text("")
            run("promote-candidate.py", ["stage-draft", *args], False)
            assert not any(line.startswith(("POST", "PATCH", "DELETE")) for line in request_log.read_text().splitlines()), label
        save(state)
        for variable in ("GITHUB_SHA", "GITHUB_WORKFLOW_SHA", "GITHUB_REF"):
            run("promote-candidate.py", ["stage-draft", *args], False, {variable: "wrong"})
        run("promote-candidate.py", ["stage-draft", *args[:-2]], False, reason="qualification-mode")
        run("promote-candidate.py", ["resolve-artifact", "--candidate-run-id", env["GITHUB_RUN_ID"],
                                    "--pull-request", number])
        request_log.write_text("")
        run("promote-candidate.py", ["stage-draft", *args])
        actual = json.loads(state_path.read_text())
        release = actual["releases"][0]
        assert release["draft"] is True and release["prerelease"] is True
        assert release["target_commitish"] == base != payload
        assert payload in release["body"] and head in release["body"]
        assert candidate["identity"]["qualification"]["baseRevision"] == base
        assert actual["refs"] == {"heads/main": base}
        assert len(release["assets"]) == 1
        assert not any(line.startswith("POST") and "/git/refs" in line for line in request_log.read_text().splitlines())
        # Even a coherently re-digested normal-version artifact cannot
        # convert the PR mode into publication authority.
        candidate["identity"].update(releaseVersion="v0.1.0", prerelease=False)
        identity_bytes = (json.dumps(candidate["identity"], indent=2, sort_keys=True) + "\n").encode()
        (final / "candidate.json").write_text(json.dumps(candidate))
        (final / "candidate.sha256").write_text(hashlib.sha256(identity_bytes).hexdigest() + "  candidate.json\n")
        promote = ["promote", *args[:-2], "--release-version", "v0.1.0",
                   "--qualified-release-id", "1", "--qualified-asset-id", "1",
                   "--approval-reference", "synthetic", "--governance-reference", "synthetic"]
        request_log.write_text("")
        run("promote-candidate.py", promote, False, reason="qualification-draft-only")
        assert request_log.read_text() == ""
    finally:
        git("checkout", "-q", "main")
    print("PR qualification: exact-head draft succeeded; authority substitutions refused")


if __name__ == "__main__":
    main()
