#!/usr/bin/env bash
# verify-release.sh — offline verification matrix for M6-R1 release tooling.
#
# Every leg runs against loopback fakes / local git fixtures; no real
# GitHub mutation is possible (the release scripts' only API root override
# is the gated loopback seam). Layout mirrors tests/action/verify-action.sh.
#
#   bash tests/release/verify-release.sh [--work DIR] [--keep]
#                                        [--only REGEX] [--real]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
RELEASE_SCRIPTS="$REPO_ROOT/scripts/release"
FAKE="$SCRIPT_DIR/fake_release_github.py"

WORK=""
KEEP=0
ONLY=""
REAL=0
while [ $# -gt 0 ]; do
    case "$1" in
        --work) WORK="$2"; shift 2 ;;
        --keep) KEEP=1; shift ;;
        --only) ONLY="$2"; shift 2 ;;
        --real) REAL=1; shift ;;
        *) echo "usage: $0 [--work DIR] [--keep] [--only REGEX] [--real]" >&2
           exit 2 ;;
    esac
done

if [ -z "$WORK" ]; then
    WORK="$(mktemp -d "${TMPDIR:-/tmp}/contract-scribe-release.XXXXXX")"
    [ "$KEEP" -eq 1 ] || trap 'rm -rf "$WORK"' EXIT
else
    mkdir -p "$WORK"
fi
LOGS="$WORK/logs"
mkdir -p "$LOGS"

CASES_RUN=0
CASES_FAILED=0
FAILED_CASES=()
run_case() {
    local name="$1" fn="$2"
    if [ -n "$ONLY" ] && ! grep -qE "$ONLY" <<<"$name"; then
        return 0
    fi
    CASES_RUN=$((CASES_RUN + 1))
    local log="$LOGS/$name.log"
    # NOTE: the subshell must NOT sit in an if/&& condition — that context
    # disables -e inside it and lets failed steps run on.
    set +e
    (set -euo pipefail; "$fn") >"$log" 2>&1
    local rc=$?
    set -e
    if [ "$rc" -eq 0 ]; then
        echo "PASS $name"
    else
        echo "FAIL $name  (log: $log)"
        CASES_FAILED=$((CASES_FAILED + 1))
        FAILED_CASES+=("$name")
    fi
}

expect_fail() {
    if "$@"; then echo "expected failure, got success" >&2; return 1; fi
    return 0
}

RELEASE_TOKEN="contract-scribe-synthetic-release-only"
READ_TOKEN="contract-scribe-synthetic-read-only"
WF_SHA="wf00000000000000000000000000000000000wf"
REPO_ID="424242"
RUN_ID="900001"

# ---------------------------------------------------------------------------
# Fixture: a local "remote" + clone so origin/main ancestry is real.
# ---------------------------------------------------------------------------

REMOTE="$WORK/remote.git"
FIXTURE="$WORK/fixture"
git init --bare -q "$REMOTE"
git init -q "$FIXTURE"
git -C "$FIXTURE" config user.email test@example.invalid
git -C "$FIXTURE" config user.name test
mkdir -p "$FIXTURE/scripts/action" "$FIXTURE/scripts/release"
cat > "$FIXTURE/action.yml" <<'EOF'
name: contract-scribe
runs:
  using: composite
EOF
cat > "$FIXTURE/scripts/action/payload-map.json" <<'EOF'
{"mapVersion": 1, "wrapper": "contract-scribe-action", "payload": null}
EOF
echo "stub" > "$FIXTURE/scripts/release/build-payload.sh"
git -C "$FIXTURE" add -A
git -C "$FIXTURE" commit -qm base
git -C "$FIXTURE" branch -M main
git -C "$FIXTURE" remote add origin "$REMOTE"
git -C "$FIXTURE" push -q origin main
BASE_REV="$(git -C "$FIXTURE" rev-parse HEAD)"

# A side commit that never lands on main -> non-main-reachable fixture.
git -C "$FIXTURE" checkout -qb side
echo side > "$FIXTURE/side.txt"
git -C "$FIXTURE" add side.txt
git -C "$FIXTURE" commit -qm side
SIDE_REV="$(git -C "$FIXTURE" rev-parse HEAD)"
git -C "$FIXTURE" checkout -q main

# ---------------------------------------------------------------------------
# Stub builder: deterministic tiny payload standing in for the A1 build.
# Runs with cwd = the detached payload worktree.
# ---------------------------------------------------------------------------

STUB="$WORK/stub-builder.sh"
cat > "$STUB" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
OUT=""; BWORK=""
while [ $# -gt 0 ]; do
    case "$1" in
        --output) OUT="$2"; shift 2 ;;
        --work) BWORK="$2"; shift 2 ;;
    esac
done
mkdir -p "$OUT" "$BWORK/stage"
REV="$(git rev-parse HEAD)"
[ -n "${STUB_MISMANIFEST:-}" ] && REV="0$REV"
TV="0.1.0-dev+$REV"
TOP="contract-scribe-$TV-linux-x64"
mkdir -p "$BWORK/stage/$TOP"
printf '{"libraries":{"Lib.A/1.2.3":{"type":"package"},"App/1.0.0":{"type":"project"}}}\n' \
    > "$BWORK/stage/$TOP/ContractScribe.Cli.deps.json"
