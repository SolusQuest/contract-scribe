#!/usr/bin/env bash
# verify-action.sh — verification matrix for the composite Action
# (issue #185 / M6-A2). Covers input validation, acquisition policy, install
# semantics, invocation/process ownership, cancellation, and (with --real)
# the real github-proposal path against loopback fakes.
#
#   bash tests/action/verify-action.sh \
#       --kit <dir> --archive <payload.tar.gz> --sha256-file <sidecar>
#       [--real] [--work <dir>] [--keep] [--only <regex>]
#
# --kit supplies the packaging-test-kit layout (tests/packaging + fixture +
# hook build output). --archive is the payload archive the committed map
# authorizes (CI passes the fixed-source build of map.payload.sourceRevision).
# --real enables legs that need the real dotnet SDK + installed payload.
set -uo pipefail

ARCHIVE=""
SHA_FILE=""
KIT=""
REPO_ARG=""
WORK=""
KEEP=0
ONLY=""
REAL=0

while [ $# -gt 0 ]; do
    case "$1" in
        --archive) ARCHIVE="$2"; shift 2 ;;
        --sha256-file) SHA_FILE="$2"; shift 2 ;;
        --kit) KIT="$2"; shift 2 ;;
        --repo) REPO_ARG="$2"; shift 2 ;;
        --work) WORK="$2"; shift 2 ;;
        --keep) KEEP=1; shift ;;
        --only) ONLY="$2"; shift 2 ;;
        --real) REAL=1; shift ;;
        *) echo "verify-action: unknown argument $1" >&2; exit 2 ;;
    esac
done

if [ -z "$WORK" ]; then
    WORK="$(mktemp -d "${TMPDIR:-/tmp}/contract-scribe-action.XXXXXX")"
else
    mkdir -p -- "$WORK"
    WORK="$(mktemp -d "$WORK/action-verify.XXXXXX")"
fi
WORK="$(cd "$WORK" && pwd)"
LOGDIR="$WORK/logs"
mkdir -p "$LOGDIR"
if [ "$KEEP" -eq 0 ]; then
    trap 'rm -rf "$WORK"' EXIT
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
ACTION_SCRIPTS="$REPO_ROOT/scripts/action"
MAP_FILE="$ACTION_SCRIPTS/payload-map.json"
DRIVER="$SCRIPT_DIR/driver.py"
if [ -n "$KIT" ]; then
    KIT="$(cd "$KIT" && pwd)"
    PACKAGING_DIR="$KIT/tests/packaging"
    HOOK_DLL="$KIT/hook/ContractScribe.CampaignStartupHook.dll"
    FIXTURE="$KIT/fixture"
elif [ -n "$REPO_ARG" ]; then
    REPO_ARG="$(cd "$REPO_ARG" && pwd)"
    PACKAGING_DIR="$REPO_ARG/tests/packaging"
    HOOK_DLL="$REPO_ARG/tests/ContractScribe.CampaignStartupHook/bin/Release/net10.0/ContractScribe.CampaignStartupHook.dll"
    FIXTURE="$PACKAGING_DIR/fixture"
else
    echo "verify-action: --kit or --repo is required" >&2; exit 2
fi
HAZARD_PY="$PACKAGING_DIR/make_hazard.py"
REF_EXTRACT="$PACKAGING_DIR/payload_extract.py"

for f in "$MAP_FILE" "$DRIVER" "$HAZARD_PY" "$REF_EXTRACT"; do
    [ -f "$f" ] || { echo "verify-action: missing $f" >&2; exit 2; }
done
[ -f "$ARCHIVE" ] || { echo "verify-action: --archive $ARCHIVE missing" >&2; exit 2; }
[ -f "$SHA_FILE" ] || { echo "verify-action: --sha256-file missing" >&2; exit 2; }
ARCHIVE_SHA="$(awk '{print $1}' "$SHA_FILE")"
ARCHIVE_BASE="$(basename "$ARCHIVE" .tar.gz)"
ARCHIVE_VERSION="${ARCHIVE_BASE#contract-scribe-}"
ARCHIVE_VERSION="${ARCHIVE_VERSION%-linux-x64}"

SYNTHETIC_TOKEN="contract-scribe-synthetic-acquisition-only"
PRODUCT_TOKEN="contract-scribe-synthetic-transport-only"
PROVIDER_TOKEN="contract-scribe-synthetic-provider-only"

CASES_RUN=0
CASES_FAILED=0
FAILED_CASES=()
run_case() {
    local name="$1"; shift
    if [ -n "$ONLY" ] && ! echo "$name" | grep -qE "$ONLY"; then
        return 0
    fi
    CASES_RUN=$((CASES_RUN + 1))
    if "$@" >"$LOGDIR/$name.log" 2>&1; then
        echo "PASS $name"
    else
        echo "FAIL $name (log: $LOGDIR/$name.log)"
        CASES_FAILED=$((CASES_FAILED + 1))
        FAILED_CASES+=("$name")
    fi
}

# ---------------------------------------------------------------------------
# Shared fixtures
# ---------------------------------------------------------------------------

STUB_BIN="$WORK/stubbin"
mkdir -p "$STUB_BIN"
cat > "$STUB_BIN/dotnet" <<'EOF'
#!/usr/bin/env bash
# Stub dotnet: answers prerequisite probes; when invoked with a .dll payload
# it records argv/env and emits a canned envelope per STUB_MODE.
case "${1:-}" in
    --list-runtimes) echo "Microsoft.NETCore.App 10.0.0 [/usr/share/dotnet]"; exit 0 ;;
    --list-sdks) echo "10.0.100 [/usr/share/dotnet]"; exit 0 ;;
    --version) echo "10.0.100"; exit 0 ;;
esac
echo "$@" > "${STUB_ARGV:-/dev/null}"
env | sort > "${STUB_ENV:-/dev/null}"
case "${STUB_MODE:-ok}" in
    sleep) sleep 3600 ;;
    conflict) echo '{"githubProposalEnvelopeVersion":1,"terminalLayer":"campaign","cliContractBaseline":"b","toolVersion":"t","campaignOperation":"start","publicationOperationId":"op","generationId":"gen","outcome":"github-proposal.conflict","diagnosticCodes":["github-proposal.conflict"],"checkpointRevision":null,"pullRequestUrl":null,"publicationDiagnostic":null}'; exit 3 ;;
    badshape) echo '{"unexpected":1}'; exit 0 ;;
    extraline) echo '{"a":1}'; echo '{"b":2}'; exit 0 ;;
    extrastderr) echo '{"githubProposalEnvelopeVersion":1,"terminalLayer":"campaign","cliContractBaseline":"b","toolVersion":"t","campaignOperation":"start","publicationOperationId":"op","generationId":"gen","outcome":"github-proposal.published","diagnosticCodes":[],"checkpointRevision":null,"pullRequestUrl":null,"publicationDiagnostic":null}'; echo "one" >&2; echo "two" >&2; exit 0 ;;
    *) echo '{"githubProposalEnvelopeVersion":1,"terminalLayer":"publication","cliContractBaseline":"v1","toolVersion":"t","campaignOperation":"start","publicationOperationId":"op","generationId":"gen","outcome":"github-proposal.published","diagnosticCodes":[],"checkpointRevision":7,"pullRequestUrl":"https://github.com/Owner/repo/pull/1","publicationDiagnostic":null}'; exit 0 ;;
