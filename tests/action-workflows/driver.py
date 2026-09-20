#!/usr/bin/env python3
"""driver.py — leg runner for tests/action-workflows (issue #186 / M6-A3).

Drives the real `examples/github-actions/handoff.py` helper against the
loopback `fake_actions_api.py` substitute using synthetic run/artifact
metadata. Used by verify-examples.sh for the local functional matrix and by
the action_examples_consumer CI job for the same legs plus transport-real
variants.

Legs assert the helper's exit code and `handoff.stop:<reason>` marker (or
`:ok`). The helper itself performs every authentication decision; this file
only composes caller claims, fake metadata and fabricated archives.

Usage:
  driver.py run <workdir> [--only REGEX]
      Start fake_actions_api on loopback and run every leg. Requires
      python3 + stdlib only; no dotnet, no credentials.

Environment consumed by the runner itself:
  HANDOFF_PY   path to handoff.py (default ../../examples/github-actions)
  FAKE_API_PY  path to fake_actions_api.py (default same directory)
"""

import hashlib
import io
import json
import os
import re
import subprocess
import sys
import time
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
HANDOFF = os.environ.get(
    "HANDOFF_PY",
    os.path.join(HERE, "..", "..", "examples", "github-actions", "handoff.py"))
FAKE_API = os.environ.get(
    "FAKE_API_PY", os.path.join(HERE, "fake_actions_api.py"))

TOKEN = "contract-scribe-synthetic-actions-read"
REPO = {"id": 7001, "full_name": "Owner/repo", "default_branch": "main"}
WORKFLOW_ID = 55
WORKFLOW_PATH = ".github/workflows/contract-scribe-campaign.yml"
SHA_A = "a" * 40
SHA_B = "b" * 40
FUTURE = "2099-01-01T00:00:00Z"

PRODUCER_RUN = 91001
CONSUMER_RUN = 92002
ARTIFACT_ID = 77123


def run_record(run_id, number, event, **overrides):
    record = {
        "id": run_id,
        "run_number": number,
        "run_attempt": 1,
        "event": event,
        "status": "completed",
        "conclusion": "success",
        "head_branch": "main",
        "head_sha": SHA_B,
        "workflow_id": WORKFLOW_ID,
        "path": WORKFLOW_PATH,
        "repository": {"id": REPO["id"], "full_name": REPO["full_name"]},
        "head_repository": {"id": REPO["id"]},
    }
    record.update(overrides)
    return record


def context_json(run_id, number, event, ref="refs/heads/main", sha=SHA_A,
                 attempt=1):
    return json.dumps({
        "runId": run_id, "runNumber": number, "runAttempt": attempt,
        "event": event, "ref": ref, "sha": sha,
        "repository": REPO["full_name"], "repositoryId": REPO["id"],
        "workflowRef": REPO["full_name"] + "/" + WORKFLOW_PATH + "@" + ref})


def activation_json(run_id=PRODUCER_RUN, number=500, artifact=ARTIFACT_ID,
                    digest="sha256:" + "0" * 64, consumer=501,
                    event="schedule"):
    return json.dumps({
        "producer": {"runId": run_id, "runNumber": number, "runAttempt": 1,
                     "artifactId": artifact, "artifactDigest": digest},
        "consumer": {"runNumber": consumer, "event": event}})


def artifact_record(artifact_id=ARTIFACT_ID, run_id=PRODUCER_RUN,
                    digest="sha256:" + "0" * 64, **overrides):
    record = {
        "id": artifact_id,
        "name": "contract-scribe-handoff",
        "size_in_bytes": 1024,
        "expired": False,
        "expires_at": FUTURE,
        "digest": digest,
        "workflow_run": {"id": run_id, "repository_id": REPO["id"],
                         "head_repository_id": REPO["id"],
                         "head_branch": "main", "head_sha": SHA_B},
    }
    record.update(overrides)
    return record


def base_config():
    return {
        "repository": REPO,
        "expected_token": TOKEN,
        "runs": {
            str(CONSUMER_RUN): run_record(
                CONSUMER_RUN, 501, "schedule", status="in_progress",
                conclusion=None, head_sha=SHA_A),
            str(PRODUCER_RUN): run_record(PRODUCER_RUN, 500,
                                          "workflow_dispatch"),
        },
        "artifacts": {str(ARTIFACT_ID): artifact_record()},
        "run_artifacts": {str(PRODUCER_RUN): [ARTIFACT_ID]},
    }


def make_zip(handoff, checkpoint):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        zf.writestr("handoff.json", handoff)
        zf.writestr("checkpoint.json", checkpoint)
    return buf.getvalue()