printf 'payload-bytes\n' > "$BWORK/stage/$TOP/payload.bin"
DEFAULTS_SHA="$(printf 'defaults' | sha256sum | cut -d' ' -f1)"
python3 - "$BWORK/stage/$TOP" "$TV" "$REV" "$DEFAULTS_SHA" <<'PY'
import json, sys
top, tv, rev, dsha = sys.argv[1:5]
manifest = {"payloadFormat": 1, "tool": "contract-scribe",
            "distributionChannel": "d2-framework-dependent-dll",
            "toolVersion": tv, "sourceRevision": rev, "cleanCheckout": True,
            "runtimeIdentifier": "linux-x64", "targetFramework": "net10.0",
            "entrypoint": "ContractScribe.Cli.dll",
            "defaultsJsonSha256": dsha,
            "bounds": {}, "files": []}
with open(top + "/payload.json", "w", encoding="utf-8", newline="\n") as fh:
    json.dump(manifest, fh, indent=2)
    fh.write("\n")
PY
tar -C "$BWORK/stage" --sort=name --mtime="@0" --owner=0 --group=0 \
    --numeric-owner -cf - "$TOP" | gzip -n > "$OUT/$TOP.tar.gz"
(
    cd "$OUT"
    SHA="$(sha256sum "$TOP.tar.gz" | cut -d' ' -f1)"
    [ -n "${STUB_BADSIDECAR:-}" ] && SHA="0$SHA"
    printf '%s  %s\n' "$SHA" "$TOP.tar.gz" > "$TOP.sha256"
)
cp "$BWORK/stage/$TOP/payload.json" "$OUT/$TOP.payload.json"
EOF

# Fake state: reset between legs by rewriting the state file.
STATE="$WORK/fake-state.json"
state_init() {
    python3 - "$STATE" "$FIXTURE" "$REPO_ID" "$RUN_ID" "$WF_SHA" <<'PY'
import json, sys
state_path, fixture, repo_id, run_id, wf_sha = sys.argv[1:6]
head = __import__("subprocess").check_output(
    ["git", "-C", fixture, "rev-parse", "main"], text=True).strip()
state = {
    "repository": {"id": int(repo_id), "full_name": "SolusQuest/contract-scribe"},
    "expected_release_token": "contract-scribe-synthetic-release-only",
    "expected_read_token": "contract-scribe-synthetic-read-only",
    "releases": [],
    "refs": {},
    "runs": [{"id": int(run_id), "event": "workflow_dispatch",
              "path": ".github/workflows/release.yml",
              "status": "completed", "conclusion": "success",
              "head_branch": "main", "head_sha": head,
              "run_attempt": 1,
              "repository": {"id": int(repo_id),
                             "full_name": "SolusQuest/contract-scribe"},
              "head_repository": {"id": int(repo_id)}}],
    "artifacts": [{"run_id": int(run_id), "id": 777,
                   "name": "release-candidate", "expired": False,
                   "digest": "sha256:" + "0" * 64,
                   "workflow_run": {"id": int(run_id), "head_sha": head,
                                    "head_repository_id": int(repo_id)}}],
    "ci_runs": {},
    "overrides": {"drop_after_write": [], "force_status": {},
                  "emit_digest_field": True},
}
with open(state_path, "w", encoding="utf-8") as fh:
    json.dump(state, fh)
PY
}
state_init

python3 - "$STATE" <<'PY'
import json, sys
s = json.load(open(sys.argv[1]))
s["ci_runs"][s["runs"][0]["head_sha"]] = [
    {"id": 888, "conclusion": "success", "run_attempt": 1,
     "jobs": [{"name": "action_packaged", "status": "completed",
               "conclusion": "success"},
              {"name": "validate", "status": "completed",
               "conclusion": "success"}]}]
json.dump(s, open(sys.argv[1] + ".tmp", "w"))
import os; os.replace(sys.argv[1] + ".tmp", sys.argv[1])
PY

touch "$WORK/fake-requests.log"
python3 "$FAKE" "$STATE" "$WORK/fake-ready.json" "$WORK/fake-requests.log" &
FAKE_PID=$!
for _ in $(seq 50); do [ -f "$WORK/fake-ready.json" ] && break; sleep 0.1; done
API_ROOT="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["api"])' "$WORK/fake-ready.json")"

state_edit() {
    # $1 = python expression applied to the state dict (var `s`)
    python3 - "$STATE" "$1" <<'PY'
import json, os, sys
path, code = sys.argv[1], sys.argv[2]
s = json.load(open(path))
exec(code)
tmp = path + ".tmp"
json.dump(s, open(tmp, "w"))
os.replace(tmp, path)
import time; time.sleep(0.15)
PY
}

prepare_env() {
    # Shared prepare env; $1 = extra env assignments (k=v, space separated)
    env \
        CONTRACTSCRIBE_RELEASE_TEST=1 \
        CONTRACTSCRIBE_RELEASE_TEST_BUILDER="$STUB" \
        GITHUB_ACTIONS=true GITHUB_REPOSITORY=SolusQuest/contract-scribe \
        GITHUB_SHA="$(git -C "$FIXTURE" rev-parse main)" \
        GITHUB_RUN_ID="$RUN_ID" GITHUB_RUN_ATTEMPT=1 \
        GITHUB_ACTOR=maintainer GITHUB_WORKFLOW_SHA="$WF_SHA" \
        GITHUB_REPOSITORY_ID="$REPO_ID" \
        RUNNER_OS=Linux RUNNER_ARCH=X64 $1 \
        python3 "$RELEASE_SCRIPTS/prepare-candidate.py" "${@:2}"
}