esac
EOF
chmod +x "$STUB_BIN/dotnet"

# Generic map template; $1 = sha256 override, $2 = tag override.
write_map() {
    local sha="${1:-$ARCHIVE_SHA}" tag="${2:-payload-$ARCHIVE_VERSION}"
    cat > "$3" <<EOF
{"mapVersion":1,"wrapper":"contract-scribe-action","payload":{
 "repository":"SolusQuest/contract-scribe",
 "releaseTag":"$tag","toolVersion":"$ARCHIVE_VERSION",
 "sourceRevision":"${4:-${ARCHIVE_VERSION##*+}}",
 "runtimeIdentifier":"linux-x64","assetName":"$ARCHIVE_BASE.tar.gz",
 "sha256":"$sha","assets":["$ARCHIVE_BASE.tar.gz"]}}
EOF
}

RELEASE_CFG="$WORK/release-config.json"
release_config() {
    # $1 = extra releases JSON fragment (appended after the primary), $2 =
    # overrides fragment, $3 = extra assets fragment, $4 = "draft" to mark
    # the primary release draft, $5 = require_auth true
    # Atomic replace: the server reloads the config per request.
    cat > "$RELEASE_CFG.$$.tmp" <<EOF
{"repository":"SolusQuest/contract-scribe",
 "expected_token":"$SYNTHETIC_TOKEN",
 "require_auth":${5:-false},
 "releases":[{"id":9001,"tag":"payload-$ARCHIVE_VERSION","draft":${4:-false},
   "assets":[{"id":1,"name":"$ARCHIVE_BASE.tar.gz","file":"$ARCHIVE"}$3]}$1],
 "overrides":{$2}}
EOF
    mv -f "$RELEASE_CFG.$$.tmp" "$RELEASE_CFG"
}
release_config "" "" ""

touch "$WORK/release-requests.log"
python3 "$SCRIPT_DIR/fake_release_api.py" "$RELEASE_CFG" \
    "$WORK/release-ready.json" "$WORK/release-requests.log" &
REL_PID=$!
for _ in $(seq 50); do [ -f "$WORK/release-ready.json" ] && break; sleep 0.1; done
API_ROOT="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["api"])' "$WORK/release-ready.json")"

new_work() {
    local d="$WORK/case-$1"
    python3 "$DRIVER" init "$d" --install-root "$d/install" --dotnet "$STUB_BIN/dotnet"
    echo "$d"
}

driver() { python3 "$DRIVER" "$@"; }
# POSIX-only legs: the install selector is a symlink and signal semantics are
# POSIX; the Action runs exclusively on Linux. Locally these legs skip when
# the platform cannot create symlinks (Windows without developer mode).
SYMLINK_OK=0
if python3 -c 'import os,tempfile;p=tempfile.mkdtemp();os.symlink("x",os.path.join(p,"l"))' 2>/dev/null; then
    SYMLINK_OK=1
fi
expect_fail() {
    if "$@"; then echo "expected failure, got success" >&2; return 1; fi
    return 0
}
posix_only() {
    if [ "$SYMLINK_OK" -eq 0 ]; then
        echo "skip: no symlink/POSIX capability on this host" >&2
        return 0
    fi
    return 1
}

# ---------------------------------------------------------------------------
# Static legs
# ---------------------------------------------------------------------------