def good_handoff(run_number=500, consumer_number=501, sha="0" * 64,
                 size=64):
    return {
        "producer": {
            "repositoryId": REPO["id"], "repository": REPO["full_name"],
            "workflowId": WORKFLOW_ID, "workflowPath": WORKFLOW_PATH,
            "runId": PRODUCER_RUN, "runNumber": run_number, "runAttempt": 1,
            "event": "workflow_dispatch", "headRef": "refs/heads/main",
            "headSha": SHA_B},
        "checkpoint": {"sha256": sha, "bytes": size},
        "consumer": {
            "repositoryId": REPO["id"], "repository": REPO["full_name"],
            "workflowId": WORKFLOW_ID, "runNumber": consumer_number,
            "event": "schedule"},
    }


def _zip_with_extra(handoff, checkpoint):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        zf.writestr("handoff.json", json.dumps(handoff))
        zf.writestr("checkpoint.json", checkpoint)
        zf.writestr("extra.txt", "x")
    return buf.getvalue()


CAMPAIGN = json.dumps({
    "snapshot": "snapshot.example",
    "operationId": "operation.example",
    "generationId": "generation.example",
    "targetRef": "refs/heads/main",
    "repositoryOwner": "Owner",
    "repositoryName": "repo",
    "policyCeilings": {"maximumDocumentationBlocks": 8,
                       "maximumDistinctChangedFiles": 4,
                       "maximumCumulativePatchBytes": 65536}})


class Runner:
    def __init__(self, workdir):
        self.workdir = workdir
        os.makedirs(workdir, exist_ok=True)
        self.config_path = os.path.join(workdir, "actions-config.json")
        self.ready_path = os.path.join(workdir, "actions-ready.json")
        self.log_path = os.path.join(workdir, "actions.log")
        self.api = None
        self.proc = None
        self.legs = []
        self.failures = []

    def configure(self, config):
        tmp = self.config_path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as fh:
            json.dump(config, fh)
        os.replace(tmp, self.config_path)

    def start(self):
        self.configure(base_config())
        self.proc = subprocess.Popen(
            [sys.executable, FAKE_API, self.config_path, self.ready_path,
             self.log_path])
        for _ in range(100):
            if os.path.exists(self.ready_path):
                break
            time.sleep(0.05)
        with open(self.ready_path, encoding="utf-8") as fh:
            ready = json.load(fh)
        self.api = ready["endpoint"]

    def stop(self):
        if self.proc:
            self.proc.terminate()
            self.proc.wait(timeout=10)

    # -- leg plumbing ------------------------------------------------------
    def leg(self, name):
        def deco(fn):
            self.legs.append((name, fn))
            return fn
        return deco

    def env(self, ctx=None, activation=None, campaign=None, token=TOKEN):
        env = dict(os.environ)
        env.update({
            "CS_RUN_CONTEXT": ctx if ctx is not None else context_json(
                CONSUMER_RUN, 501, "schedule"),
            "CS_ACTIONS_API_URL": self.api,
            "GITHUB_TOKEN": token,
            "CS_CAMPAIGN_LINEAGE": "campaign.example",
        })
        if activation is not None:
            env["CS_ACTIVATION"] = activation
        if campaign is not None:
            env["CS_CAMPAIGN"] = campaign
        out = os.path.join(self.workdir, "gh-output.txt")
        env["GITHUB_OUTPUT"] = out
        return env

    def run_helper(self, args, env, ok=True, reason=None):
        out = os.path.join(self.workdir, "gh-output.txt")
        if os.path.exists(out):
            os.unlink(out)
        proc = subprocess.run(
            [sys.executable, HANDOFF] + args, env=env,
            capture_output=True, text=True, timeout=60)
        stderr = proc.stderr.strip().splitlines()
        stdout = proc.stdout.strip().splitlines()
        smarker = stderr[-1] if stderr else ""
        omarker = stdout[-1] if stdout else ""
        if ok:
            assert proc.returncode == 0 and omarker.endswith(":ok"), \
                f"expected ok, got rc={proc.returncode} " \
                f"stdout={omarker} stderr={smarker}"
        else:
            assert proc.returncode == 1
            expected = "handoff.stop:" + reason
            assert expected == smarker, \
                f"expected {expected}, got stderr={smarker}"
        return proc

    def read_output(self, name):
        path = os.path.join(self.workdir, "gh-output.txt")
        if not os.path.exists(path):
            return None
        for line in open(path, encoding="utf-8").read().splitlines():
            if line.startswith(name + "="):
                return line.split("=", 1)[1]
        return None


# ---------------------------------------------------------------------------
# Legs
# ---------------------------------------------------------------------------