run_prepare() {
    # $1 = out dir, $2 = source rev, $3 = payload rev, $4 = version
    prepare_env "" --repo "$FIXTURE" --output "$1" \
        --source-revision "$2" --payload-source-revision "$3" \
        --release-version "${4:-v0.1.0}" --wrapper contract-scribe-action
}

CAND="$WORK/candidate-base"
run_prepare "$CAND" "$BASE_REV" "$BASE_REV" "v0.1.0" >"$WORK/prepare-base.log" 2>&1 || {
    echo "FAIL base prepare (log: $WORK/prepare-base.log)"; exit 1; }
CAND_DIGEST="$(cut -d' ' -f1 "$CAND/candidate.sha256")"
ASSET_NAME="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["identity"]["assetName"])' "$CAND/candidate.json")"
ASSET_SHA="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["identity"]["archiveSha256"])' "$CAND/candidate.json")"
TV="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["identity"]["releaseTag"])' "$CAND/candidate.json")"
REL_TAG="$TV"

# Commit 2: record the authorized pair on main (the map-PR step).
python3 - "$FIXTURE" "$CAND/payload-map.json" <<'PY'
import shutil, sys
shutil.copy(sys.argv[2], sys.argv[1] + "/scripts/action/payload-map.json")
PY
git -C "$FIXTURE" add scripts/action/payload-map.json
git -C "$FIXTURE" commit -qm "record authorized pair"
git -C "$FIXTURE" push -q origin main
MAP_REV="$(git -C "$FIXTURE" rev-parse main)"

# Final candidate bound to the merged wrapper revision (the R2/R3 one).
CAND2="$WORK/candidate-final"
run_prepare "$CAND2" "$MAP_REV" "$BASE_REV" "v0.1.0" >"$WORK/prepare-final.log" 2>&1 || {
    echo "FAIL final prepare (log: $WORK/prepare-final.log)"; exit 1; }
CAND2_DIGEST="$(cut -d' ' -f1 "$CAND2/candidate.sha256")"

# point_run_state <sha>: pin the fake run/artifact/CI fixtures at the
# head sha the candidate under test was produced from.
fresh_state() {
    state_init
    point_run_state "${1:-$MAP_REV}"
    # keep a cumulative audit trail for the log-* legs
    cat "$WORK/fake-requests.log" >> "$WORK/fake-requests-all.log" 2>/dev/null || true
    : > "$WORK/fake-requests.log"
}

point_run_state() {
    python3 - "$STATE" "$1" <<'PY'
import json, os, sys
path, head = sys.argv[1:3]
s = json.load(open(path))
s["runs"][0]["head_sha"] = head
s["artifacts"][0]["workflow_run"]["head_sha"] = head
s["ci_runs"] = {head: [
    {"id": 888, "conclusion": "success", "run_attempt": 1,
     "jobs": [{"name": "action_packaged", "status": "completed",
               "conclusion": "success"}]}]}
tmp = path + ".tmp"; json.dump(s, open(tmp, "w")); os.replace(tmp, path)
import time; time.sleep(0.15)
PY
}
state_init
point_run_state "$MAP_REV"

release_env() {
    # $1 = "notoken" drops the release credential; $2 = script args
    local extra=""
    if [ "${1:-}" != "notoken" ]; then
        extra="CONTRACTSCRIBE_RELEASE_TOKEN=$RELEASE_TOKEN"
    fi
    env \
        CONTRACTSCRIBE_RELEASE_TEST=1 \
        CONTRACTSCRIBE_RELEASE_TEST_API_ROOT="$API_ROOT" \
        GITHUB_ACTIONS=true GITHUB_REPOSITORY=SolusQuest/contract-scribe \
        GITHUB_WORKFLOW_SHA="$WF_SHA" \
        CONTRACTSCRIBE_RELEASE_READ_TOKEN="$READ_TOKEN" \
        $extra \
        python3 "$RELEASE_SCRIPTS/promote-candidate.py" "${@:2}"
}

vc_args() {
    echo --candidate "$1" --repo "$FIXTURE" \
        --source-revision "$MAP_REV" --payload-source-revision "$BASE_REV" \
        --release-version v0.1.0 --wrapper contract-scribe-action \
        --candidate-digest "$CAND2_DIGEST" --candidate-run-id "$RUN_ID"
}

count_log() { grep -c "$1" "$WORK/fake-requests.log" || true; }
mutations() { grep -cE '^(POST|PATCH|DELETE|PUT)' "$WORK/fake-requests.log" || true; }

# ---------------------------------------------------------------------------
# Static legs
# ---------------------------------------------------------------------------