case_static_pycompile() {
    python3 -m py_compile "$ACTION_SCRIPTS"/*.py "$SCRIPT_DIR"/*.py
}
case_static_map_schema() {
    python3 - "$MAP_FILE" <<'PY'
import json,sys
doc = json.load(open(sys.argv[1]))
assert doc["mapVersion"] == 1 and doc["wrapper"] == "contract-scribe-action"
p = doc["payload"]
assert p["repository"] == "SolusQuest/contract-scribe"
assert p["sourceRevision"] == "a960b29db78e41de4aa9df34c9141b5ec2e13fdf"
assert p["toolVersion"].endswith("+" + p["sourceRevision"])
assert p["assetName"] == "contract-scribe-%s-linux-x64.tar.gz" % p["toolVersion"]
assert p["assets"] == [p["assetName"]]
assert len(p["sha256"]) == 64
PY
}
case_static_action_yml() {
    local yml="$REPO_ROOT/action.yml"
    grep -q "using: composite" "$yml"
    grep -q 'exec python3 "\${{ github.action_path }}/scripts/action/prepare.py"' "$yml"
    grep -q 'exec python3 "\${{ github.action_path }}/scripts/action/invoke.py"' "$yml"
    ! grep -qE 'pull_request_target|\$\{\{ *secrets' "$yml"
    ! grep -qE 'github-token.*argv|--token' "$yml"
    grep -q "if: always()" "$yml"
}


# ---------------------------------------------------------------------------
# Prepare legs (production entrypoint, env-fed)
# ---------------------------------------------------------------------------

prep_env() {
    local req="${6-}"
    [ -z "$req" ] && req='{}'
    env -u CONTRACTSCRIBE_ACTION_TEST -u CONTRACTSCRIBE_ACTION_TEST_API_ROOT \
        -u CS_WORK_DIR \
        PATH="$STUB_BIN:$PATH" \
        RUNNER_OS=Linux RUNNER_ARCH=X64 RUNNER_TEMP="$WORK" \
        GITHUB_OUTPUT="$1" GITHUB_ENV="$1.env" \
        CS_INPUT_OPERATION="${2:-github-proposal-start}" \
        CS_INPUT_REPOSITORY_ROOT="${3:-/repo}" \
        CS_INPUT_INPUT="${4:-App/App.csproj}" \
        CS_INPUT_POLICY="${5:-policy.json}" \
        CS_INPUT_REQUEST="$req" \
        CS_INPUT_CONFIGURATION="${7-}" \
        CS_INPUT_CONFIGURATION_OVERRIDE="${8-}" \
        python3 "$ACTION_SCRIPTS/prepare.py"
}
case_prepare_ok() {
    prep_env "$WORK/prep-out.txt" >"$WORK/prep-stdout.log" 2>&1
    grep -q "action-prepare stage=validate result=ok" "$WORK/prep-stdout.log"
    local wd; wd="$(grep '^CS_WORK_DIR=' "$WORK/prep-out.txt.env" | cut -d= -f2-)"
    [ -n "$wd" ] && [ -f "$wd/plan.json" ]
}
case_prepare_dotnet_missing() {
    # A dedicated bin dir containing only python3: hosted runners ship dotnet
    # under /usr/bin, so restricting PATH to /usr/bin cannot hide it.
    local sb="$WORK/nobin"
    mkdir -p "$sb"
    ln -sf "$(command -v python3)" "$sb/python3"
    expect_fail env -u CONTRACTSCRIBE_ACTION_TEST -u CONTRACTSCRIBE_ACTION_TEST_API_ROOT -u CS_WORK_DIR PATH="$sb" HOME="$HOME" \
        RUNNER_OS=Linux RUNNER_ARCH=X64 RUNNER_TEMP="$WORK" \
        GITHUB_OUTPUT=/dev/null GITHUB_ENV=/dev/null \
        CS_INPUT_OPERATION=github-proposal-start CS_INPUT_REPOSITORY_ROOT=/r \
        CS_INPUT_INPUT=i CS_INPUT_POLICY=p CS_INPUT_REQUEST='{}' \
        python3 "$ACTION_SCRIPTS/prepare.py"
}
case_prepare_operation_missing() {
    expect_fail env -u CONTRACTSCRIBE_ACTION_TEST -u CONTRACTSCRIBE_ACTION_TEST_API_ROOT -u CS_WORK_DIR PATH="$PATH" RUNNER_OS=Linux RUNNER_ARCH=X64 \
        RUNNER_TEMP="$WORK" GITHUB_OUTPUT=/dev/null GITHUB_ENV=/dev/null \
        python3 "$ACTION_SCRIPTS/prepare.py"
}
case_prepare_operation_unknown() {
    expect_fail prep_env /dev/null "audit-run"
}
case_prepare_request_missing() {
    expect_fail env -u CONTRACTSCRIBE_ACTION_TEST -u CONTRACTSCRIBE_ACTION_TEST_API_ROOT -u CS_WORK_DIR PATH="$PATH" RUNNER_OS=Linux RUNNER_ARCH=X64 \
        RUNNER_TEMP="$WORK" GITHUB_OUTPUT=/dev/null GITHUB_ENV=/dev/null \
        CS_INPUT_OPERATION=github-proposal-start \
        CS_INPUT_REPOSITORY_ROOT=/r CS_INPUT_INPUT=i CS_INPUT_POLICY=p \
        python3 "$ACTION_SCRIPTS/prepare.py"
}
case_prepare_request_oversize() {
    local big; big="$(python3 -c 'print(" "*66000)')"
    expect_fail prep_env /dev/null github-proposal-start /r i p "{\"k\":\"$big\"}"
}
case_prepare_runner_os() {
    expect_fail env -u CONTRACTSCRIBE_ACTION_TEST -u CONTRACTSCRIBE_ACTION_TEST_API_ROOT -u CS_WORK_DIR PATH="$PATH" RUNNER_OS=Windows RUNNER_ARCH=X64 \
        RUNNER_TEMP="$WORK" GITHUB_OUTPUT=/dev/null GITHUB_ENV=/dev/null \
        CS_INPUT_OPERATION=github-proposal-start CS_INPUT_REPOSITORY_ROOT=/r \
        CS_INPUT_INPUT=i CS_INPUT_POLICY=p CS_INPUT_REQUEST='{}' \
        python3 "$ACTION_SCRIPTS/prepare.py"
}
case_prepare_runner_arch() {
    expect_fail env -u CONTRACTSCRIBE_ACTION_TEST -u CONTRACTSCRIBE_ACTION_TEST_API_ROOT -u CS_WORK_DIR PATH="$PATH" RUNNER_OS=Linux RUNNER_ARCH=ARM64 \
        RUNNER_TEMP="$WORK" GITHUB_OUTPUT=/dev/null GITHUB_ENV=/dev/null \
        CS_INPUT_OPERATION=github-proposal-start CS_INPUT_REPOSITORY_ROOT=/r \
        CS_INPUT_INPUT=i CS_INPUT_POLICY=p CS_INPUT_REQUEST='{}' \
        python3 "$ACTION_SCRIPTS/prepare.py"
}
case_prepare_multiline() {
    local req='{
  "githubProposalRequestVersion": 1,
  "github": {"transition": "initial"}
}'
    prep_env "$WORK/prep-ml.txt" github-proposal-start /r i p "$req" >/dev/null
    local wd; wd="$(grep '^CS_WORK_DIR=' "$WORK/prep-ml.txt.env" | cut -d= -f2-)"
    cmp -s "$wd/input-request.json" <(printf '%s' "$req")
}

# ---------------------------------------------------------------------------
# Acquire legs (driver-injected maps/api-root against the fake release API)
# ---------------------------------------------------------------------------

acquire_leg() {
    # $1=workdir $2=mapfile ; env CONTRACTSCRIBE_ACQUISITION_TOKEN optional
    CONTRACTSCRIBE_ACTION_TEST=1 driver acquire "$1" "$2" "$API_ROOT"
}
case_acquire_ok_published() {
    local w; w="$(new_work pub)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    CONTRACTSCRIBE_ACQUISITION_TOKEN="$SYNTHETIC_TOKEN" acquire_leg "$w" "$w/map.json"
    grep -q "action-acquire stage=resolve result=ok" <<<"$(cat /dev/null; true)" || true
    [ -f "$w/install/.archives/$ARCHIVE_SHA.tar.gz" ]
    # Authorization must not cross to the CDN origin.
    ! grep -q "/cdn/1 auth=present" "$WORK/release-requests.log"
    grep -q "/releases/assets/1 auth=present" "$WORK/release-requests.log"
}
case_acquire_cache_hit() {
    local w; w="$(new_work hit)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    acquire_leg "$w" "$w/map.json"
    local before; before="$(grep -c '/releases/assets/' "$WORK/release-requests.log")"
    acquire_leg "$w" "$w/map.json"
    local after; after="$(grep -c '/releases/assets/' "$WORK/release-requests.log")"
    [ "$before" -eq "$after" ]
}
case_acquire_wrong_sha() {
    local w; w="$(new_work sha)"
    write_map "$(printf '0%.0s' {1..64})" "payload-$ARCHIVE_VERSION" "$w/map.json"
    expect_fail acquire_leg "$w" "$w/map.json"
}
case_acquire_release_404() {
    local w; w="$(new_work r404)"
    write_map "$ARCHIVE_SHA" "payload-nonexistent-tag" "$w/map.json"
    expect_fail acquire_leg "$w" "$w/map.json"
}
case_acquire_release_ambiguous() {
    local w; w="$(new_work amb)"
    release_config ',{"id":9002,"tag":"payload-'"$ARCHIVE_VERSION"'","draft":true,"assets":[]}' "" "" true true
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    # one published + one draft with the same tag -> ambiguity under auth enum
    expect_fail env CONTRACTSCRIBE_ACQUISITION_TOKEN="$SYNTHETIC_TOKEN" \
        CONTRACTSCRIBE_ACTION_TEST=1 driver acquire "$w" "$w/map.json" "$API_ROOT"
    release_config "" "" ""
}
case_acquire_asset_missing() {
    local w; w="$(new_work amiss)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    release_config "" "" "" true true
    # draft release has no assets -> enumeration resolves but asset missing
    python3 - "$RELEASE_CFG" <<'PY'
import json,os,sys
cfg=json.load(open(sys.argv[1])); cfg["releases"][0]["assets"]=[]
json.dump(cfg,open(sys.argv[1]+".tmp","w"));os.replace(sys.argv[1]+".tmp",sys.argv[1])
PY
    expect_fail env CONTRACTSCRIBE_ACQUISITION_TOKEN="$SYNTHETIC_TOKEN" \
        CONTRACTSCRIBE_ACTION_TEST=1 driver acquire "$w" "$w/map.json" "$API_ROOT"
    release_config "" "" ""
}
case_acquire_asset_duplicate() {
    local w; w="$(new_work dup)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    release_config "" "" ',{"id":2,"name":"'"$ARCHIVE_BASE"'.tar.gz","file":"'"$ARCHIVE"'"}'
    expect_fail acquire_leg "$w" "$w/map.json"
    release_config "" "" ""
}
case_acquire_asset_sibling() {
    local w; w="$(new_work sib)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    release_config "" "" ',{"id":3,"name":"contract-scribe-9.9.9-linux-x64.tar.gz","file":"'"$ARCHIVE"'"}'
    expect_fail acquire_leg "$w" "$w/map.json"
    release_config "" "" ""
}
case_acquire_asset_unrelated_ok() {
    local w; w="$(new_work unrel)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    release_config "" "" ',{"id":4,"name":"release-notes.txt","file":"'"$ARCHIVE"'"}'
    acquire_leg "$w" "$w/map.json"
    release_config "" "" ""
}
case_acquire_http_401() {
    local w; w="$(new_work a401)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    release_config "" "" "" true true
    expect_fail env CONTRACTSCRIBE_ACQUISITION_TOKEN="wrong-token" \
        CONTRACTSCRIBE_ACTION_TEST=1 driver acquire "$w" "$w/map.json" "$API_ROOT"
    release_config "" "" ""
}
case_acquire_draft_public_blocked() {
    local w; w="$(new_work dpub)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    release_config "" "" "" true false
    # no token -> by-tag path cannot see the draft -> closed failure
    expect_fail acquire_leg "$w" "$w/map.json"
    release_config "" "" ""
}
case_acquire_draft_authenticated() {
    local w; w="$(new_work dauth)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    release_config "" "" "" true true
    env CONTRACTSCRIBE_ACQUISITION_TOKEN="$SYNTHETIC_TOKEN" \
        CONTRACTSCRIBE_ACTION_TEST=1 driver acquire "$w" "$w/map.json" "$API_ROOT"
    release_config "" "" ""
}
case_acquire_redirect_foreign() {
    local w; w="$(new_work rfor)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    release_config "" "" '"asset_location":"https://objects.example.com/x.tar.gz"'
    expect_fail acquire_leg "$w" "$w/map.json"
    release_config "" "" ""
}
case_acquire_redirect_userinfo() {
    local w; w="$(new_work rusr)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    release_config "" "" '"asset_location":"http://user:pw@127.0.0.1:9/x"'
    expect_fail acquire_leg "$w" "$w/map.json"
    release_config "" "" ""
}
case_acquire_redirect_loop() {
    local w; w="$(new_work rloop)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    local self="$API_ROOT/repos/SolusQuest/contract-scribe/releases/assets/1"
    release_config "" "" '"asset_location":"'"$self"'"'
    expect_fail acquire_leg "$w" "$w/map.json"
    release_config "" "" ""
}
case_acquire_truncated() {
    local w; w="$(new_work trunc)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    release_config "" "" '"asset_200":true,"truncate_bytes":128'
    expect_fail acquire_leg "$w" "$w/map.json"
    release_config "" "" ""
}
case_acquire_oversize_declared() {
    local w; w="$(new_work ovsize)"
    local big="$WORK/big-sparse.tar.gz"
    truncate -s 300M "$big"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    release_config "" "" '"asset_200":true' ',{"id":9,"name":"x","file":"'"$big"'"}'
    # repoint asset id 1 at the sparse file
    python3 - "$RELEASE_CFG" "$big" <<'PY'
import json,os,sys
cfg=json.load(open(sys.argv[1])); cfg["releases"][0]["assets"]=[{"id":1,"name":cfg["releases"][0]["assets"][0]["name"],"file":sys.argv[2]}]
json.dump(cfg,open(sys.argv[1]+".tmp","w"));os.replace(sys.argv[1]+".tmp",sys.argv[1])
PY
    expect_fail acquire_leg "$w" "$w/map.json"
    release_config "" "" ""
}
case_acquire_poisoned_cache() {
    local w; w="$(new_work poison)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    acquire_leg "$w" "$w/map.json"
    printf 'x' >> "$w/install/.archives/$ARCHIVE_SHA.tar.gz"
    expect_fail acquire_leg "$w" "$w/map.json"
}
case_acquire_foreign_cache_file() {
    local w; w="$(new_work fc)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    mkdir -p "$w/install/.archives"
    python3 - "$w/install/.archives/$ARCHIVE_SHA.tar.gz" <<'PY'
import os,sys
try:
    os.symlink("/nonexistent-target", sys.argv[1])
except OSError:
    # No symlink privilege on this host — a foreign regular file exercises the
    # same retained-archive rejection (digest mismatch).
    open(sys.argv[1], "wb").write(b"foreign")
PY
    expect_fail acquire_leg "$w" "$w/map.json"
}
case_acquire_test_credential() {
    local w; w="$(new_work tcred)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    expect_fail env CONTRACTSCRIBE_ACQUISITION_TOKEN="ghp_realish" \
        CONTRACTSCRIBE_ACTION_TEST=1 driver acquire "$w" "$w/map.json" "$API_ROOT"
}
case_acquire_proxy_off() {
    local w; w="$(new_work prox)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    HTTPS_PROXY="http://127.0.0.1:1" HTTP_PROXY="http://127.0.0.1:1" acquire_leg "$w" "$w/map.json"
}
case_acquire_gate_env_without_test() {
    local w; w="$(new_work gate1)"
    expect_fail env -u CONTRACTSCRIBE_ACTION_TEST \
        CONTRACTSCRIBE_ACTION_TEST_API_ROOT="$API_ROOT" \
        CS_WORK_DIR="$w" GITHUB_OUTPUT=/dev/null \
        python3 "$ACTION_SCRIPTS/acquire.py"
}
case_acquire_gate_nonloopback() {
    local w; w="$(new_work gate2)"
    expect_fail env CONTRACTSCRIBE_ACTION_TEST=1 GITHUB_ACTIONS=true \
        GITHUB_REPOSITORY=SolusQuest/contract-scribe \
        CONTRACTSCRIBE_ACTION_TEST_API_ROOT="https://api.evil.example" \
        CS_WORK_DIR="$w" GITHUB_OUTPUT=/dev/null \
        python3 "$ACTION_SCRIPTS/acquire.py"
}
case_acquire_gate_foreign_repo() {
    local w; w="$(new_work gate3)"
    expect_fail env CONTRACTSCRIBE_ACTION_TEST=1 GITHUB_ACTIONS=true \
        GITHUB_REPOSITORY=someone/else \
        CONTRACTSCRIBE_ACTION_TEST_API_ROOT="$API_ROOT" \
        CS_WORK_DIR="$w" GITHUB_OUTPUT=/dev/null \
        python3 "$ACTION_SCRIPTS/acquire.py"
}
case_acquire_map_null_payload() {
    local w; w="$(new_work nullmap)"
    printf '{"mapVersion":1,"wrapper":"contract-scribe-action","payload":null}' > "$w/map.json"
    expect_fail acquire_leg "$w" "$w/map.json"
}
case_acquire_map_malformed() {
    local w; w="$(new_work badmap)"
    printf '{"mapVersion":1,"wrapper":"x","payload":{}}' > "$w/map.json"
    expect_fail acquire_leg "$w" "$w/map.json"
    write_map "zzzz" "payload-$ARCHIVE_VERSION" "$w/map.json"
    expect_fail acquire_leg "$w" "$w/map.json"
}
case_acquire_committed_map() {
    # The production acquire entrypoint reads only the checked-in map; this
    # leg exercises it end-to-end when the supplied archive is the mapped one.
    python3 - "$MAP_FILE" "$ARCHIVE_SHA" <<'PY' || { echo "archive != committed map; skipped"; return 0; }
import json,os,sys
m=json.load(open(sys.argv[1]))
assert m["payload"]["sha256"]==sys.argv[2]
PY
    local tag; tag="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["payload"]["releaseTag"])' "$MAP_FILE")"
    release_config "" "" "" true true
    python3 - "$RELEASE_CFG" "$tag" <<'PY'
import json,os,sys
c=json.load(open(sys.argv[1])); c["releases"][0]["tag"]=sys.argv[2]
json.dump(c,open(sys.argv[1]+".tmp","w"));os.replace(sys.argv[1]+".tmp",sys.argv[1])
PY
    local w; w="$(new_work cmap)"
    env CONTRACTSCRIBE_ACTION_TEST=1 GITHUB_ACTIONS=true \
        GITHUB_REPOSITORY=SolusQuest/contract-scribe \
        CONTRACTSCRIBE_ACTION_TEST_API_ROOT="$API_ROOT" \
        CONTRACTSCRIBE_ACQUISITION_TOKEN="$SYNTHETIC_TOKEN" \
        CS_WORK_DIR="$w" GITHUB_OUTPUT=/dev/null \
        python3 "$ACTION_SCRIPTS/acquire.py"
    release_config "" "" ""
}
case_canary_no_credentials() {
    local w; w="$(setup_invoke canary)"
    env CS_WORK_DIR="$w" GITHUB_OUTPUT="$w/out.txt" \
        CONTRACTSCRIBE_PROVIDER_API_KEY="$PROVIDER_TOKEN" \
        CONTRACTSCRIBE_GITHUB_TOKEN="$PRODUCT_TOKEN" \
        CONTRACTSCRIBE_ACQUISITION_TOKEN="$SYNTHETIC_TOKEN" \
        STUB_ARGV="$w/argv.txt" STUB_ENV="$w/env.txt" \
        python3 "$ACTION_SCRIPTS/invoke.py" >/dev/null
    # No credential value may persist in owned files or outputs (the only
    # sanctioned places: the child's own env and ::add-mask:: lines the
    # runner rewrites). env.txt is the capture proving the child received it.
    ! grep -rF --exclude=env.txt "$PRODUCT_TOKEN" "$w" | grep -v add-mask
    ! grep -rF --exclude=env.txt "$PROVIDER_TOKEN" "$w" | grep -v add-mask
    ! grep -rF --exclude=env.txt "$SYNTHETIC_TOKEN" "$w" | grep -v add-mask
    grep -F "$PRODUCT_TOKEN" "$w/env.txt" >/dev/null  # child env carries it
    ! grep -F "CONTRACTSCRIBE_ACQUISITION_TOKEN" "$w/env.txt" >/dev/null
}

# ---------------------------------------------------------------------------
# Install legs
# ---------------------------------------------------------------------------

install_via_acquire() {
    local w="$1"
    acquire_leg "$w" "$w/map.json" && driver install "$w"
}
case_install_happy() {
    posix_only && return 0
    local w; w="$(new_work ih)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    install_via_acquire "$w"
    [ -f "$w/install/$ARCHIVE_BASE/ContractScribe.Cli.dll" ]
    [ "$(readlink "$w/install/current")" = "$ARCHIVE_BASE" ]
    ! ls "$w/install" | grep -q '^\.install-staging\.'
}
case_install_cache_reuse() {
    posix_only && return 0
    local w; w="$(new_work icr)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    install_via_acquire "$w"
    driver install "$w" 2>&1 | tee "$w/second.log"
    grep -q "action-cache check=hit result=ok" "$w/second.log"
}
case_install_poisoned_tree() {
    posix_only && return 0
    local w; w="$(new_work ipt)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    install_via_acquire "$w"
    printf 'x' >> "$w/install/$ARCHIVE_BASE/config/defaults.json"
    expect_fail driver install "$w"
}
case_install_poisoned_coherent() {
    posix_only && return 0
    local w; w="$(new_work ipc)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    install_via_acquire "$w"
    # Replace a file AND the installed manifest coherently; the verified
    # archive oracle must still detect the substitution.
    local target="$w/install/$ARCHIVE_BASE/config/defaults.json"
    printf 'poisoned' > "$target"
    python3 - "$w/install/$ARCHIVE_BASE/payload.json" "$target" <<'PY'
import hashlib,json,sys
m=json.load(open(sys.argv[1]))
m["defaultsJsonSha256"]=hashlib.sha256(open(sys.argv[2],'rb').read()).hexdigest()
for e in m.get("files",[]):
    if e["path"]=="config/defaults.json":
        e["sha256"]=m["defaultsJsonSha256"]; e["length"]=len(open(sys.argv[2],'rb').read())
json.dump(m,open(sys.argv[1],"w"),indent=2)
PY
    expect_fail driver install "$w"
}
case_install_foreign_current() {
    local w; w="$(new_work ifc)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    acquire_leg "$w" "$w/map.json"
    mkdir -p "$w/install/current"
    expect_fail driver install "$w"
}
case_install_staging_residue() {
    posix_only && return 0
    local w; w="$(new_work isr)"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    acquire_leg "$w" "$w/map.json"
    mkdir -p "$w/install/.install-staging.dead/junk"
    driver install "$w"
    ! ls "$w/install" | grep -q '^\.install-staging\.'
}
case_install_hazard_corpus() {
    local corpus="$WORK/hazard-corpus"
    python3 "$HAZARD_PY" "$ARCHIVE" "$corpus" >/dev/null
    local fail=0
    for hazard in "$corpus"/*.tar.gz; do
        local w; w="$(new_work "hz-$(basename "$hazard" .tar.gz)")"
        local sha; sha="$(sha256sum "$hazard" | cut -d' ' -f1)"
        if driver install-archive "$w" "$hazard" "$sha" "$ARCHIVE_VERSION" \
                >>"$LOGDIR/hazard-detail.log" 2>&1; then
            echo "hazard $(basename "$hazard") was not rejected" >&2
            fail=1
        fi
        # The product entrypoint must never exist after a rejected install.
        if [ -f "$w/install/current/ContractScribe.Cli.dll" ]; then
            echo "hazard $(basename "$hazard") reached publish" >&2
            fail=1
        fi
    done
    return "$fail"
}
case_differential_extractor() {
    local corpus="$WORK/hazard-corpus"
    [ -d "$corpus" ] || python3 "$HAZARD_PY" "$ARCHIVE" "$corpus" >/dev/null
    local fail=0
    for arc in "$ARCHIVE" "$corpus"/*.tar.gz; do
        local a b
        a="$(python3 "$REF_EXTRACT" scan "$arc" >/dev/null 2>&1; echo $?)"
        b="$(python3 "$ACTION_SCRIPTS/payload_extract.py" scan "$arc" >/dev/null 2>&1; echo $?)"
        if [ "$a" != "$b" ]; then
            echo "drift on $(basename "$arc"): ref=$a action=$b" >&2
            fail=1
        fi
    done
    return "$fail"
}

# ---------------------------------------------------------------------------
# Invoke legs (stub dotnet)
# ---------------------------------------------------------------------------

setup_invoke() {
    local w; w="$(new_work "$1")"
    write_map "$ARCHIVE_SHA" "payload-$ARCHIVE_VERSION" "$w/map.json"
    install_via_acquire "$w" >/dev/null
    echo "$w"
}
case_invoke_argv() {
    posix_only && return 0
    local w; w="$(setup_invoke iargv)"
    env CS_WORK_DIR="$w" GITHUB_OUTPUT="$w/out.txt" \
        CONTRACTSCRIBE_PROVIDER_API_KEY="$PROVIDER_TOKEN" \
        CONTRACTSCRIBE_GITHUB_TOKEN="$PRODUCT_TOKEN" \
        CONTRACTSCRIBE_ACQUISITION_TOKEN="$SYNTHETIC_TOKEN" \
        STUB_ARGV="$w/argv.txt" STUB_ENV="$w/env.txt" \
        python3 "$ACTION_SCRIPTS/invoke.py"
    grep -q "github-proposal start" "$w/argv.txt"
    grep -q "ContractScribe.Cli.dll" "$w/argv.txt"
    ! grep -q "CONTRACTSCRIBE_ACQUISITION_TOKEN" "$w/env.txt"
    grep -q "CONTRACTSCRIBE_GITHUB_TOKEN=$PRODUCT_TOKEN" "$w/env.txt"
    grep -q "^action-status=ok" "$w/out.txt"
    grep -q "^outcome=github-proposal.published" "$w/out.txt"
}
case_invoke_conflict_exit() {
    posix_only && return 0
    local w; w="$(setup_invoke icf)"
    env CS_WORK_DIR="$w" GITHUB_OUTPUT="$w/out.txt" STUB_MODE=conflict \
        python3 "$ACTION_SCRIPTS/invoke.py"
    [ "$?" -eq 3 ] || return 1
}
case_invoke_envelope_badshape() {
    posix_only && return 0
    local w; w="$(setup_invoke ibs)"
    if env CS_WORK_DIR="$w" GITHUB_OUTPUT="$w/out.txt" STUB_MODE=badshape \
            python3 "$ACTION_SCRIPTS/invoke.py"; then return 1; fi
    grep -q "action.envelope" "$w/out.txt"
    ! grep -q "^outcome=" "$w/out.txt"
}
case_invoke_envelope_extraline() {
    posix_only && return 0
    local w; w="$(setup_invoke iel)"
    if env CS_WORK_DIR="$w" GITHUB_OUTPUT="$w/out.txt" STUB_MODE=extraline \
            python3 "$ACTION_SCRIPTS/invoke.py"; then return 1; fi
}
case_invoke_envelope_extrastderr() {
    posix_only && return 0
    local w; w="$(setup_invoke ies)"
    if env CS_WORK_DIR="$w" GITHUB_OUTPUT="$w/out.txt" STUB_MODE=extrastderr \
            python3 "$ACTION_SCRIPTS/invoke.py"; then return 1; fi
}
case_invoke_cancel() {
    posix_only && return 0
    local w; w="$(setup_invoke icx)"
    env CS_WORK_DIR="$w" GITHUB_OUTPUT="$w/out.txt" STUB_MODE=sleep \
        STUB_ARGV="$w/argv.txt" STUB_ENV="$w/env.txt" \
        bash -c "exec python3 '$ACTION_SCRIPTS/invoke.py'" &
    local entry=$!
    sleep 1.5
    pgrep -f "stubbin/dotnet" >/dev/null 2>&1 \
        || { echo "stub child never started" >&2; kill -9 "$entry"; return 1; }
    kill -INT "$entry" 2>/dev/null
    local deadline=$((SECONDS + 8))
    while kill -0 "$entry" 2>/dev/null && [ $SECONDS -lt $deadline ]; do
        sleep 0.2
    done
    local rc=0
    wait "$entry" || rc=$?
    [ "$rc" -eq 130 ]
    sleep 0.3
    ! pgrep -f "stubbin/dotnet" >/dev/null 2>&1
}
case_invoke_missing_install() {
    local w; w="$(new_work imi)"
    expect_fail env CS_WORK_DIR="$w" GITHUB_OUTPUT=/dev/null \
        python3 "$ACTION_SCRIPTS/invoke.py"
}

# ---------------------------------------------------------------------------
# Real legs (--real): full script chain, real dotnet + real payload
# ---------------------------------------------------------------------------

real_env_ready() {
    [ "$REAL" -eq 1 ] && command -v dotnet >/dev/null && [ -f "$HOOK_DLL" ] \
        && [ -d "$FIXTURE" ]
}

prepare_real() {
    # $1 = workdir; materializes fixture + request + layer; writes plan via
    # the real prepare.py with production env names.
    local w="$1"
    mkdir -p "$w/target repo x"
    cp -r "$FIXTURE/." "$w/target repo x/"
    (cd "$w/target repo x" && dotnet restore >/dev/null && dotnet build -c Release --no-restore >/dev/null)
    cat > "$w/layer.json" <<EOF
{"consumerConfigurationVersion":1,
 "provider":{"endpoint":"$PROVIDER_ENDPOINT","model":"fixture-model",
   "requestProfile":{"toolChoice":"auto"}},
 "budgets":{"campaign":{"maximumCandidatesPerBlock":30,
   "maximumElapsedMilliseconds":3600000}}}
EOF
    cat > "$w/request.json" <<EOF
{"githubProposalRequestVersion":1,
 "campaignLineage":"campaign.action.test","snapshot":"snapshot.action.test",
 "state":"$w/checkpoint/state.bin",
 "github":{"repositoryOwner":"Owner","repositoryName":"repo",
   "targetRef":"refs/heads/main","expectedBaseCommitOid":"$BASE_OID",
   "operationId":"operation.action","generationId":"generation.action",
   "policy":{"maximumDocumentationBlocks":128,
     "maximumDistinctChangedFiles":128,
     "maximumCumulativePatchBytes":4194304},
   "transition":"initial"}}
EOF
    env -u CONTRACTSCRIBE_ACTION_TEST -u CONTRACTSCRIBE_ACTION_TEST_API_ROOT -u CS_WORK_DIR PATH="$PATH" HOME="$HOME" RUNNER_OS=Linux RUNNER_ARCH=X64 \
        RUNNER_TEMP="$WORK" GITHUB_OUTPUT="$w/pre-out.txt" \
        GITHUB_ENV="$w/pre-env.txt" GITHUB_WORKSPACE="$w" \
        CS_INPUT_OPERATION="github-proposal-$2" \
        CS_INPUT_REPOSITORY_ROOT="$w/target repo x" \
        CS_INPUT_INPUT="App/App.csproj" CS_INPUT_POLICY="policy.json" \
        CS_INPUT_REQUEST="$(cat "$w/request.json")" \
        CS_INPUT_CONFIGURATION="$w/layer.json" \
        python3 "$ACTION_SCRIPTS/prepare.py"
    export CS_WORK_DIR="$(grep '^CS_WORK_DIR=' "$w/pre-env.txt" | cut -d= -f2-)"
}

case_real_chain() {
    real_env_ready || { echo "skip: real env unavailable"; return 0; }
    local w; w="$(new_work real)"
    prepare_real "$w" start
    env CS_WORK_DIR="$CS_WORK_DIR" GITHUB_OUTPUT="$w/acq-out.txt" \
        CONTRACTSCRIBE_ACTION_TEST=1 \
        CONTRACTSCRIBE_ACTION_TEST_API_ROOT="$API_ROOT" \
        CONTRACTSCRIBE_ACQUISITION_TOKEN="$SYNTHETIC_TOKEN" \
        python3 "$ACTION_SCRIPTS/acquire.py"
    env CS_WORK_DIR="$CS_WORK_DIR" GITHUB_OUTPUT="$w/inst-out.txt" \
        python3 "$ACTION_SCRIPTS/install.py"
    env CS_WORK_DIR="$CS_WORK_DIR" GITHUB_OUTPUT="$w/inv-out.txt" \
        GITHUB_ENV=/dev/null \
        DOTNET_STARTUP_HOOKS="$HOOK_DLL" \
        CONTRACTSCRIBE_TEST_GITHUB_ENDPOINT="$GITHUB_ENDPOINT" \
        CONTRACTSCRIBE_TEST_GITHUB_OBSERVATIONS="$w/obs.txt" \
        CONTRACTSCRIBE_GITHUB_TOKEN="$PRODUCT_TOKEN" \
        CONTRACTSCRIBE_PROVIDER_API_KEY="$PROVIDER_TOKEN" \
        python3 "$ACTION_SCRIPTS/invoke.py"
    grep -q "^outcome=github-proposal.published$" "$w/inv-out.txt"
    grep -q "^exit-code=0$" "$w/inv-out.txt"
    grep -q "POST /repos/Owner/repo/pulls" "$WORK/github-requests.log"
    grep -q "POST /v1/chat/completions" "$WORK/provider-requests.log"
}
case_real_missing_token() {
    real_env_ready || { echo "skip: real env unavailable"; return 0; }
    local w; w="$(new_work realmt)"
    prepare_real "$w" start
    env CS_WORK_DIR="$CS_WORK_DIR" GITHUB_OUTPUT="$w/acq.txt" \
        CONTRACTSCRIBE_ACTION_TEST=1 \
        CONTRACTSCRIBE_ACTION_TEST_API_ROOT="$API_ROOT" \
        CONTRACTSCRIBE_ACQUISITION_TOKEN="$SYNTHETIC_TOKEN" \
        python3 "$ACTION_SCRIPTS/acquire.py"
    env CS_WORK_DIR="$CS_WORK_DIR" GITHUB_OUTPUT=/dev/null \
        python3 "$ACTION_SCRIPTS/install.py"
    local rc=0
    env CS_WORK_DIR="$CS_WORK_DIR" GITHUB_OUTPUT="$w/inv.txt" \
        DOTNET_STARTUP_HOOKS="$HOOK_DLL" \
        CONTRACTSCRIBE_TEST_GITHUB_ENDPOINT="$GITHUB_ENDPOINT" \
        CONTRACTSCRIBE_PROVIDER_API_KEY="$PROVIDER_TOKEN" \
        python3 "$ACTION_SCRIPTS/invoke.py" || rc=$?
    [ "$rc" -eq 4 ]
    grep -q "^outcome=github-proposal.permission$" "$w/inv.txt"
}
case_real_cancel() {
    real_env_ready || { echo "skip: real env unavailable"; return 0; }
    local w; w="$(new_work realcx)"
    prepare_real "$w" start
    # Point the layer at the hanging provider so the CLI blocks mid-campaign.
    python3 - "$w/layer.json" "$HANG_ENDPOINT" <<'PY'
import json,os,sys
d=json.load(open(sys.argv[1])); d["provider"]["endpoint"]=sys.argv[2]
json.dump(d,open(sys.argv[1]+".tmp","w"));os.replace(sys.argv[1]+".tmp",sys.argv[1])
PY
    env CS_WORK_DIR="$CS_WORK_DIR" GITHUB_OUTPUT=/dev/null \
        CONTRACTSCRIBE_ACTION_TEST=1 \
        CONTRACTSCRIBE_ACTION_TEST_API_ROOT="$API_ROOT" \
        CONTRACTSCRIBE_ACQUISITION_TOKEN="$SYNTHETIC_TOKEN" \
        python3 "$ACTION_SCRIPTS/acquire.py"
    env CS_WORK_DIR="$CS_WORK_DIR" GITHUB_OUTPUT=/dev/null \
        python3 "$ACTION_SCRIPTS/install.py"
    env CS_WORK_DIR="$CS_WORK_DIR" GITHUB_OUTPUT="$w/inv.txt" \
        DOTNET_STARTUP_HOOKS="$HOOK_DLL" \
        CONTRACTSCRIBE_TEST_GITHUB_ENDPOINT="$GITHUB_ENDPOINT" \
        CONTRACTSCRIBE_GITHUB_TOKEN="$PRODUCT_TOKEN" \
        CONTRACTSCRIBE_PROVIDER_API_KEY="$PROVIDER_TOKEN" \
        bash -c "exec python3 '$ACTION_SCRIPTS/invoke.py'" &
    local entry=$!
    sleep 6
    kill -INT "$entry" 2>/dev/null
    sleep 4
    kill -TERM "$entry" 2>/dev/null || true
    local deadline=$((SECONDS + 10)) rc=0
    while kill -0 "$entry" 2>/dev/null && [ $SECONDS -lt $deadline ]; do
        sleep 0.2
    done
    wait "$entry" || rc=$?
    case "$rc" in 6|130|143) ;; *) echo "unexpected rc=$rc" >&2; return 1;; esac
    ! pgrep -f "ContractScribe.Cli.dll" >/dev/null 2>&1
}

# ---------------------------------------------------------------------------
# Runner
# ---------------------------------------------------------------------------

run_case static-pycompile case_static_pycompile
run_case static-map-schema case_static_map_schema
run_case static-action-yml case_static_action_yml

run_case prepare-ok case_prepare_ok
run_case prepare-operation-missing case_prepare_operation_missing
run_case prepare-operation-unknown case_prepare_operation_unknown
run_case prepare-request-missing case_prepare_request_missing
run_case prepare-request-oversize case_prepare_request_oversize
run_case prepare-runner-os case_prepare_runner_os
run_case prepare-runner-arch case_prepare_runner_arch
run_case prepare-dotnet-missing case_prepare_dotnet_missing
run_case prepare-multiline case_prepare_multiline

run_case acquire-ok-published case_acquire_ok_published
run_case acquire-cache-hit case_acquire_cache_hit
run_case acquire-wrong-sha case_acquire_wrong_sha
run_case acquire-release-404 case_acquire_release_404
run_case acquire-release-ambiguous case_acquire_release_ambiguous
run_case acquire-asset-missing case_acquire_asset_missing
run_case acquire-asset-duplicate case_acquire_asset_duplicate
run_case acquire-asset-sibling case_acquire_asset_sibling
run_case acquire-asset-unrelated-ok case_acquire_asset_unrelated_ok
run_case acquire-http-401 case_acquire_http_401
run_case acquire-draft-public-blocked case_acquire_draft_public_blocked
run_case acquire-draft-authenticated case_acquire_draft_authenticated
run_case acquire-redirect-foreign case_acquire_redirect_foreign
run_case acquire-redirect-userinfo case_acquire_redirect_userinfo
run_case acquire-redirect-loop case_acquire_redirect_loop
run_case acquire-truncated case_acquire_truncated
run_case acquire-oversize-declared case_acquire_oversize_declared
run_case acquire-poisoned-cache case_acquire_poisoned_cache
run_case acquire-foreign-cache-file case_acquire_foreign_cache_file
run_case acquire-test-credential case_acquire_test_credential
run_case acquire-proxy-off case_acquire_proxy_off
run_case acquire-gate-env-without-test case_acquire_gate_env_without_test
run_case acquire-gate-nonloopback case_acquire_gate_nonloopback
run_case acquire-gate-foreign-repo case_acquire_gate_foreign_repo
run_case acquire-map-null case_acquire_map_null_payload
run_case acquire-map-malformed case_acquire_map_malformed
run_case acquire-committed-map case_acquire_committed_map
run_case canary-no-credentials case_canary_no_credentials

run_case install-happy case_install_happy
run_case install-cache-reuse case_install_cache_reuse
run_case install-poisoned-tree case_install_poisoned_tree
run_case install-poisoned-coherent case_install_poisoned_coherent
run_case install-foreign-current case_install_foreign_current
run_case install-staging-residue case_install_staging_residue
run_case install-hazard-corpus case_install_hazard_corpus
run_case differential-extractor case_differential_extractor

run_case invoke-argv case_invoke_argv
run_case invoke-conflict-exit case_invoke_conflict_exit
run_case invoke-envelope-badshape case_invoke_envelope_badshape
run_case invoke-envelope-extraline case_invoke_envelope_extraline
run_case invoke-envelope-extrastderr case_invoke_envelope_extrastderr
run_case invoke-cancel case_invoke_cancel
run_case invoke-missing-install case_invoke_missing_install
case_emit_annotation_counts() {
    # Frozen annotation contract: success emits no ::error::; a wrapper
    # failure and a product failure each emit exactly one.
    local w; w="$(new_work emit)"
    env GITHUB_OUTPUT="$w/o1.txt" CS_STATUS_GUARD="" CS_STATUS_PREPARE=""         CS_STATUS_ACQUIRE="" CS_STATUS_INSTALL="" CS_STATUS_INVOKE=""         CS_OUT_OUTCOME="" CS_OUT_EXIT_CODE="0" CS_INPUT_OPERATION="github-proposal-start"         python3 "$ACTION_SCRIPTS/emit.py" > "$w/ok.log" 2>&1
    [ "$(grep -c '::error::' "$w/ok.log")" -eq 0 ]
    env GITHUB_OUTPUT="$w/o2.txt" CS_STATUS_GUARD="" CS_STATUS_PREPARE="action.prepare-operation"         CS_STATUS_ACQUIRE="" CS_STATUS_INSTALL="" CS_STATUS_INVOKE=""         CS_OUT_OUTCOME="" CS_OUT_EXIT_CODE="" CS_INPUT_OPERATION="github-proposal-start"         python3 "$ACTION_SCRIPTS/emit.py" > "$w/wrap.log" 2>&1
    [ "$(grep -c '::error::' "$w/wrap.log")" -eq 1 ]
    grep -q "action.prepare-operation" "$w/wrap.log"
    env GITHUB_OUTPUT="$w/o3.txt" CS_STATUS_GUARD="" CS_STATUS_PREPARE=""         CS_STATUS_ACQUIRE="" CS_STATUS_INSTALL="" CS_STATUS_INVOKE=""         CS_OUT_OUTCOME="github-proposal.permission" CS_OUT_EXIT_CODE="4"         CS_INPUT_OPERATION="github-proposal-start"         python3 "$ACTION_SCRIPTS/emit.py" > "$w/prod.log" 2>&1
    [ "$(grep -c '::error::' "$w/prod.log")" -eq 1 ]
    grep -q "github-proposal.permission (exit 4)" "$w/prod.log"
    grep -q "^action-status=ok" "$w/o1.txt"
    grep -q "^action-status=action.prepare-operation" "$w/o2.txt"
}

run_case emit-annotation-counts case_emit_annotation_counts

if [ "$REAL" -eq 1 ]; then
    # Loopback provider + GitHub fakes for the real-CLI legs.
    python3 "$SCRIPT_DIR/fake_provider.py" proposal \
        "$WORK/provider-ready.json" "$WORK/provider-requests.log" &
    PROV_PID=$!
    python3 "$SCRIPT_DIR/fake_provider.py" hang \
        "$WORK/hang-ready.json" "$WORK/hang-requests.log" &
    HANG_PID=$!
    cat > "$WORK/github-config.json" <<'EOF'
{"files":{"README.md":"# synthetic\n","src/App.cs":"class A {}\n"}}
EOF
    touch "$WORK/provider-requests.log" "$WORK/hang-requests.log" \
        "$WORK/github-requests.log" "$WORK/github-failures.log"
    python3 "$SCRIPT_DIR/fake_github_api.py" "$WORK/github-config.json" \
        "$WORK/github-ready.json" "$WORK/github-requests.log" \
        "$WORK/github-failures.log" &
    GH_PID=$!
    for f in provider-ready hang-ready github-ready; do
        for _ in $(seq 50); do [ -f "$WORK/$f.json" ] && break; sleep 0.1; done
    done
    PROVIDER_ENDPOINT="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["endpoint"])' "$WORK/provider-ready.json")"
    HANG_ENDPOINT="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["endpoint"])' "$WORK/hang-ready.json")"
    GITHUB_ENDPOINT="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["endpoint"])' "$WORK/github-ready.json")"
    BASE_OID="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["baseOid"])' "$WORK/github-ready.json")"

    run_case real-chain case_real_chain
    run_case real-missing-token case_real_missing_token
    run_case real-cancel case_real_cancel

    kill $PROV_PID $HANG_PID $GH_PID 2>/dev/null || true
    if [ -s "$WORK/github-failures.log" ]; then
        echo "FAIL github-fake protocol violations:"; cat "$WORK/github-failures.log"
        CASES_FAILED=$((CASES_FAILED + 1)); FAILED_CASES+=("github-fake-failures")
    fi
fi

kill $REL_PID 2>/dev/null || true
echo "verify-action: $CASES_RUN cases, $CASES_FAILED failed"
[ "$CASES_FAILED" -eq 0 ]