def register(r):
    leg = r.leg

    def select_env(**kw):
        return r.env(**kw)

    def select_args(wd):
        return ["select", "--workdir", os.path.join(wd, "sel")]

    # -- select ------------------------------------------------------------
    @leg("select_ok")
    def _():
        cfg = base_config()
        r.configure(cfg)
        env = select_env(activation=activation_json())
        r.run_helper(select_args(r.workdir), env)
        assert r.read_output("artifact-id") == str(ARTIFACT_ID)
        with open(os.path.join(r.workdir, "sel", "selection.json"),
                  encoding="utf-8") as fh:
            sel = json.load(fh)
        assert sel["producer"]["runNumber"] == 500

    def select_case(name, mutate_cfg=None, mutate_act=None,
                    ctx=None, activation="default", reason=""):
        @leg(name)
        def _():
            cfg = base_config()
            if mutate_cfg:
                mutate_cfg(cfg)
            r.configure(cfg)
            act = activation_json() if activation == "default" else activation
            env = select_env(ctx=ctx, activation=act)
            r.run_helper(select_args(r.workdir), env, ok=False, reason=reason)

    select_case("select_wrong_repo",
                mutate_cfg=lambda c: c["runs"][str(PRODUCER_RUN)]
                .update({"repository": {"id": 9999,
                                        "full_name": "Other/x"}}),
                reason="producer-repo")
    select_case("select_wrong_workflow",
                mutate_cfg=lambda c: c["runs"][str(PRODUCER_RUN)]
                .update({"workflow_id": 777}),
                reason="producer-workflow")
    select_case("select_wrong_ref",
                mutate_cfg=lambda c: c["runs"][str(PRODUCER_RUN)]
                .update({"head_branch": "topic"}),
                reason="producer-ref")
    select_case("select_wrong_event",
                ctx=context_json(CONSUMER_RUN, 501, "workflow_dispatch"),
                reason="consumer-event")
    select_case("select_own_attempt2",
                mutate_cfg=lambda c: c["runs"][str(CONSUMER_RUN)]
                .update({"run_attempt": 2}),
                ctx=context_json(CONSUMER_RUN, 501, "schedule", attempt=2),
                reason="own-run-attempt-one")
    select_case("select_producer_attempt2",
                mutate_cfg=lambda c: c["runs"][str(PRODUCER_RUN)]
                .update({"run_attempt": 2}),
                activation=json.dumps({
                    "producer": {"runId": PRODUCER_RUN, "runNumber": 500,
                                 "runAttempt": 2, "artifactId": ARTIFACT_ID,
                                 "artifactDigest": "sha256:" + "0" * 64},
                    "consumer": {"runNumber": 501, "event": "schedule"}}),
                reason="producer-rerun")
    select_case("select_producer_failed",
                mutate_cfg=lambda c: c["runs"][str(PRODUCER_RUN)]
                .update({"conclusion": "failure"}),
                reason="producer-conclusion")
    select_case("select_producer_cancelled",
                mutate_cfg=lambda c: c["runs"][str(PRODUCER_RUN)]
                .update({"conclusion": "cancelled"}),
                reason="producer-conclusion")
    select_case("select_producer_missing",
                mutate_cfg=lambda c: c["runs"].pop(str(PRODUCER_RUN)),
                reason="producer-run-absent")
    select_case("select_fork_producer",
                mutate_cfg=lambda c: c["runs"][str(PRODUCER_RUN)]
                .update({"head_repository": {"id": 4242}}),
                reason="producer-fork")
    select_case("select_artifact_missing",
                mutate_cfg=lambda c: c["artifacts"].pop(str(ARTIFACT_ID)),
                reason="artifact-not-unique")
    select_case("select_artifact_duplicate",
                mutate_cfg=lambda c: (
                    c["artifacts"].update({"881": artifact_record(881)}),
                    c["run_artifacts"].update(
                        {str(PRODUCER_RUN): [ARTIFACT_ID, 881]})),
                reason="artifact-not-unique")
    select_case("select_artifact_wrong_run",
                mutate_cfg=lambda c: c["artifacts"][str(ARTIFACT_ID)]
                .update({"workflow_run": {"id": 3333, "repository_id": 7001,
                                          "head_repository_id": 7001,
                                          "head_branch": "main",
                                          "head_sha": SHA_B}}),
                reason="artifact-wrong-run")
    select_case("select_artifact_foreign_repo",
                mutate_cfg=lambda c: c["artifacts"][str(ARTIFACT_ID)]
                ["workflow_run"].update({"head_repository_id": 4242}),
                reason="artifact-fork")
    select_case("select_artifact_expired",
                mutate_cfg=lambda c: c["artifacts"][str(ARTIFACT_ID)]
                .update({"expired": True}),
                reason="artifact-expired")
    select_case("select_artifact_past_expiry",
                mutate_cfg=lambda c: c["artifacts"][str(ARTIFACT_ID)]
                .update({"expires_at": "2020-01-01T00:00:00Z"}),
                reason="artifact-expiry")
    select_case("select_artifact_oversize",
                mutate_cfg=lambda c: c["artifacts"][str(ARTIFACT_ID)]
                .update({"size_in_bytes": 6 * 1024 * 1024}),
                reason="artifact-size")
    select_case("select_digest_mismatch",
                mutate_cfg=lambda c: c["artifacts"][str(ARTIFACT_ID)]
                .update({"digest": "sha256:" + "f" * 64}),
                reason="artifact-digest-mismatch")
    select_case("select_activation_slot",
                activation=activation_json(consumer=999),
                reason="activation-slot")
    select_case("select_activation_event",
                activation=activation_json(event="workflow_dispatch"),
                reason="activation-event")
    select_case("select_activation_malformed",
                activation='{"producer":', reason="cs_activation-json")
    select_case("select_activation_unknown_key",
                activation=json.dumps({
                    "producer": {"runId": PRODUCER_RUN, "runNumber": 500,
                                 "runAttempt": 1, "artifactId": ARTIFACT_ID,
                                 "artifactDigest": "sha256:" + "0" * 64},
                    "consumer": {"runNumber": 501, "event": "schedule"},
                    "campaignLineage": "campaign.evil"}),
                reason="activation-fields")
    select_case("select_no_activation", activation=None,
                reason="CS_ACTIVATION-absent")
    select_case("select_wrong_number",
                activation=activation_json(number=499),
                reason="producer-run-number")
    select_case("select_wrong_name",
                mutate_cfg=lambda c: c["artifacts"][str(ARTIFACT_ID)]
                .update({"name": "other"}),
                reason="artifact-not-unique")

    # -- verify ------------------------------------------------------------
    def verify_setup(r, handoff=None, checkpoint=b'{"state":1}', cfg=None):
        cfg = cfg or base_config()
        handoff = handoff or good_handoff(
            sha=hashlib.sha256(checkpoint).hexdigest(),
            size=len(checkpoint))
        archive = make_zip(json.dumps(handoff).encode(), checkpoint)
        digest = "sha256:" + hashlib.sha256(archive).hexdigest()
        cfg["artifacts"][str(ARTIFACT_ID)]["digest"] = digest
        cfg["artifacts"][str(ARTIFACT_ID)]["size_in_bytes"] = len(archive)
        r.configure(cfg)
        env = select_env(activation=activation_json(digest=digest))
        sel_dir = os.path.join(r.workdir, "sel")
        r.run_helper(["select", "--workdir", sel_dir], env)
        arch = os.path.join(r.workdir, "in.zip")
        with open(arch, "wb") as fh:
            fh.write(archive)
        return env, os.path.join(sel_dir, "selection.json"), arch


    def verify_args(sel, arch, wd):
        return ["verify", "--archive", arch, "--selection", sel,
                "--state-dir", os.path.join(wd, "state")]

    @leg("verify_ok")
    def _():
        env, sel, arch = verify_setup(r)
        r.run_helper(verify_args(sel, arch, r.workdir), env)
        state = r.read_output("state-path")
        assert state and os.path.exists(state)
        if os.name == "posix":
            assert oct(os.stat(state).st_mode & 0o777) == "0o600"

    def verify_case(name, reason, checkpoint=b'{"state":1}',
                    handoff_mut=None, zip_mut=None):
        @leg(name)
        def _():
            handoff = good_handoff(
                sha=hashlib.sha256(checkpoint).hexdigest(),
                size=len(checkpoint))
            if handoff_mut:
                handoff_mut(handoff)
            if zip_mut:
                archive = zip_mut(handoff, checkpoint)
            else:
                archive = make_zip(json.dumps(handoff).encode(), checkpoint)
            cfg = base_config()
            digest = "sha256:" + hashlib.sha256(archive).hexdigest()
            cfg["artifacts"][str(ARTIFACT_ID)]["digest"] = digest
            cfg["artifacts"][str(ARTIFACT_ID)]["size_in_bytes"] = len(archive)
            r.configure(cfg)
            env = select_env(activation=activation_json(digest=digest))
            sel_dir = os.path.join(r.workdir, "sel")
            r.run_helper(["select", "--workdir", sel_dir], env)
            arch = os.path.join(r.workdir, "in.zip")
            with open(arch, "wb") as fh:
                fh.write(archive)
            r.run_helper(verify_args(os.path.join(sel_dir, "selection.json"),
                                     arch, r.workdir), env, ok=False,
                         reason=reason)

    verify_case("verify_tampered_checkpoint", "checkpoint-digest",
                handoff_mut=lambda h: h["checkpoint"]
                .update({"sha256": "f" * 64}))
    verify_case("verify_extra_member", "zip-count",
                zip_mut=lambda h, c: _zip_with_extra(h, c))

    @leg("verify_duplicate_member")
    def _():
        checkpoint = b'{"state":1}'
        handoff = good_handoff(sha=hashlib.sha256(checkpoint).hexdigest(),
                               size=len(checkpoint))
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as zf:
            zf.writestr("handoff.json", json.dumps(handoff))
            zf.writestr("checkpoint.json", checkpoint)
            zf.writestr("checkpoint.json", checkpoint)
        archive = buf.getvalue()
        cfg = base_config()
        digest = "sha256:" + hashlib.sha256(archive).hexdigest()
        cfg["artifacts"][str(ARTIFACT_ID)]["digest"] = digest
        cfg["artifacts"][str(ARTIFACT_ID)]["size_in_bytes"] = len(archive)
        r.configure(cfg)
        env = select_env(activation=activation_json(digest=digest))
        sel = os.path.join(r.workdir, "sel")
        r.run_helper(["select", "--workdir", sel], env)
        arch = os.path.join(r.workdir, "in.zip")
        with open(arch, "wb") as fh:
            fh.write(archive)
        r.run_helper(verify_args(os.path.join(sel, "selection.json"),
                                 arch, r.workdir), env, ok=False,
                     reason="zip-count")

    @leg("verify_symlink_member")
    def _():
        checkpoint = b'{"state":1}'
        handoff = good_handoff(sha=hashlib.sha256(checkpoint).hexdigest(),
                               size=len(checkpoint))
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as zf:
            zf.writestr("handoff.json", json.dumps(handoff))
            info = zipfile.ZipInfo("checkpoint.json")
            info.external_attr = (0o120000 | 0o777) << 16  # S_IFLNK
            zf.writestr(info, checkpoint)
        archive = buf.getvalue()
        cfg = base_config()
        digest = "sha256:" + hashlib.sha256(archive).hexdigest()
        cfg["artifacts"][str(ARTIFACT_ID)]["digest"] = digest
        cfg["artifacts"][str(ARTIFACT_ID)]["size_in_bytes"] = len(archive)
        r.configure(cfg)
        env = select_env(activation=activation_json(digest=digest))
        sel = os.path.join(r.workdir, "sel")
        r.run_helper(["select", "--workdir", sel], env)
        arch = os.path.join(r.workdir, "in.zip")
        with open(arch, "wb") as fh:
            fh.write(archive)
        r.run_helper(verify_args(os.path.join(sel, "selection.json"),
                                 arch, r.workdir), env, ok=False,
                     reason="zip-member-type")
    verify_case("verify_seal_run", "handoff-seal",
                handoff_mut=lambda h: h["consumer"]
                .update({"runNumber": 999}))
    verify_case("verify_seal_event", "handoff-seal",
                handoff_mut=lambda h: h["consumer"]
                .update({"event": "workflow_dispatch"}))
    verify_case("verify_lineage_absent", "handoff-fields",
                handoff_mut=lambda h: h
                .update({"campaignLineage": "campaign.evil"}))
    verify_case("verify_malformed_handoff", "handoff-json",
                zip_mut=lambda h, c: make_zip(b"{not json", c))

    @leg("verify_dir_member")
    def _():
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as zf:
            zf.writestr("evil/", "")
            zf.writestr("handoff.json", "{}")
        archive = buf.getvalue()
        cfg = base_config()
        digest = "sha256:" + hashlib.sha256(archive).hexdigest()
        cfg["artifacts"][str(ARTIFACT_ID)]["digest"] = digest
        r.configure(cfg)
        env = select_env(activation=activation_json(digest=digest))
        sel = os.path.join(r.workdir, "sel")
        r.run_helper(["select", "--workdir", sel], env)
        arch = os.path.join(r.workdir, "in.zip")
        with open(arch, "wb") as fh:
            fh.write(archive)
        r.run_helper(verify_args(os.path.join(sel, "selection.json"),
                                 arch, r.workdir), env, ok=False,
                     reason="zip-member")

    @leg("verify_traversal_member")
    def _():
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as zf:
            zf.writestr("../handoff.json", "{}")
            zf.writestr("checkpoint.json", b"x")
        archive = buf.getvalue()
        cfg = base_config()
        digest = "sha256:" + hashlib.sha256(archive).hexdigest()
        cfg["artifacts"][str(ARTIFACT_ID)]["digest"] = digest
        r.configure(cfg)
        env = select_env(activation=activation_json(digest=digest))
        sel = os.path.join(r.workdir, "sel")
        r.run_helper(["select", "--workdir", sel], env)
        arch = os.path.join(r.workdir, "in.zip")
        with open(arch, "wb") as fh:
            fh.write(archive)
        r.run_helper(verify_args(os.path.join(sel, "selection.json"),
                                 arch, r.workdir), env, ok=False,
                     reason="zip-member")

    @leg("verify_oversize_member")
    def _():
        # Member compresses below the artifact bound; the zip entry's
        # declared uncompressed size exceeds the checkpoint bound.
        checkpoint = b"x" * (4 * 1024 * 1024 + 1)
        handoff = good_handoff(sha=hashlib.sha256(checkpoint).hexdigest(),
                               size=len(checkpoint))
        archive = make_zip(json.dumps(handoff).encode(), checkpoint)
        cfg = base_config()
        digest = "sha256:" + hashlib.sha256(archive).hexdigest()
        cfg["artifacts"][str(ARTIFACT_ID)]["digest"] = digest
        cfg["artifacts"][str(ARTIFACT_ID)]["size_in_bytes"] = len(archive)
        r.configure(cfg)
        env = select_env(activation=activation_json(digest=digest))
        sel = os.path.join(r.workdir, "sel")
        r.run_helper(["select", "--workdir", sel], env)
        arch = os.path.join(r.workdir, "in.zip")
        with open(arch, "wb") as fh:
            fh.write(archive)
        r.run_helper(verify_args(os.path.join(sel, "selection.json"),
                                 arch, r.workdir), env, ok=False,
                     reason="zip-member-size")

    @leg("verify_stale_seal")
    def _():
        # After the sealed slot passed, re-selecting the original artifact
        # for a later run must stop: handoff seal says 501, current run is
        # 502, activation claims 502. This is the missed/intervening-slot
        # stop — only a new activation naming an authenticated successor
        # could continue the chain.
        cfg = base_config()
        cfg["runs"][str(CONSUMER_RUN)] = run_record(
            CONSUMER_RUN, 502, "schedule", status="in_progress",
            conclusion=None, head_sha=SHA_A)
        checkpoint = b'{"state":1}'
        handoff = good_handoff(consumer_number=501,
                               sha=hashlib.sha256(checkpoint).hexdigest(),
                               size=len(checkpoint))
        archive = make_zip(json.dumps(handoff).encode(), checkpoint)
        digest = "sha256:" + hashlib.sha256(archive).hexdigest()
        cfg["artifacts"][str(ARTIFACT_ID)]["digest"] = digest
        cfg["artifacts"][str(ARTIFACT_ID)]["size_in_bytes"] = len(archive)
        r.configure(cfg)
        ctx = context_json(CONSUMER_RUN, 502, "schedule")
        env = r.env(ctx=ctx,
                    activation=activation_json(digest=digest, consumer=502))
        sel = os.path.join(r.workdir, "sel")
        r.run_helper(["select", "--workdir", sel], env)
        arch = os.path.join(r.workdir, "in.zip")
        with open(arch, "wb") as fh:
            fh.write(archive)
        r.run_helper(verify_args(os.path.join(sel, "selection.json"),
                                 arch, r.workdir), env, ok=False,
                     reason="handoff-seal")

    @leg("select_lost_upload")
    def _():
        # The consumed producer ran to a successful conclusion but its
        # successor upload was lost: the run owns no handoff artifact, so
        # the activation's named artifact is absent and the chain stops.
        cfg = base_config()
        cfg["run_artifacts"][str(PRODUCER_RUN)] = []
        r.configure(cfg)
        env = select_env(activation=activation_json())
        r.run_helper(select_args(r.workdir), env, ok=False,
                     reason="artifact-not-unique")

    @leg("verify_archive_digest")
    def _():
        env, sel, arch = verify_setup(r)
        with open(arch, "wb") as fh:
            fh.write(b"corrupted-bytes")
        r.run_helper(verify_args(sel, arch, r.workdir), env, ok=False,
                     reason="archive-digest")

    # -- emit --------------------------------------------------------------
    def emit_env(event="workflow_dispatch", attempt=1, branch="main",
                 number=500):
        cfg = base_config()
        cfg["runs"] = {str(CONSUMER_RUN): run_record(
            CONSUMER_RUN, number, event, run_attempt=attempt,
            head_branch=branch, status="in_progress", conclusion=None,
            head_sha=SHA_A)}
        r.configure(cfg)
        return r.env(ctx=context_json(CONSUMER_RUN, number, event,
                                      ref="refs/heads/" + branch,
                                      attempt=attempt))

    @leg("emit_ok")
    def _():
        env = emit_env()
        cp = os.path.join(r.workdir, "cp.json")
        with open(cp, "wb") as fh:
            fh.write(b'{"campaign":1}')
        out = os.path.join(r.workdir, "hand")
        r.run_helper(["emit", "--checkpoint", cp, "--handoff-dir", out], env)
        handoff = json.load(open(os.path.join(out, "handoff.json")))
        assert handoff["consumer"]["runNumber"] == 501
        assert handoff["consumer"]["event"] == "schedule"
        assert handoff["producer"]["runNumber"] == 500
        assert handoff["producer"]["event"] == "workflow_dispatch"
        assert "campaignLineage" not in handoff
        assert open(os.path.join(out, "checkpoint.json"),
                    "rb").read() == b'{"campaign":1}'

    @leg("emit_attempt2")
    def _():
        env = emit_env(attempt=2)
        cp = os.path.join(r.workdir, "cp.json")
        with open(cp, "wb") as fh:
            fh.write(b"{}")
        r.run_helper(["emit", "--checkpoint", cp,
                      "--handoff-dir", os.path.join(r.workdir, "h")], env,
                     ok=False, reason="own-run-attempt-one")

    @leg("emit_wrong_branch")
    def _():
        env = emit_env(branch="topic")
        cp = os.path.join(r.workdir, "cp.json")
        with open(cp, "wb") as fh:
            fh.write(b"{}")
        r.run_helper(["emit", "--checkpoint", cp,
                      "--handoff-dir", os.path.join(r.workdir, "h")], env,
                     ok=False, reason="own-run-branch")

    @leg("emit_oversize_checkpoint")
    def _():
        env = emit_env()
        cp = os.path.join(r.workdir, "cp.json")
        with open(cp, "wb") as fh:
            fh.write(b"x" * (4 * 1024 * 1024 + 1))
        r.run_helper(["emit", "--checkpoint", cp,
                      "--handoff-dir", os.path.join(r.workdir, "h")], env,
                     ok=False, reason="checkpoint-oversize")

    # -- gate --------------------------------------------------------------
    @leg("gate_ok")
    def _():
        env = emit_env()
        r.run_helper(["gate"], env)

    @leg("gate_attempt2")
    def _():
        env = emit_env(attempt=2)
        r.run_helper(["gate"], env, ok=False, reason="own-run-attempt-one")

    @leg("gate_wrong_event")
    def _():
        cfg = base_config()
        cfg["runs"] = {str(CONSUMER_RUN): run_record(
            CONSUMER_RUN, 500, "schedule", status="in_progress",
            conclusion=None, head_sha=SHA_A)}
        r.configure(cfg)
        env = r.env(ctx=context_json(CONSUMER_RUN, 500, "schedule"))
        r.run_helper(["gate"], env, ok=False, reason="gate-event")

    # -- request -----------------------------------------------------------
    def request_env(campaign=CAMPAIGN, ctx=None):
        return r.env(ctx=ctx or context_json(CONSUMER_RUN, 501, "schedule"),
                     campaign=campaign)

    @leg("request_ok")
    def _():
        env = request_env()
        out = os.path.join(r.workdir, "req.json")
        r.run_helper(["request", "--base-oid", SHA_A,
                      "--state", os.path.join(r.workdir, "state.json"),
                      "--out", out], env)
        req = json.load(open(out))
        gh = req["github"]
        assert req["githubProposalRequestVersion"] == 1
        assert gh["expectedBaseCommitOid"] == SHA_A
        assert gh["transition"] == "initial"
        assert gh["policy"]["maximumDocumentationBlocks"] == 8

    @leg("request_bad_oid")
    def _():
        env = request_env()
        r.run_helper(["request", "--base-oid", "nothex",
                      "--state", os.path.join(r.workdir, "s.json"),
                      "--out", os.path.join(r.workdir, "r.json")], env,
                     ok=False, reason="base-oid")

    @leg("request_repo_mismatch")
    def _():
        campaign = json.loads(CAMPAIGN)
        campaign["repositoryOwner"] = "SomeoneElse"
        env = request_env(campaign=json.dumps(campaign))
        r.run_helper(["request", "--base-oid", SHA_A,
                      "--state", os.path.join(r.workdir, "s.json"),
                      "--out", os.path.join(r.workdir, "r.json")], env,
                     ok=False, reason="campaign-repo")

    @leg("request_unknown_field")
    def _():
        campaign = json.loads(CAMPAIGN)
        campaign["extra"] = 1
        env = request_env(campaign=json.dumps(campaign))
        r.run_helper(["request", "--base-oid", SHA_A,
                      "--state", os.path.join(r.workdir, "s.json"),
                      "--out", os.path.join(r.workdir, "r.json")], env,
                     ok=False, reason="campaign-fields")

    @leg("request_lineage_colon")
    def _():
        # The product lineage grammar permits ':'; the transport helper
        # must not narrow it - C# owns semantic acceptance.
        env = request_env()
        env["CS_CAMPAIGN_LINEAGE"] = "campaign:docs.daily"
        out = os.path.join(r.workdir, "req.json")
        r.run_helper(["request", "--base-oid", SHA_A,
                      "--state", os.path.join(r.workdir, "s.json"),
                      "--out", out], env)
        with open(out, encoding="utf-8") as fh:
            req = json.load(fh)
        assert req["campaignLineage"] == "campaign:docs.daily"

    @leg("request_bad_lineage")
    def _():
        env = request_env()
        env["CS_CAMPAIGN_LINEAGE"] = "bad/lineage"
        r.run_helper(["request", "--base-oid", SHA_A,
                      "--state", os.path.join(r.workdir, "s.json"),
                      "--out", os.path.join(r.workdir, "r.json")], env,
                     ok=False, reason="campaign-lineage")

    @leg("request_no_lineage")
    def _():
        env = request_env()
        env.pop("CS_CAMPAIGN_LINEAGE")
        r.run_helper(["request", "--base-oid", SHA_A,
                      "--state", os.path.join(r.workdir, "s.json"),
                      "--out", os.path.join(r.workdir, "r.json")], env,
                     ok=False, reason="campaign-lineage")

    @leg("request_lineage_in_campaign")
    def _():
        # campaignLineage inside the claims document is a closed-shape
        # violation - the lineage authority is the separate env var.
        campaign = json.loads(CAMPAIGN)
        campaign["campaignLineage"] = "campaign.example"
        env = request_env(campaign=json.dumps(campaign))
        r.run_helper(["request", "--base-oid", SHA_A,
                      "--state", os.path.join(r.workdir, "s.json"),
                      "--out", os.path.join(r.workdir, "r.json")], env,
                     ok=False, reason="campaign-fields")

    # -- activation render -------------------------------------------------
    @leg("activation_ok")
    def _():
        env = emit_env()
        cp = os.path.join(r.workdir, "cp.json")
        with open(cp, "wb") as fh:
            fh.write(b'{"campaign":1}')
        out = os.path.join(r.workdir, "hand")
        r.run_helper(["emit", "--checkpoint", cp, "--handoff-dir", out], env)
        env2 = dict(env)
        proc = r.run_helper(["activation", "--handoff-dir", out,
                             "--artifact-id", "42",
                             "--artifact-digest", "1" * 64],
                            env2)
        doc = json.loads(proc.stdout.strip().splitlines()[0])
        assert doc["producer"]["artifactId"] == 42
        assert doc["producer"]["artifactDigest"] == "sha256:" + "1" * 64
        assert doc["consumer"]["runNumber"] == 501

    @leg("activation_prefixed_digest")
    def _():
        env = emit_env()
        cp = os.path.join(r.workdir, "cp.json")
        with open(cp, "wb") as fh:
            fh.write(b'{"campaign":1}')
        out = os.path.join(r.workdir, "hand")
        r.run_helper(["emit", "--checkpoint", cp, "--handoff-dir", out], env)
        proc = r.run_helper(["activation", "--handoff-dir", out,
                             "--artifact-id", "43",
                             "--artifact-digest", "sha256:" + "2" * 64],
                            dict(env))
        doc = json.loads(proc.stdout.strip().splitlines()[0])
        assert doc["producer"]["artifactDigest"] == "sha256:" + "2" * 64


def main():
    workdir = sys.argv[2]
    only = None
    if "--only" in sys.argv:
        only = re.compile(sys.argv[sys.argv.index("--only") + 1])
    r = Runner(workdir)
    register(r)
    r.start()
    passed = failed = 0
    try:
        for name, fn in r.legs:
            if only and not only.search(name):
                continue
            try:
                fn()
                print(f"PASS {name}")
                passed += 1
            except Exception as error:
                print(f"FAIL {name}: {error}")
                failed += 1
    finally:
        r.stop()
    print(f"driver: {passed} passed, {failed} failed")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