case_static_pycompile() {
    python3 -m py_compile "$RELEASE_SCRIPTS"/*.py "$SCRIPT_DIR"/*.py
}
case_static_workflow() {
    python3 "$SCRIPT_DIR/check_release_workflow.py"
}
case_static_no_delete() {
    # The release scripts must never issue a DELETE mutation.
    ! grep -nE '"DELETE"|method="DELETE"' "$RELEASE_SCRIPTS"/*.py
}
run_case static-pycompile case_static_pycompile
run_case static-workflow case_static_workflow
run_case static-no-delete-verb case_static_no_delete

# ---------------------------------------------------------------------------
# Prepare legs
# ---------------------------------------------------------------------------

case_prepare_happy() {
    local d="$WORK/p-happy"
    run_prepare "$d" "$MAP_REV" "$BASE_REV" "v0.1.0"
    python3 - "$d" <<'PY'
import json, sys, tarfile
d = sys.argv[1]
c = json.load(open(d + "/candidate.json"))
i = c["identity"]
assert c["candidateVersion"] == 1
assert i["wrapper"]["name"] == "contract-scribe-action"
assert i["wrapper"]["actionYmlName"] == "contract-scribe"
assert i["prerelease"] is False
assert i["releaseVersion"] == "v0.1.0"
assert i["releaseTag"].startswith("payload-0.1.0-dev+")
assert i["dependencyInventory"] == [{"name": "Lib.A", "version": "1.2.3"}]
assert c["provenance"]["runId"] == "900001"
m = json.load(open(d + "/payload-map.json"))
assert m["mapVersion"] == 1 and m["wrapper"] == "contract-scribe-action"
assert m["payload"]["sourceRevision"] == i["payloadSourceRevision"]
assert m["payload"]["sha256"] == i["archiveSha256"]
assert m["payload"]["assets"] == [i["assetName"]]
with tarfile.open(d + "/" + i["assetName"], "r:gz") as t:
    names = t.getnames()
assert not any(n.endswith("payload-map.json") for n in names), \
    "archive must not contain the map (acyclicity)"
PY
}
case_prepare_repeatable() {
    local d="$WORK/p-repeat"
    run_prepare "$d" "$MAP_REV" "$BASE_REV" "v0.1.0"
    # Different run receipt, identical identity -> identical digest.
    prepare_env "GITHUB_RUN_ID=424242 GITHUB_RUN_ATTEMPT=7 GITHUB_ACTOR=other" \
        --repo "$FIXTURE" --output "$WORK/p-repeat2" \
        --source-revision "$MAP_REV" --payload-source-revision "$BASE_REV" \
        --release-version v0.1.0 --wrapper contract-scribe-action
    [ "$(cut -d' ' -f1 "$d/candidate.sha256")" = "$CAND2_DIGEST" ]
    [ "$(cut -d' ' -f1 "$WORK/p-repeat2/candidate.sha256")" = "$CAND2_DIGEST" ]
}
case_prepare_digest_drift() {
    local d="$WORK/p-drift"
    run_prepare "$d" "$MAP_REV" "$BASE_REV" "v9.9.9"
    [ "$(cut -d' ' -f1 "$d/candidate.sha256")" != "$CAND2_DIGEST" ]
}
case_prepare_non_main_reachable() {
    expect_fail run_prepare "$WORK/p-nmr" "$MAP_REV" "$SIDE_REV" "v0.1.0"
}
case_prepare_source_not_reachable() {
    expect_fail run_prepare "$WORK/p-snr" "$SIDE_REV" "$MAP_REV" "v0.1.0"
}
case_prepare_bad_hex() {
    expect_fail run_prepare "$WORK/p-hex" "zzz" "$MAP_REV" "v0.1.0"
}
case_prepare_bad_version() {
    expect_fail run_prepare "$WORK/p-ver" "$MAP_REV" "$BASE_REV" "1.2.3"
    expect_fail run_prepare "$WORK/p-ver2" "$MAP_REV" "$BASE_REV" "v1.2.3.4"
}
case_prepare_wrapper_mismatch() {
    expect_fail prepare_env "" --repo "$FIXTURE" --output "$WORK/p-wm" \
        --source-revision "$MAP_REV" --payload-source-revision "$BASE_REV" \
        --release-version v0.1.0 --wrapper contract-scribe
}
case_prepare_manifest_source_mismatch() {
    expect_fail prepare_env "STUB_MISMANIFEST=1" --repo "$FIXTURE" \
        --output "$WORK/p-mm" --source-revision "$MAP_REV" \
        --payload-source-revision "$BASE_REV" --release-version v0.1.0 \
        --wrapper contract-scribe-action
}
case_prepare_archive_sha_mismatch() {
    expect_fail prepare_env "STUB_BADSIDECAR=1" --repo "$FIXTURE" \
        --output "$WORK/p-sm" --source-revision "$MAP_REV" \
        --payload-source-revision "$BASE_REV" --release-version v0.1.0 \
        --wrapper contract-scribe-action
}
case_prepare_output_nonempty() {
    expect_fail run_prepare "$CAND2" "$MAP_REV" "$BASE_REV" "v0.1.0"
}
run_case prepare-happy case_prepare_happy
run_case prepare-repeatable case_prepare_repeatable
run_case prepare-digest-drift case_prepare_digest_drift
run_case prepare-non-main-reachable case_prepare_non_main_reachable
run_case prepare-source-not-reachable case_prepare_source_not_reachable
run_case prepare-bad-hex case_prepare_bad_hex
run_case prepare-bad-version case_prepare_bad_version
run_case prepare-wrapper-mismatch case_prepare_wrapper_mismatch
run_case prepare-manifest-source-mismatch case_prepare_manifest_source_mismatch
run_case prepare-archive-sha-mismatch case_prepare_archive_sha_mismatch
run_case prepare-output-nonempty case_prepare_output_nonempty

# ---------------------------------------------------------------------------
# resolve-artifact legs
# ---------------------------------------------------------------------------

case_resolve_happy() {
    fresh_state
    release_env x resolve-artifact --candidate-run-id "$RUN_ID" | grep -q 777
}
case_resolve_wrong_event() {
    fresh_state
    state_edit 's["runs"][0]["event"]="push"'
    expect_fail release_env x resolve-artifact --candidate-run-id "$RUN_ID"
}
case_resolve_wrong_workflow() {
    fresh_state
    state_edit 's["runs"][0]["path"]=".github/workflows/ci.yml"'
    expect_fail release_env x resolve-artifact --candidate-run-id "$RUN_ID"
}
case_resolve_run_failed() {
    fresh_state
    state_edit 's["runs"][0]["conclusion"]="failure"'
    expect_fail release_env x resolve-artifact --candidate-run-id "$RUN_ID"
}
case_resolve_two_artifacts() {
    fresh_state
    state_edit 's["artifacts"].append(dict(s["artifacts"][0], id=778))'
    expect_fail release_env x resolve-artifact --candidate-run-id "$RUN_ID"
}
case_resolve_expired() {
    fresh_state
    state_edit 's["artifacts"][0]["expired"]=True'
    expect_fail release_env x resolve-artifact --candidate-run-id "$RUN_ID"
}
case_resolve_no_read_token() {
    fresh_state
    expect_fail env CONTRACTSCRIBE_RELEASE_TEST=1 \
        CONTRACTSCRIBE_RELEASE_TEST_API_ROOT="$API_ROOT" \
        GITHUB_ACTIONS=true GITHUB_REPOSITORY=SolusQuest/contract-scribe \
        python3 "$RELEASE_SCRIPTS/promote-candidate.py" resolve-artifact \
        --candidate-run-id "$RUN_ID"
}
run_case resolve-happy case_resolve_happy
run_case resolve-wrong-event case_resolve_wrong_event
run_case resolve-wrong-workflow case_resolve_wrong_workflow
run_case resolve-run-failed case_resolve_run_failed
run_case resolve-two-artifacts case_resolve_two_artifacts
run_case resolve-expired case_resolve_expired
run_case resolve-no-read-token case_resolve_no_read_token

# ---------------------------------------------------------------------------
# stage-draft legs
# ---------------------------------------------------------------------------

case_stage_happy() {
    fresh_state
    release_env x stage-draft $(vc_args "$CAND2")
    python3 - "$CAND2" <<'PY'
import json, sys
s = json.load(open(sys.argv[1] + "/staged.json"))
assert s["releaseId"] and s["assetId"]
assert s["releaseTag"].startswith("payload-0.1.0-dev+")
PY
    # Draft visible via authenticated enumeration, invisible via by-tag.
    grep -q "GET /repos/SolusQuest/contract-scribe/releases?" \
        "$WORK/fake-requests.log"
    ! grep -q "POST /repos/.*/git/refs" "$WORK/fake-requests.log"
}
case_stage_exact_retry() {
    fresh_state
    release_env x stage-draft $(vc_args "$CAND2")
    local before; before="$(count_log 'POST /repos/.*/releases')"
    release_env x stage-draft $(vc_args "$CAND2")
    local after; after="$(count_log 'POST /repos/.*/releases')"
    [ "$before" = "$after" ]   # adopted, no duplicate create
}
case_stage_ambiguous_create() {
    fresh_state
    state_edit 's["overrides"]["drop_after_write"]=["release-create"]'
    release_env x stage-draft $(vc_args "$CAND2")
    [ "$(count_log 'POST /repos/.*/releases ')" = "1" ]  # single POST, adopted
    state_edit 's["overrides"]["drop_after_write"]=[]'
}
case_stage_published_conflict() {
    fresh_state
    state_edit 's["releases"].append({"id":9100,"tag_name":"'"$REL_TAG"'","target_commitish":"'"$MAP_REV"'","name":"x","body":"b","draft":False,"prerelease":False,"assets":[]})'
    expect_fail release_env x stage-draft $(vc_args "$CAND2")
    state_edit 's["releases"]=[r for r in s["releases"] if r["id"]!=9100]'
}
case_stage_tag_present() {
    fresh_state
    state_edit 's["refs"]["tags/'"$REL_TAG"'"]="'"$MAP_REV"'"'
    expect_fail release_env x stage-draft $(vc_args "$CAND2")
    state_edit 's["refs"].pop("tags/'"$REL_TAG"'")'
}
case_stage_field_mismatch() {
    fresh_state
    state_edit 's["releases"].append({"id":9101,"tag_name":"'"$REL_TAG"'","target_commitish":"'"$MAP_REV"'","name":"wrong","body":"b","draft":True,"prerelease":False,"assets":[]})'
    expect_fail release_env x stage-draft $(vc_args "$CAND2")
    state_edit 's["releases"]=[r for r in s["releases"] if r["id"]!=9101]'
}
case_stage_extra_asset() {
    fresh_state
    state_edit 's["releases"].append({"id":9102,"tag_name":"'"$REL_TAG"'","target_commitish":"'"$MAP_REV"'","name":None,"body":None,"draft":True,"prerelease":False,"assets":[{"id":5,"name":"other.bin","file":"/nope","size":1,"digest":"sha256:"+"0"*64}]})'
    # exact-match on frozen fields fails before the asset set is even read
    expect_fail release_env x stage-draft $(vc_args "$CAND2")
    state_edit 's["releases"]=[r for r in s["releases"] if r["id"]!=9102]'
}
case_stage_no_token() {
    fresh_state
    expect_fail release_env notoken stage-draft $(vc_args "$CAND2")
}
case_stage_upload_drop_after_write() {
    fresh_state
    state_edit 's["overrides"]["drop_after_write"]=["asset-upload"]'
    release_env x stage-draft $(vc_args "$CAND2")
    [ "$(count_log 'POST /uploads/')" = "1" ]   # single upload, adopted
}
case_stage_upload_lost_before_write() {
    fresh_state
    state_edit 's["overrides"]["force_status"]["asset-upload"]=500'
    expect_fail release_env x stage-draft $(vc_args "$CAND2")
}
case_stage_map_null_at_source() {
    # The published tag target must itself contain the authorized pair:
    # the candidate produced at BASE_REV (map still null there) must be
    # rejected even though main's map now holds the pair.
    point_run_state "$BASE_REV"
    expect_fail release_env x stage-draft \
        --candidate "$CAND" --repo "$FIXTURE" \
        --source-revision "$BASE_REV" --payload-source-revision "$BASE_REV" \
        --release-version v0.1.0 --wrapper contract-scribe-action \
        --candidate-digest "$CAND_DIGEST" --candidate-run-id "$RUN_ID"
    point_run_state "$MAP_REV"
}
case_stage_digest_mismatch() {
    fresh_state
    expect_fail release_env x stage-draft \
        --candidate "$CAND2" --repo "$FIXTURE" \
        --source-revision "$MAP_REV" --payload-source-revision "$BASE_REV" \
        --release-version v0.1.0 --wrapper contract-scribe-action \
        --candidate-digest "$(printf '0%.0s' {1..64})" \
        --candidate-run-id "$RUN_ID"
}
case_stage_repo_id_drift() {
    fresh_state
    state_edit 's["repository"]["id"]=999999'
    expect_fail release_env x stage-draft $(vc_args "$CAND2")
}
run_case stage-happy case_stage_happy
run_case stage-exact-retry case_stage_exact_retry
run_case stage-ambiguous-create case_stage_ambiguous_create
run_case stage-published-conflict case_stage_published_conflict
run_case stage-tag-present case_stage_tag_present
run_case stage-field-mismatch case_stage_field_mismatch
run_case stage-extra-asset case_stage_extra_asset
run_case stage-no-token case_stage_no_token
run_case stage-upload-drop-after-write case_stage_upload_drop_after_write
run_case stage-upload-lost-before-write case_stage_upload_lost_before_write
run_case stage-map-null-at-source case_stage_map_null_at_source
run_case stage-digest-mismatch case_stage_digest_mismatch
run_case stage-repo-id-drift case_stage_repo_id_drift

# ---------------------------------------------------------------------------
# promote legs (state reset: stage once, then exercise promote paths)
# ---------------------------------------------------------------------------

stage_once() {
    fresh_state
    release_env x stage-draft $(vc_args "$CAND2") >/dev/null
    QUAL_REL="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["releaseId"])' "$CAND2/staged.json" 2>/dev/null || true)"
    QUAL_ASSET="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["assetId"])' "$CAND2/staged.json" 2>/dev/null || true)"
    rm -f "$CAND2/staged.json"
    : > "$WORK/fake-requests.log"
}

promote_args() {
    echo $(vc_args "$CAND2") \
        --qualified-release-id "${QUAL_REL:-1}" \
        --qualified-asset-id "${QUAL_ASSET:-1}" \
        --approval-reference "issue-187-approval" \
        --governance-reference "issue-29-disposition"
}

case_promote_happy() {
    stage_once
    release_env x promote $(promote_args)
    python3 - "$CAND2" "$STATE" "$REL_TAG" "$BASE_REV" "$MAP_REV" <<'PY'
import json, sys
cand, state_path, rel_tag, payload_rev, rev = sys.argv[1:6]
e = json.load(open(cand + "/promotion.json"))
s = json.load(open(state_path))
assert s["refs"]["tags/" + rel_tag] == payload_rev  # payload tag on payload rev
assert s["refs"]["tags/v0.1.0"] == rev              # version tag on source rev
rel = s["releases"][0]
assert rel["draft"] is False and rel["prerelease"] is False
assert e["releaseId"] == rel["id"]
PY
    # ordering: payload ref POST before publish PATCH before version POST
    python3 - "$WORK/fake-requests.log" <<'PY'
import sys
lines = [l.strip() for l in open(sys.argv[1])]
posts = [l for l in lines if l.startswith(("POST", "PATCH"))]
payload = next(i for i, l in enumerate(posts) if "/git/refs" in l)
publish = next(i for i, l in enumerate(posts) if "PATCH" in l)
version = next(i for i, l in enumerate(posts)
               if "/git/refs" in l and i > payload)
assert payload < publish < version
PY
}
case_promote_stale_digest() {
    stage_once
    : > "$WORK/fake-requests.log"
    expect_fail release_env x promote $(promote_args | \
        sed "s/--candidate-digest $CAND2_DIGEST/--candidate-digest $(printf '9%.0s' {1..64})/")
    [ "$(mutations)" = "0" ]
}
case_promote_changed_version() {
    stage_once
    : > "$WORK/fake-requests.log"
    expect_fail release_env x promote $(promote_args | \
        sed "s/--release-version v0.1.0/--release-version v9.9.9/")
    [ "$(mutations)" = "0" ]
}
case_promote_missing_approval() {
    stage_once
    : > "$WORK/fake-requests.log"
    expect_fail env \
        CONTRACTSCRIBE_RELEASE_TEST=1 \
        CONTRACTSCRIBE_RELEASE_TEST_API_ROOT="$API_ROOT" \
        GITHUB_ACTIONS=true GITHUB_REPOSITORY=SolusQuest/contract-scribe \
        GITHUB_WORKFLOW_SHA="$WF_SHA" \
        CONTRACTSCRIBE_RELEASE_READ_TOKEN="$READ_TOKEN" \
        CONTRACTSCRIBE_RELEASE_TOKEN="$RELEASE_TOKEN" \
        python3 "$RELEASE_SCRIPTS/promote-candidate.py" promote \
        $(vc_args "$CAND2") \
        --qualified-release-id "$QUAL_REL" --qualified-asset-id "$QUAL_ASSET" \
        --approval-reference "" --governance-reference "g"
    [ "$(mutations)" = "0" ]
}
case_promote_ci_missing() {
    stage_once
    state_edit 's["ci_runs"]={}'
    : > "$WORK/fake-requests.log"
    expect_fail release_env x promote $(promote_args)
    [ "$(mutations)" = "0" ]
}
case_promote_ci_failed() {
    stage_once
    state_edit 's["ci_runs"][list(s["ci_runs"])[0]][0]["jobs"][0]["conclusion"]="failure"'
    : > "$WORK/fake-requests.log"
    expect_fail release_env x promote $(promote_args)
    [ "$(mutations)" = "0" ]
}
case_promote_ci_wrong_sha() {
    stage_once
    state_edit 'import re; s["ci_runs"]={"0"*40: s["ci_runs"].pop(list(s["ci_runs"])[0])}'
    : > "$WORK/fake-requests.log"
    expect_fail release_env x promote $(promote_args)
    [ "$(mutations)" = "0" ]
}
case_promote_wrong_release_id() {
    stage_once
    QUAL_REL="999999"
    : > "$WORK/fake-requests.log"
    expect_fail release_env x promote $(promote_args)
    [ "$(mutations)" = "0" ]
}
case_promote_recreated_asset() {
    stage_once
    state_edit 'r=s["releases"][0]; r["assets"]=[dict(r["assets"][0], id=424242)]'
    : > "$WORK/fake-requests.log"
    expect_fail release_env x promote $(promote_args)
    [ "$(mutations)" = "0" ]
}
case_promote_conflicting_version_tag() {
    stage_once
    state_edit 's["refs"]["tags/v0.1.0"]="0"*40'
    : > "$WORK/fake-requests.log"
    expect_fail release_env x promote $(promote_args)
    # preflight checks both refs before any mutation -> zero mutations
    [ "$(mutations)" = "0" ]
}
case_promote_partial_tag_only() {
    stage_once
    state_edit 's["refs"]["tags/'"$REL_TAG"'"]="'"$BASE_REV"'"'
    release_env x promote $(promote_args)
    python3 - "$STATE" "$REL_TAG" <<'PY'
import json, sys
s = json.load(open(sys.argv[1]))
assert s["releases"][0]["draft"] is False
assert "tags/v0.1.0" in s["refs"]
PY
}
case_promote_partial_published_no_version_tag() {
    stage_once
    state_edit 's["refs"]["tags/'"$REL_TAG"'"]="'"$BASE_REV"'"; s["releases"][0]["draft"]=False'
    release_env x promote $(promote_args)
    python3 - "$STATE" <<'PY'
import json, sys
s = json.load(open(sys.argv[1]))
assert "tags/v0.1.0" in s["refs"]
PY
}
case_promote_publish_drop_after_write() {
    stage_once
    state_edit 's["overrides"]["drop_after_write"]=["release-publish"]'
    release_env x promote $(promote_args)
    [ "$(count_log 'PATCH')" = "1" ]
}
case_promote_extra_asset() {
    stage_once
    state_edit 's["releases"][0]["assets"].append({"id":31337,"name":"extra.bin","file":"/nope","size":1,"digest":"sha256:"+"0"*64})'
    : > "$WORK/fake-requests.log"
    expect_fail release_env x promote $(promote_args)
    [ "$(mutations)" = "0" ]
}
case_promote_metadata_mismatch() {
    stage_once
    state_edit 's["releases"][0]["name"]="tampered"'
    : > "$WORK/fake-requests.log"
    expect_fail release_env x promote $(promote_args)
    [ "$(mutations)" = "0" ]
}
case_promote_map_not_authorizing() {
    stage_once
    # A later main commit reverts the map; a candidate whose source
    # revision no longer contains the authorized pair must be rejected.
    git -C "$FIXTURE" checkout -q -b nomap "$MAP_REV" 2>/dev/null || true
    python3 - "$FIXTURE" <<'PY'
import json, sys
p = sys.argv[1] + "/scripts/action/payload-map.json"
json.dump({"mapVersion": 1, "wrapper": "contract-scribe-action",
           "payload": None}, open(p, "w"))
PY
    git -C "$FIXTURE" add scripts/action/payload-map.json
    git -C "$FIXTURE" commit -qm revert
    git -C "$FIXTURE" checkout -q main
    git -C "$FIXTURE" merge -q nomap
    git -C "$FIXTURE" push -q origin main
    NOMAP_REV="$(git -C "$FIXTURE" rev-parse main)"
    local d="$WORK/cand-nomap"
    run_prepare "$d" "$NOMAP_REV" "$BASE_REV" "v0.1.0"
    point_run_state "$NOMAP_REV"
    : > "$WORK/fake-requests.log"
    expect_fail release_env x promote \
        --candidate "$d" --repo "$FIXTURE" \
        --source-revision "$NOMAP_REV" --payload-source-revision "$BASE_REV" \
        --release-version v0.1.0 --wrapper contract-scribe-action \
        --candidate-digest "$(cut -d' ' -f1 "$d/candidate.sha256")" \
        --candidate-run-id "$RUN_ID" \
        --qualified-release-id "$QUAL_REL" --qualified-asset-id "$QUAL_ASSET" \
        --approval-reference a --governance-reference g
    [ "$(mutations)" = "0" ]
    # Restore: main keeps the revert for history realism, but the fake
    # state points back at MAP_REV for the remaining legs.
    point_run_state "$MAP_REV"
}
run_case promote-happy case_promote_happy
run_case promote-stale-digest case_promote_stale_digest
run_case promote-changed-version case_promote_changed_version
run_case promote-missing-approval case_promote_missing_approval
run_case promote-ci-missing case_promote_ci_missing
run_case promote-ci-failed case_promote_ci_failed
run_case promote-ci-wrong-sha case_promote_ci_wrong_sha
run_case promote-wrong-release-id case_promote_wrong_release_id
run_case promote-recreated-asset case_promote_recreated_asset
run_case promote-conflicting-version-tag case_promote_conflicting_version_tag
run_case promote-partial-tag-only case_promote_partial_tag_only
run_case promote-partial-published case_promote_partial_published_no_version_tag
run_case promote-publish-drop case_promote_publish_drop_after_write
run_case promote-extra-asset case_promote_extra_asset
run_case promote-metadata-mismatch case_promote_metadata_mismatch
run_case promote-map-not-authorizing case_promote_map_not_authorizing

# ---------------------------------------------------------------------------
# verify-draft leg + global request-log hygiene
# ---------------------------------------------------------------------------

case_verify_draft_happy() {
    stage_once
    release_env x verify-draft $(vc_args "$CAND2") \
        --qualified-release-id "$QUAL_REL" --qualified-asset-id "$QUAL_ASSET"
}
case_verify_draft_mismatch() {
    stage_once
    state_edit 's["releases"][0]["body"]="changed"'
    expect_fail release_env x verify-draft $(vc_args "$CAND2")
}
run_case verify-draft-happy case_verify_draft_happy
run_case verify-draft-mismatch case_verify_draft_mismatch

case_log_no_delete() {
    cat "$WORK/fake-requests.log" >> "$WORK/fake-requests-all.log"
    ! grep -q "^DELETE" "$WORK/fake-requests-all.log"
}
case_log_no_unknown_auth() {
    # The release credential never travels to /actions readbacks; the read
    # token never performs release mutations; no DELETE anywhere.
    ! grep -qE "^(POST|PATCH|PUT|DELETE).*auth=read" "$WORK/fake-requests-all.log"
    ! grep -q "^DELETE" "$WORK/fake-requests-all.log"
}
run_case log-no-delete case_log_no_delete
run_case log-token-channels case_log_no_unknown_auth

# ---------------------------------------------------------------------------
# --real: real A1 candidate-build smoke on a local clone of the repo.
# ---------------------------------------------------------------------------

if [ "$REAL" -eq 1 ]; then
    case_real_prepare() {
        local remote="$WORK/real-remote.git" clone="$WORK/real-clone"
        git init --bare -q "$remote"
        git clone -q "$REPO_ROOT" "$clone" 2>/dev/null || \
            git clone -q "file://$REPO_ROOT" "$clone"
        # Seed origin/main at the current checkout's HEAD: the fixture
        # remote is local, so the candidate revision is main-reachable.
        local rev; rev="$(git -C "$REPO_ROOT" rev-parse HEAD)"
        git -C "$clone" remote set-url origin "$remote"
        git -C "$clone" push -q origin HEAD:refs/heads/main
        git -C "$clone" fetch -q origin main
        env \
            CONTRACTSCRIBE_RELEASE_TEST=1 \
            GITHUB_ACTIONS=true \
            GITHUB_REPOSITORY=SolusQuest/contract-scribe \
            RUNNER_OS=Linux RUNNER_ARCH=X64 \
            python3 "$RELEASE_SCRIPTS/prepare-candidate.py" \
            --repo "$clone" --output "$WORK/real-out" \
            --source-revision "$rev" --payload-source-revision "$rev" \
            --release-version v0.1.0 --wrapper contract-scribe-action
        python3 - "$WORK/real-out" <<'PY'
import json, sys
c = json.load(open(sys.argv[1] + "/candidate.json"))
i = c["identity"]
assert i["archiveSha256"] and i["manifestSha256"]
assert i["dependencyInventory"], "deps.json inventory must be non-empty"
assert i["toolchain"]["dotnetSdk"]
m = json.load(open(sys.argv[1] + "/payload-map.json"))
assert m["payload"]["sha256"] == i["archiveSha256"]
PY
    }
    run_case real-prepare case_real_prepare
fi

kill $FAKE_PID 2>/dev/null || true
echo "verify-release: $CASES_RUN cases, $CASES_FAILED failed"
[ "$CASES_FAILED" -eq 0 ]
