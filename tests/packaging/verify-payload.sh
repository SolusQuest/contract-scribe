#!/usr/bin/env bash
# verify-payload.sh — packed-path verification matrix for the D2 payload
# (issue #18 / M6-A1). Runs the full install/extraction/execution matrix
# against a single verified payload archive:
#
#   bash tests/packaging/verify-payload.sh \
#       --archive payload/contract-scribe-<ver>-linux-x64.tar.gz \
#       --sha256-file payload/contract-scribe-<ver>-linux-x64.sha256 \
#       --kit kit
#
# Modes:
#   --kit <dir>      consumer layout: <dir>/{tests/packaging,fixture,expected,
#                  testbin,hook} as produced by the payload_producer job
#   --repo <dir>     source checkout layout: packaging scripts + fixture under
#                  tests/packaging, testbin under the IntegrationTests build
#                  output, hook under the CampaignStartupHook build output;
#                  --expected still required for comparison legs
#   --expected <dir> override the expected-data directory
#   --work <dir>     work directory (default: mktemp; removed unless --keep)
#   --keep           keep the work directory
#   --only <regex>   run only cases whose name matches (install-happy always
#                  runs — every dependent case needs the installation)
#
# If --archive is omitted the script runs scripts/release/build-payload.sh
# (requires --repo). Every leg emits PASS/FAIL lines; logs land in
# <work>/logs/. Exit 0 only when every case passed.
set -uo pipefail

ARCHIVE=""
SHA_FILE=""
KIT=""
REPO=""
EXPECTED=""
WORK=""
KEEP=0
SKIP_NETNS=0
ONLY=""

while [ $# -gt 0 ]; do
    case "$1" in
        --archive) ARCHIVE="$2"; shift 2 ;;
        --sha256-file) SHA_FILE="$2"; shift 2 ;;
        --kit) KIT="$2"; shift 2 ;;
        --repo) REPO="$2"; shift 2 ;;
        --expected) EXPECTED="$2"; shift 2 ;;
        --work) WORK="$2"; shift 2 ;;
        --keep) KEEP=1; shift ;;
        --skip-netns) SKIP_NETNS=1; shift ;;
        --only) ONLY="$2"; shift 2 ;;
        *) echo "verify-payload: unknown argument $1" >&2; exit 2 ;;
    esac
done

# --work supplies a PARENT for a fresh owned child; a pre-existing caller
# directory's contents are never deleted by the cleanup trap. Every
# allocation step must succeed before the cleanup trap is installed — a
# failed allocation can never assign an unowned directory to $WORK.
if [ -z "$WORK" ]; then
    WORK="$(mktemp -d "${TMPDIR:-/tmp}/contract-scribe-verify.XXXXXX")" || {
        echo "verify-payload: cannot allocate work dir" >&2; exit 1; }
else
    mkdir -p -- "$WORK" 2>/dev/null || {
        echo "verify-payload: --work parent unusable: $WORK" >&2; exit 1; }
    [ -d "$WORK" ] || {
        echo "verify-payload: --work parent is not a directory: $WORK" >&2; exit 1; }
    WORK="$(mktemp -d "$WORK/payload-verify.XXXXXX")" || {
        echo "verify-payload: cannot allocate work dir under: $WORK" >&2; exit 1; }
fi
WORK="$(cd "$WORK" && pwd)" || {
    echo "verify-payload: cannot normalize work dir" >&2; exit 1; }
[ -n "$WORK" ] && [ -d "$WORK" ] || {
    echo "verify-payload: work dir invalid after allocation" >&2; exit 1; }
LOGDIR="$WORK/logs"
mkdir -p "$LOGDIR"
if [ "$KEEP" -eq 0 ]; then
    trap 'rm -rf "$WORK"' EXIT
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT_SELF="$SCRIPT_DIR/$(basename "${BASH_SOURCE[0]}")"

if [ -n "$KIT" ]; then
    KIT="$(cd "$KIT" && pwd)"
    PACKAGING="$KIT/tests/packaging"
    FIXTURE="$KIT/fixture"
    TESTBIN="$KIT/testbin"
    HOOK="$KIT/hook/ContractScribe.CampaignStartupHook.dll"
    [ -z "$EXPECTED" ] && EXPECTED="$KIT/expected"
elif [ -n "$REPO" ]; then
    REPO="$(cd "$REPO" && pwd)"
    PACKAGING="$REPO/tests/packaging"
    FIXTURE="$PACKAGING/fixture"
    TESTBIN="$REPO/tests/ContractScribe.IntegrationTests/bin/Release/net10.0"
    HOOK="$REPO/tests/ContractScribe.CampaignStartupHook/bin/Release/net10.0/ContractScribe.CampaignStartupHook.dll"
else
    echo "verify-payload: either --kit or --repo is required" >&2
    exit 2
fi

# shellcheck source=install-payload.sh
. "$PACKAGING/install-payload.sh"
PAYLOAD_EXTRACT_PY="$PACKAGING/payload_extract.py"
HAZARD_PY="$PACKAGING/make_hazard.py"

for f in "$PAYLOAD_EXTRACT_PY" "$HAZARD_PY"; do
    [ -f "$f" ] || { echo "verify-payload: missing $f" >&2; exit 2; }
done

if [ -z "$ARCHIVE" ]; then
    [ -n "$REPO" ] || { echo "verify-payload: --archive is required outside --repo mode" >&2; exit 2; }
    echo "== build-payload =="
    "$REPO/scripts/release/build-payload.sh" --output "$WORK/artifacts" || exit 2
    ARCHIVE="$(find "$WORK/artifacts" -name 'contract-scribe-*-linux-x64.tar.gz' | head -1)"
fi
ARCHIVE="$(cd "$(dirname "$ARCHIVE")" && pwd)/$(basename "$ARCHIVE")"
[ -f "$ARCHIVE" ] || { echo "verify-payload: missing archive $ARCHIVE" >&2; exit 2; }
if [ -z "$SHA_FILE" ]; then
    SHA_FILE="${ARCHIVE%.tar.gz}.sha256"
fi
[ -f "$SHA_FILE" ] || { echo "verify-payload: missing sha256 sidecar $SHA_FILE" >&2; exit 2; }
EXPECTED_SHA="$(awk '{print $1}' "$SHA_FILE")"
[ -n "$EXPECTED" ] && EXPECTED="$(cd "$EXPECTED" && pwd)"

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

expect_install_reject() {
    local stage="$1"; shift
    local log="$LOGDIR/_last-install.log"
    if PAYLOAD_INSTALL_DIR="" payload_install "$@" >"$log" 2>&1; then
        echo "expected rejection at stage=$stage but install succeeded" >&2
        return 1
    fi
    grep -q "payload-install stage=$stage result=fail" "$log" \
        || { echo "expected stage=$stage rejection; markers:" >&2
             grep "payload-install" "$log" >&2; return 1; }
    return 0
}

# Network isolation: prefer an unprivileged user-namespace netns; Ubuntu
# 24.04's default AppArmor policy blocks those, so fall back to a sudo root
# netns that drops privileges before running the command (runner users have
# passwordless sudo). NETNS_MODE records which mechanism netns_exec must use.
NETNS_MODE=""
if [ "$SKIP_NETNS" -eq 0 ]; then
    if unshare --user --net --map-root-user true 2>/dev/null; then
        NETNS_MODE="userns"
    elif command -v sudo >/dev/null 2>&1 && sudo -n unshare -n true 2>/dev/null; then
        NETNS_MODE="sudo"
    fi
fi

# netns_exec <loopback:0|1> <cmd...> — run cmd inside ONE isolated network
# namespace; loopback=1 brings `lo` up first (as root where required). All
# non-root work runs as the invoking user so created files stay owned.
netns_exec() {
    local lo_up="$1"; shift
    if [ "$NETNS_MODE" = "userns" ]; then
        if [ "$lo_up" = 1 ]; then
            unshare --user --net --map-root-user bash -c 'ip link set lo up; exec "$@"' _ "$@"
        else
            unshare --user --net --map-root-user "$@"
        fi
        return
    fi
    if [ "$NETNS_MODE" = "sudo" ]; then
        local uid gid
        uid="$(id -u)"; gid="$(id -g)"
        if [ "$lo_up" = 1 ]; then
            sudo -n unshare -n bash -c 'ip link set lo up; exec env PATH="'"$PATH"'" HOME="'"$HOME"'" setpriv --reuid='"$uid"' --regid='"$gid"' --clear-groups "$@"' _ "$@"
        else
            sudo -n unshare -n env PATH="$PATH" HOME="$HOME" setpriv --reuid="$uid" --regid="$gid" --clear-groups "$@"
        fi
        return
    fi
    return 2
}

# PATH with no usable dotnet host: dirs that resolve `dotnet` are replaced
# by a private link-farm of their other contents, so /usr/bin's coreutils
# stay available while every dotnet alias (binary or symlink) disappears.
HOSTLESS_BIN="$WORK/hostless-bin"
mkdir -p "$HOSTLESS_BIN"
HOSTLESS_PATH=""
_old_ifs="$IFS"; IFS=':'
read -ra _path_dirs <<< "$PATH"
IFS="$_old_ifs"
for _d in "${_path_dirs[@]}"; do
    [ -n "$_d" ] && [ -d "$_d" ] || continue
    if [ -x "$_d/dotnet" ] || [ -x "$_d/dotnet.exe" ]; then
        for _f in "$_d"/*; do
            [ -e "$_f" ] || continue
            _b="$(basename "$_f")"
            case "$_b" in dotnet|dotnet.exe) continue ;; esac
            ln -sfn "$_f" "$HOSTLESS_BIN/$_b"
        done
    else
        HOSTLESS_PATH="$HOSTLESS_PATH:$_d"
    fi
done
HOSTLESS_PATH="$HOSTLESS_BIN$HOSTLESS_PATH"

# Real symlinks are unavailable on some development hosts (MSYS copies
# instead); Linux CI always has them. Legs needing a link check this probe.
CAN_SYMLINK=0
_probe="$WORK/.symlink-probe"
if ln -s "$WORK" "$_probe" 2>/dev/null && [ -L "$_probe" ]; then
    CAN_SYMLINK=1
fi
rm -rf "$_probe"

# The declared/actual mode comparison is meaningful only on POSIX hosts.
POSIX_MODE=0
[ "$(uname -s 2>/dev/null)" = "Linux" ] && POSIX_MODE=1

INSTALL_ROOT="$WORK/install root"
INSTALL_DIR=""
MANIFEST=""
TOOL_VERSION=""

manifest_get() {
    # Path as argv (MSYS converts it for the native python3 executable).
    python3 -c "import json,sys; print(json.load(open(sys.argv[1]))$1)" "$MANIFEST"
}

materialize_fixture() {
    local dest="$1"
    mkdir -p "$dest"
    (cd "$FIXTURE" && find . -type d \( -name bin -o -name obj \) -prune -o -type f -print \
        | while read -r f; do
            mkdir -p "$dest/$(dirname "$f")"; cp "$f" "$dest/$f"
        done)
    dotnet restore "$dest/Fixture.slnx" --nologo >/dev/null
    # Debug, not the product's Release: the loader's default design-time
    # generated inputs are Debug. A first load that generates them is a
    # protected drift (see docs/20_architecture/validation/m5-github-proposal-proof.md).
    dotnet build "$dest/Fixture.slnx" --no-restore --nologo >/dev/null
}

# ---------- cases ----------

case_toolchain() {
    command -v dotnet; command -v python3; command -v tar; command -v sha256sum
    command -v unshare; command -v ip; command -v sudo; command -v setpriv
    tar --version | head -1
    dotnet --list-sdks
    dotnet --list-runtimes | grep '^Microsoft\.NETCore\.App'
    python3 --version
}

case_acquire_missing_artifact() {
    expect_install_reject acquire \
        "$WORK/no-such-archive.tar.gz" "$EXPECTED_SHA" "$WORK/x"
}

case_acquire_wrong_sha() {
    expect_install_reject digest \
        "$ARCHIVE" "0000000000000000000000000000000000000000000000000000000000000000" "$WORK/x"
}

# Rejection at any stage counts — a mid-stream byte flip can leave a valid
# tar with corrupted member data (gzip CRC is only read at physical EOF), in
# which case the inventory stage catches it instead of scan/extract.
expect_install_reject_any() {
    local log="$LOGDIR/_last-install.log"
    if PAYLOAD_INSTALL_DIR="" payload_install "$@" >"$log" 2>&1; then
        echo "expected rejection but install succeeded" >&2
        return 1
    fi
    grep -q "payload-install stage=.* result=fail" "$log" \
        || { echo "no fail marker recorded:" >&2; cat "$log" >&2; return 1; }
    return 0
}

case_acquire_truncated_archive() {
    local trunc="$WORK/truncated.tar.gz"
    local size
    size=$(($(stat -c %s "$ARCHIVE") - 1024))
    head -c "$size" "$ARCHIVE" > "$trunc"
    expect_install_reject digest "$trunc" "$EXPECTED_SHA" "$WORK/x"
    local trunc_sha
    trunc_sha="$(sha256sum "$trunc" | cut -d' ' -f1)"
    expect_install_reject_any "$trunc" "$trunc_sha" "$WORK/x"
}

case_acquire_corrupt_archive() {
    local corrupt="$WORK/corrupt.tar.gz"
    cp "$ARCHIVE" "$corrupt"
    printf '\xff' | dd of="$corrupt" bs=1 seek=$(( $(stat -c %s "$corrupt") / 2 )) conv=notrunc status=none
    expect_install_reject digest "$corrupt" "$EXPECTED_SHA" "$WORK/x"
    local corrupt_sha
    corrupt_sha="$(sha256sum "$corrupt" | cut -d' ' -f1)"
    expect_install_reject_any "$corrupt" "$corrupt_sha" "$WORK/x"
}

hazard_case() {
    local name="$1"; shift
    local hazard="$WORK/hazard-$name.tar.gz"
    python3 "$HAZARD_PY" "$hazard" "$@" || return 1
    local hazard_sha
    hazard_sha="$(sha256sum "$hazard" | cut -d' ' -f1)"
    # The member policy — not the digest — must reject the archive.
    expect_install_reject scan "$hazard" "$hazard_sha" "$WORK/hazard-$name-root" \
        || expect_install_reject extract "$hazard" "$hazard_sha" "$WORK/hazard-$name-root"
}

case_hazard_traversal()   { hazard_case traversal   "hazard:traversal"; }
case_hazard_absolute()    { hazard_case absolute    "hazard:absolute"; }
case_hazard_dotdot()      { hazard_case dotdot      "hazard:dotdot"; }
case_hazard_symlink()     { hazard_case symlink     "hazard:symlink"; }
case_hazard_hardlink()    { hazard_case hardlink    "hazard:hardlink"; }
case_hazard_device()      { hazard_case device      "hazard:device"; }
case_hazard_fifo()        { hazard_case fifo        "hazard:fifo"; }
case_hazard_sparse()      { hazard_case sparse      "hazard:sparse"; }
case_hazard_paxsparse()   { hazard_case paxsparse   "hazard:paxsparse"; }
case_hazard_duplicate()   { hazard_case duplicate   "hazard:duplicate"; }
case_hazard_collision()   { hazard_case collision   "hazard:collision"; }
case_hazard_secondtop()   { hazard_case secondtop   "hazard:secondtop"; }
case_hazard_paxbomb()     { hazard_case paxbomb     "hazard:paxbomb"; }
case_hazard_longnamebomb() { hazard_case longnamebomb "hazard:longnamebomb"; }

case_install_happy() {
    local log="$LOGDIR/_install-happy.markers"
    if ! payload_install "$ARCHIVE" "$EXPECTED_SHA" "$INSTALL_ROOT" >"$log" 2>&1; then
        cat "$log" >&2
        return 1
    fi
    cat "$log"
    local stage
    for stage in acquire digest scan extract inventory prereq publish; do
        grep -q "stage=$stage result=ok" "$log" \
            || { echo "missing stage=$stage ok marker"; return 1; }
    done
    INSTALL_DIR="$PAYLOAD_INSTALL_DIR"
    [ -d "$INSTALL_DIR" ] || return 1
    [ -f "$INSTALL_DIR/payload.json" ] || return 1
    MANIFEST="$INSTALL_DIR/payload.json"
    TOOL_VERSION="$(manifest_get "['toolVersion']")"
    [ "$(manifest_get "['sourceRevision']")" != "" ] || return 1
    payload_sdk_present || echo "note: SDK presence recorded as $(payload_sdk_present && echo yes || echo no)"
    echo "installed: $INSTALL_DIR"
    echo "toolVersion: $TOOL_VERSION"
}

# Packed CLI runs go through files, not $(dotnet ...) — see build-payload.sh.
packed() {
    local out_file="$1"; shift
    dotnet "$INSTALL_DIR/ContractScribe.Cli.dll" "$@" >"$out_file" 2>&1
}

case_selector_pinning() {
    local top
    top="$(basename "$INSTALL_DIR")"
    payload_select "$INSTALL_ROOT" "$top" || return 1
    [ "$(readlink "$INSTALL_ROOT/current")" = "$top" ] || return 1
    dotnet "$INSTALL_ROOT/current/ContractScribe.Cli.dll" --version \
        | grep -q "ContractScribe $TOOL_VERSION" || return 1
    payload_select "$INSTALL_ROOT" "$top"
    [ "$(readlink "$INSTALL_ROOT/current")" = "$top" ] || return 1
}

case_identity_version() {
    packed "$WORK/version.txt" --version || {
        echo "packed --version failed"; cat "$WORK/version.txt"; return 1; }
    local got
    got="$(cat "$WORK/version.txt")"
    [ "$got" = "ContractScribe $TOOL_VERSION" ] || {
        echo "version mismatch: '$got' != 'ContractScribe $TOOL_VERSION'"; return 1; }
    [ -f "$EXPECTED/version.txt" ] || {
        echo "missing required oracle: version.txt"; return 1; }
    diff -u "$EXPECTED/version.txt" "$WORK/version.txt" || return 1
}

case_identity_help() {
    local name
    for name in help-top help-audit help-campaign help-github-proposal; do
        [ -f "$EXPECTED/$name.txt" ] || {
            echo "missing required oracle: $name.txt"; return 1; }
        case "$name" in
            help-top)             packed "$WORK/$name.txt" --help ;;
            help-audit)           packed "$WORK/$name.txt" audit --help ;;
            help-campaign)        packed "$WORK/$name.txt" campaign --help ;;
            help-github-proposal) packed "$WORK/$name.txt" github-proposal --help ;;
        esac || { echo "$name: help invocation failed"; return 1; }
        diff -u "$EXPECTED/$name.txt" "$WORK/$name.txt" || return 1
    done
}

case_identity_invalid_usage() {
    packed "$WORK/bogus.txt" bogus-command
    local rc=$?
    [ "$rc" -eq 2 ] || { echo "expected exit 2, got $rc"; cat "$WORK/bogus.txt"; return 1; }
    grep -q "cli.usage.unknown-command" "$WORK/bogus.txt" || return 1
}

case_location_doctor() {
    (cd / && dotnet "$INSTALL_DIR/ContractScribe.Cli.dll" doctor) >"$WORK/doctor.txt" 2>&1 || {
        echo "doctor failed"; cat "$WORK/doctor.txt"; return 1; }
    cat "$WORK/doctor.txt"
    grep -q "runtime_identifier: linux-x64" "$WORK/doctor.txt" || return 1
    grep -q "network_access: not performed" "$WORK/doctor.txt" || return 1
    grep -q "credential_access: not performed" "$WORK/doctor.txt" || return 1
}

case_defaults_exact_bytes() {
    [ -f "$EXPECTED/config-defaults.json" ] || {
        echo "missing required oracle: config-defaults.json"; return 1; }
    cmp "$EXPECTED/config-defaults.json" "$INSTALL_DIR/config/defaults.json"
}

case_prereq_no_host() {
    # The constructed PATH must genuinely lack every dotnet alias while the
    # installer's own utilities stay resolvable.
    if PATH="$HOSTLESS_PATH" command -v dotnet >/dev/null 2>&1; then
        echo "hostless PATH still resolves dotnet: $(PATH="$HOSTLESS_PATH" command -v dotnet)"
        return 1
    fi
    PATH="$HOSTLESS_PATH" command -v sha256sum >/dev/null 2>&1 || {
        echo "hostless PATH lost sha256sum"; return 1; }
    PATH="$HOSTLESS_PATH" command -v python3 >/dev/null 2>&1 || {
        echo "hostless PATH lost python3"; return 1; }
    PATH="$HOSTLESS_PATH" expect_install_reject prereq \
        "$ARCHIVE" "$EXPECTED_SHA" "$WORK/prereq-nohost"
}

copy_toolchain_part() {
    # copy_toolchain_part <src> <dest> — hardlink where possible, plain copy
    # otherwise (MSYS2 cp -al can leave a half-created tree, so clean first).
    if ! cp -al "$1" "$2" 2>/dev/null; then
        rm -rf "$2"
        cp -a "$1" "$2"
    fi
}

make_private_toolchain() {
    # make_private_toolchain <dest> <copy-shared:0|1>
    local dest="$1" with_shared="$2"
    local dotnet_home
    dotnet_home="$(cd "$(dirname "$(command -v dotnet)")" && pwd)"
    mkdir -p "$dest"
    copy_toolchain_part "$dotnet_home/dotnet" "$dest/dotnet"
    copy_toolchain_part "$dotnet_home/host" "$dest/host"
    if [ "$with_shared" -eq 1 ]; then
        copy_toolchain_part "$dotnet_home/shared" "$dest/shared"
    fi
}

case_prereq_no_runtime() {
    local tc="$WORK/tc-noruntime"
    make_private_toolchain "$tc" 0
    [ -z "$("$tc/dotnet" --list-runtimes 2>/dev/null)" ] || {
        echo "private layout unexpectedly sees runtimes"; return 1; }
    PATH="$tc:$HOSTLESS_PATH" expect_install_reject prereq \
        "$ARCHIVE" "$EXPECTED_SHA" "$WORK/prereq-noruntime"
    # The private host itself must not start the product without a runtime.
    if PATH="$tc:$HOSTLESS_PATH" dotnet "$INSTALL_DIR/ContractScribe.Cli.dll" --version >/dev/null 2>&1; then
        echo "packed CLI ran without a compatible runtime"; return 1
    fi
}

case_prereq_no_sdk() {
    local tc="$WORK/tc-nosdk"
    make_private_toolchain "$tc" 1
    [ -z "$("$tc/dotnet" --list-sdks 2>/dev/null)" ] || {
        echo "private layout unexpectedly sees SDKs"; return 1; }
    "$tc/dotnet" --list-runtimes | grep -q '^Microsoft\.NETCore\.App 10\.' || {
        echo "private layout missing shared runtime"; return 1; }
    # Runtime-only: the product --version command works; the SDK command fails.
    PATH="$tc:$HOSTLESS_PATH" dotnet "$INSTALL_DIR/ContractScribe.Cli.dll" --version \
        >"$WORK/nosdk-version.txt" 2>&1 || {
            echo "packed --version did not run under runtime-only toolchain"
            cat "$WORK/nosdk-version.txt"; return 1; }
    grep -q "ContractScribe" "$WORK/nosdk-version.txt" || {
        cat "$WORK/nosdk-version.txt"; return 1; }
    if PATH="$tc:$HOSTLESS_PATH" dotnet --version >"$WORK/nosdk-sdkver.txt" 2>&1; then
        echo "SDK command unexpectedly succeeded without SDK"; return 1
    fi
    # Semantic loading requires the SDK: the packed audit reaches a bounded
    # load failure rather than a crash or a silent pass.
    local fx="$WORK/fx-nosdk"
    materialize_fixture "$fx"
    local rc=0
    PATH="$tc:$HOSTLESS_PATH" dotnet "$INSTALL_DIR/ContractScribe.Cli.dll" audit \
        --repository-root "$fx" --input Fixture.slnx --policy policy.json \
        --output "$WORK/audit-nosdk.json" >"$WORK/nosdk.out" 2>&1 || rc=$?
    tail -5 "$WORK/nosdk.out"
    [ "$rc" -ne 0 ] || { echo "audit unexpectedly succeeded without SDK"; return 1; }
    [ "$(wc -c <"$WORK/nosdk.out")" -lt 65536 ] || { echo "unbounded failure output"; return 1; }
}

case_inventory_negative() {
    local tamper_root="$WORK/install-tamper"
    payload_install "$ARCHIVE" "$EXPECTED_SHA" "$tamper_root" >/dev/null || {
        echo "baseline install for tamper cases failed"; return 1; }
    local dir="$tamper_root/$(basename "$INSTALL_DIR")"
    local failure=0

    rm "$dir/ContractScribe.Core.dll"
    payload_verify_inventory "$dir" && { echo "missing-assembly not detected"; failure=1; }
    cp "$INSTALL_DIR/ContractScribe.Core.dll" "$dir/ContractScribe.Core.dll"

    printf 'tampered\n' > "$dir/config/defaults.json"
    payload_verify_inventory "$dir" && { echo "modified defaults not detected"; failure=1; }
    cp "$INSTALL_DIR/config/defaults.json" "$dir/config/defaults.json"

    rm "$dir/ContractScribe.Cli.deps.json"
    payload_verify_inventory "$dir" && { echo "missing deps.json not detected"; failure=1; }
    cp "$INSTALL_DIR/ContractScribe.Cli.deps.json" "$dir/ContractScribe.Cli.deps.json"

    rm "$dir/ContractScribe.Cli.runtimeconfig.json"
    payload_verify_inventory "$dir" && { echo "missing runtimeconfig not detected"; failure=1; }
    cp "$INSTALL_DIR/ContractScribe.Cli.runtimeconfig.json" "$dir/ContractScribe.Cli.runtimeconfig.json"

    local buildhost_member
    buildhost_member="$(find "$dir" -path '*/BuildHost-*/*' -type f | head -1)"
    if [ -n "$buildhost_member" ]; then
        rm "$buildhost_member"
        payload_verify_inventory "$dir" && { echo "missing BuildHost member not detected"; failure=1; }
        local rel="${buildhost_member#"$dir"/}"
        cp "$INSTALL_DIR/$rel" "$buildhost_member"
    fi

    echo injected > "$dir/injected.txt"
    payload_verify_inventory "$dir" && { echo "extra file not detected"; failure=1; }
    rm "$dir/injected.txt"

    # Manifest identity tampering — each mutation must reject.
    local manifest="$dir/payload.json"
    cp "$manifest" "$WORK/manifest.orig.json"
    python3 - "$manifest" <<'PYEOF'
import json, sys
m = json.load(open(sys.argv[1]))
m["defaultsJsonSha256"] = "0" * 64
json.dump(m, open(sys.argv[1], "w", encoding="utf-8", newline="\n"), indent=2)
PYEOF
    payload_verify_inventory "$dir" && { echo "defaults-digest mismatch not detected"; failure=1; }
    cp "$WORK/manifest.orig.json" "$manifest"

    python3 - "$manifest" <<'PYEOF'
import json, sys
m = json.load(open(sys.argv[1]))
m["files"].append(dict(m["files"][0]))
json.dump(m, open(sys.argv[1], "w", encoding="utf-8", newline="\n"), indent=2)
PYEOF
    payload_verify_inventory "$dir" && { echo "duplicate inventory path not detected"; failure=1; }
    cp "$WORK/manifest.orig.json" "$manifest"

    python3 - "$manifest" <<'PYEOF'
import json, sys
m = json.load(open(sys.argv[1]))
m["files"][0]["mode"] = "0777"
json.dump(m, open(sys.argv[1], "w", encoding="utf-8", newline="\n"), indent=2)
PYEOF
    payload_verify_inventory "$dir" && { echo "invalid mode not detected"; failure=1; }
    cp "$WORK/manifest.orig.json" "$manifest"

    python3 - "$manifest" <<'PYEOF'
import json, sys
m = json.load(open(sys.argv[1]))
m["sourceRevision"] = "f" * 40
json.dump(m, open(sys.argv[1], "w", encoding="utf-8", newline="\n"), indent=2)
PYEOF
    payload_verify_inventory "$dir" && { echo "version/revision disagreement not detected"; failure=1; }
    cp "$WORK/manifest.orig.json" "$manifest"

    # The manifest itself must be a regular no-follow file.
    if [ "$CAN_SYMLINK" -eq 1 ]; then
        cp "$manifest" "$WORK/manifest-outside.json"
        rm "$manifest"
        ln -s "$WORK/manifest-outside.json" "$manifest"
        payload_verify_inventory "$dir" && {
            echo "linked manifest not detected"; failure=1; }
        rm "$manifest"
        cp "$WORK/manifest.orig.json" "$manifest"
    fi

    # The runtime identifier is the frozen linux-x64 value, not any RID.
    python3 - "$manifest" <<'PYEOF'
import json, sys
m = json.load(open(sys.argv[1]))
m["runtimeIdentifier"] = "linux-arm64"
json.dump(m, open(sys.argv[1], "w", encoding="utf-8", newline="\n"), indent=2)
PYEOF
    payload_verify_inventory "$dir" && { echo "foreign RID accepted"; failure=1; }
    cp "$WORK/manifest.orig.json" "$manifest"

    # An installed file's actual mode must equal its declared mode.
    if [ "$POSIX_MODE" -eq 1 ]; then
        chmod 0666 "$dir/config/defaults.json"
        payload_verify_inventory "$dir" && {
            echo "actual/declared mode drift not detected"; failure=1; }
        chmod 0644 "$dir/config/defaults.json"
    fi

    # A linked directory inside the installed tree must reject.
    mkdir -p "$WORK/link-target" && echo x > "$WORK/link-target/x"
    if [ "$CAN_SYMLINK" -eq 1 ]; then
        ln -s "$WORK/link-target" "$dir/linked-dir"
        payload_verify_inventory "$dir" && { echo "linked directory not detected"; failure=1; }
        rm -rf "$dir/linked-dir"
    fi

    payload_verify_inventory "$dir" || { echo "restored inventory should verify"; failure=1; }
    rm -rf "$tamper_root"
    return "$failure"
}

case_audit_equivalence() {
    [ -f "$EXPECTED/audit-result.json" ] || { echo "missing expected audit result"; return 1; }
    local fx="$WORK/fx-audit"
    materialize_fixture "$fx"
    local out="$WORK/audit-packed.json" stdout_file="$WORK/audit-packed.stdout"
    local rc=0
    (cd / && dotnet "$INSTALL_DIR/ContractScribe.Cli.dll" audit \
        --repository-root "$fx" --input Fixture.slnx --policy policy.json \
        --output "$out" > "$stdout_file") || rc=$?
    for oracle in audit-exit.txt audit-result.json audit-stdout.json; do
        [ -f "$EXPECTED/$oracle" ] || {
            echo "missing required oracle: $oracle"; return 1; }
    done
    if [ "$(cat "$EXPECTED/audit-exit.txt")" != "$rc" ]; then
        echo "audit exit $rc != expected $(cat "$EXPECTED/audit-exit.txt")"; return 1
    fi
    cmp "$EXPECTED/audit-result.json" "$out" || {
        echo "canonical audit output differs from source oracle"; return 1; }
    cmp "$EXPECTED/audit-stdout.json" "$stdout_file" || {
        echo "audit stdout differs from source oracle"; return 1; }
}

case_offline_audit() {
    [ -n "$NETNS_MODE" ] || { echo "no usable network namespace mechanism"; return 1; }
    local fx="$WORK/fx-offline"
    materialize_fixture "$fx"
    local out="$WORK/audit-offline.json"
    cat > "$WORK/offline-audit-inner.sh" <<'INNER'
#!/usr/bin/env bash
set -u
# Control: outbound connectivity must fail inside this namespace.
if python3 -c 'import socket,sys; s=socket.create_connection(("1.1.1.1",443),3); sys.exit(0)' 2>/dev/null; then
    exit 90
fi
exec dotnet "$1" audit \
    --repository-root "$2" --input Fixture.slnx --policy policy.json \
    --output "$3" >/dev/null 2>&1
INNER
    local rc=0
    netns_exec 0 bash "$WORK/offline-audit-inner.sh" \
        "$INSTALL_DIR/ContractScribe.Cli.dll" "$fx" "$out" || rc=$?
    if [ "$rc" -eq 90 ]; then
        echo "network namespace is NOT isolated"; return 1
    fi
    for oracle in audit-exit.txt audit-result.json; do
        [ -f "$EXPECTED/$oracle" ] || {
            echo "missing required oracle: $oracle"; return 1; }
    done
    [ "$(cat "$EXPECTED/audit-exit.txt")" != "$rc" ] && {
        echo "offline audit exit $rc"; return 1; }
    cmp "$EXPECTED/audit-result.json" "$out" || {
        echo "offline audit bytes differ"; return 1; }
}

case_packed_tests() {
    [ -f "$TESTBIN/ContractScribe.IntegrationTests.dll" ] || {
        echo "missing kit test assembly at $TESTBIN"; return 1; }
    [ -f "$HOOK" ] || { echo "missing startup hook at $HOOK"; return 1; }
    local results="$WORK/TestResults"
    mkdir -p "$results"
    local rc=0
    if [ -n "$NETNS_MODE" ]; then
        # Loopback up inside the same namespace; control proves external
        # connectivity absent; fake services and the packed client share it.
        cat > "$WORK/packed-tests-inner.sh" <<'INNER'
#!/usr/bin/env bash
set -u
if python3 -c 'import socket,sys; s=socket.create_connection(("1.1.1.1",443),3); sys.exit(0)' 2>/dev/null; then
    exit 90
fi
cd "$6"
export CONTRACTSCRIBE_PACKAGED_MODE=verify
export CONTRACTSCRIBE_PACKAGED_CLI="$1"
export CONTRACTSCRIBE_PACKAGED_CLI_IDENTITY="$2"
export CONTRACTSCRIBE_PACKAGING_EXPECTED="$3"
export CONTRACTSCRIBE_PACKAGING_FIXTURE="$4"
export CONTRACTSCRIBE_STARTUP_HOOK_PATH="$5"
exec dotnet vstest "$7" \
    --TestCaseFilter:FullyQualifiedName~PackagedCli \
    --logger:"trx;LogFileName=packed.trx" \
    --logger:"console;verbosity=normal"
INNER
        netns_exec 1 bash "$WORK/packed-tests-inner.sh" \
            "$INSTALL_DIR/ContractScribe.Cli.dll" "$TOOL_VERSION" "$EXPECTED" \
            "$FIXTURE" "$HOOK" "$WORK" "$TESTBIN/ContractScribe.IntegrationTests.dll" || rc=$?
        [ "$rc" -ne 90 ] || { echo "network namespace is NOT isolated"; return 1; }
    else
        (cd "$WORK" && env \
            CONTRACTSCRIBE_PACKAGED_MODE=verify \
            CONTRACTSCRIBE_PACKAGED_CLI="$INSTALL_DIR/ContractScribe.Cli.dll" \
            CONTRACTSCRIBE_PACKAGED_CLI_IDENTITY="$TOOL_VERSION" \
            CONTRACTSCRIBE_PACKAGING_EXPECTED="$EXPECTED" \
            CONTRACTSCRIBE_PACKAGING_FIXTURE="$FIXTURE" \
            CONTRACTSCRIBE_STARTUP_HOOK_PATH="$HOOK" \
            dotnet vstest "$TESTBIN/ContractScribe.IntegrationTests.dll" \
                --TestCaseFilter:FullyQualifiedName~PackagedCli \
                --logger:"trx;LogFileName=packed.trx" \
                --logger:"console;verbosity=normal") || rc=$?
    fi
    [ "$rc" -eq 0 ] || { echo "vstest exited $rc"; return 1; }
    python3 - "$results" <<'PYEOF'
import sys, xml.etree.ElementTree as ET
required = {
    "PackedCliProcessTests.PackedCampaign_StartCompleteAndResumeLifecycle",
    "PackedCliProcessTests.PackedCampaign_NoRequiredWorkIsNoWork",
    "PackedCliProcessTests.PackedLayered_ResolutionAndBoundaries",
    "PackedCliProcessTests.PackedIdentity_VersionDoctorFromArbitraryCwd",
    "PackagedCliGitHubProcessTests.PackedGitHub_StartPublishAndReplay",
    "PackagedCliGitHubProcessTests.PackedGitHub_MissingTokenStopsAtCredentialBoundary",
}
seen = {}
import glob
for trx in glob.glob(sys.argv[1] + "/**/*.trx", recursive=True):
    tree = ET.parse(trx)
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    for result in tree.iter("{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult"):
        name = result.get("testName", "")
        outcome = result.get("outcome", "")
        for req in required:
            if req.split(".")[-1] in name:
                seen[req] = outcome
missing = required - set(seen)
if missing:
    print(f"required packed tests not executed: {sorted(missing)}", file=sys.stderr)
    sys.exit(1)
bad = {k: v for k, v in seen.items() if v != "Passed"}
if bad:
    print(f"required packed tests not passed: {bad}", file=sys.stderr)
    sys.exit(1)
print(f"packed tests executed and passed: {len(seen)}")
PYEOF
}

make_variant_b() {
    # Repack the same payload under a second identity for update/rollback.
    # B is a STORAGE/SELECTOR fixture only: its manifest identity is
    # internally consistent (so the stricter inventory accepts it), but its
    # binaries still carry A's embedded version — it is never used as
    # version-binding evidence.
    local staging="$1" archive="$2"
    mkdir -p "$staging/content"
    python3 "$PAYLOAD_EXTRACT_PY" extract "$ARCHIVE" "$staging/content" >/dev/null
    local top
    top="$(basename "$INSTALL_DIR")"
    local btop="contract-scribe-0.1.0-testB+${TOOL_VERSION##*+}-linux-x64"
    mv "$staging/content/$top" "$staging/$btop"
    python3 - "$staging/$btop/payload.json" "$btop" <<'PYEOF'
import json, sys
path, btop = sys.argv[1], sys.argv[2]
m = json.load(open(path))
m["toolVersion"] = btop[len("contract-scribe-"):-len("-linux-x64")]
with open(path, "w", encoding="utf-8", newline="\n") as fh:
    json.dump(m, fh, indent=2, ensure_ascii=False)
    fh.write("\n")
PYEOF
    tar -C "$staging" --sort=name --mtime="@0" --owner=0 --group=0 --numeric-owner \
        -cf - "$btop" | gzip -n > "$archive"
}

case_pin_update_rollback() {
    local root="$WORK/update-root"
    local staging="$WORK/variant-b"
    local b_archive="$WORK/variant-b.tar.gz"
    local top_a
    top_a="$(basename "$INSTALL_DIR")"

    # Version pinning: a mismatched expected sha rejects before any install.
    expect_install_reject digest "$ARCHIVE" \
        "1111111111111111111111111111111111111111111111111111111111111111" "$root"

    payload_install "$ARCHIVE" "$EXPECTED_SHA" "$root" >/dev/null || {
        echo "install A failed"; return 1; }
    payload_select "$root" "$top_a"
    [ "$(readlink "$root/current")" = "$top_a" ] || return 1

    make_variant_b "$staging" "$b_archive"
    local b_sha btop
    b_sha="$(sha256sum "$b_archive" | cut -d' ' -f1)"
    btop="contract-scribe-0.1.0-testB+${TOOL_VERSION##*+}-linux-x64"

    # Failed update: tampered B is rejected; the selector stays on A.
    local bad_b="$WORK/variant-b-bad.tar.gz"
    cp "$b_archive" "$bad_b"
    printf '\xff' | dd of="$bad_b" bs=1 seek=64 conv=notrunc status=none
    expect_install_reject digest "$bad_b" "$b_sha" "$root" || return 1
    [ "$(readlink "$root/current")" = "$top_a" ] || {
        echo "selector moved after failed update"; return 1; }
    dotnet "$root/$top_a/ContractScribe.Cli.dll" --version >"$WORK/a-after-bad.txt" 2>&1 || {
        echo "A unusable after failed update"; return 1; }
    grep -q ContractScribe "$WORK/a-after-bad.txt" || {
        echo "A unusable after failed update"; return 1; }

    # Update: B installs side-by-side; selector moves to B.
    payload_install "$b_archive" "$b_sha" "$root" >/dev/null || {
        echo "install B failed"; return 1; }
    [ -d "$root/$btop" ] && [ -d "$root/$top_a" ] || {
        echo "side-by-side install missing"; return 1; }
    payload_select "$root" "$btop"
    [ "$(readlink "$root/current")" = "$btop" ] || return 1
    dotnet "$root/current/ContractScribe.Cli.dll" --version >"$WORK/b-version.txt" 2>&1 || {
        echo "current/B unusable"; return 1; }
    grep -q ContractScribe "$WORK/b-version.txt" || {
        echo "current/B unusable"; return 1; }

    # Rollback: repoint the selector to A; A's files were never merged.
    payload_select "$root" "$top_a"
    [ "$(readlink "$root/current")" = "$top_a" ] || return 1
    dotnet "$root/current/ContractScribe.Cli.dll" --version \
        | grep -q "ContractScribe $TOOL_VERSION" || {
            echo "rollback A identity wrong"; return 1; }
    cmp "$root/$top_a/payload.json" "$INSTALL_DIR/payload.json" || {
        echo "A content mutated during update/rollback"; return 1; }

    # Caller-owned state survives; owned removal is confined.
    mkdir -p "$root/sentinel-state"
    echo caller-owned > "$root/sentinel-state/state.txt"
    payload_remove "$root" "$btop" || { echo "remove B failed"; return 1; }
    [ ! -e "$root/$btop" ] || { echo "B not removed"; return 1; }
    [ -d "$root/$top_a" ] || { echo "A removed with B"; return 1; }
    payload_uninstall "$root"
    [ -f "$root/sentinel-state/state.txt" ] || {
        echo "caller-owned sentinel was deleted"; return 1; }
    [ -d "$root" ] || { echo "root removed despite sentinel"; return 1; }
    [ ! -e "$root/$top_a" ] && [ ! -L "$root/current" ] || {
        echo "owned content not fully removed"; return 1; }
}

case_uninstall_sentinel() {
    local root="$WORK/uninstall-root"
    mkdir -p "$root"
    echo sentinel > "$root/keep.txt"
    payload_install "$ARCHIVE" "$EXPECTED_SHA" "$root" >/dev/null || return 1
    payload_select "$root" "$(basename "$INSTALL_DIR")"
    payload_uninstall "$root"
    [ -f "$root/keep.txt" ] || { echo "sentinel deleted"; return 1; }
    [ -d "$root" ] || { echo "non-empty root removed"; return 1; }
    local empty="$WORK/uninstall-empty"
    payload_install "$ARCHIVE" "$EXPECTED_SHA" "$empty" >/dev/null || return 1
    payload_uninstall "$empty"
    [ ! -e "$empty" ] || { echo "empty root not removed"; return 1; }
}

case_cleanup_confinement() {
    local root="$WORK/cleanup-root"
    local victim="$WORK/victim"
    mkdir -p "$victim"
    echo precious > "$victim/payload.json"
    payload_install "$ARCHIVE" "$EXPECTED_SHA" "$root" >/dev/null || {
        echo "baseline install failed"; return 1; }
    local top
    top="$(basename "$(echo "$root"/contract-scribe-*/)")"

    # A name containing traversal must reject before any removal; the
    # sibling victim survives.
    payload_remove "$root" "contract-scribe-a/../../victim" && {
        echo "traversal version name accepted"; return 1; }
    [ -f "$victim/payload.json" ] || { echo "traversal deleted the victim"; return 1; }
    [ -d "$root/$top" ] || { echo "traversal removed the real install"; return 1; }

    # A linked contract-scribe-* entry is not an owned directory — uninstall
    # must leave the link and its outside target untouched.
    local outside="$WORK/outside-target"
    mkdir -p "$outside"
    echo keep > "$outside/keep.txt"
    if [ "$CAN_SYMLINK" -eq 1 ]; then
        ln -s "$outside" "$root/contract-scribe-link"
    fi
    payload_uninstall "$root"
    [ -f "$outside/keep.txt" ] || { echo "linked outside content deleted"; return 1; }
    if [ "$CAN_SYMLINK" -eq 1 ]; then
        [ -L "$root/contract-scribe-link" ] || { echo "foreign link removed"; return 1; }
        rm "$root/contract-scribe-link"
    fi

    # A caller-owned regular file at `current` must never be overwritten by
    # the selector.
    payload_install "$ARCHIVE" "$EXPECTED_SHA" "$root" >/dev/null || {
        echo "reinstall failed"; return 1; }
    top="$(basename "$(echo "$root"/contract-scribe-*/)")"
    echo caller-file > "$root/current"
    payload_select "$root" "$top" && {
        echo "foreign current was overwritten"; return 1; }
    [ "$(cat "$root/current")" = "caller-file" ] || {
        echo "caller-owned current was destroyed"; return 1; }
    rm "$root/current"
    payload_uninstall "$root"
    rm -rf "$root" "$victim" "$outside"
}

case_publish_failure() {
    # Occupied destination → checked publish rejection with no staging
    # residue and no damage to the existing installation.
    local root="$WORK/pub-root"
    payload_install "$ARCHIVE" "$EXPECTED_SHA" "$root" >/dev/null || {
        echo "baseline install failed"; return 1; }
    local top
    top="$(basename "$(echo "$root"/contract-scribe-*/)")"
    payload_install "$ARCHIVE" "$EXPECTED_SHA" "$root" >"$WORK/pub2.txt" 2>&1 && {
        echo "install into occupied destination succeeded"; return 1; }
    grep -q "stage=publish result=fail" "$WORK/pub2.txt" || {
        cat "$WORK/pub2.txt"; return 1; }
    if ls -d "$root"/.install-staging.* >/dev/null 2>&1; then
        echo "staging residue left after failed publish"; return 1
    fi
    [ -d "$root/$top" ] || { echo "existing installation damaged"; return 1; }
    dotnet "$root/$top/ContractScribe.Cli.dll" --version >"$WORK/pub-existing.txt" 2>&1 || {
        echo "existing installation broken by failed publish"; return 1; }
    grep -q ContractScribe "$WORK/pub-existing.txt" || {
        echo "existing installation broken by failed publish"; return 1; }

    # An invalid (regular-file) installation root rejects at acquire and the
    # file survives.
    echo file > "$WORK/pub-file"
    payload_install "$ARCHIVE" "$EXPECTED_SHA" "$WORK/pub-file" >"$WORK/pub3.txt" 2>&1 && {
        echo "install into file root succeeded"; return 1; }
    grep -q "stage=acquire result=fail" "$WORK/pub3.txt" || {
        cat "$WORK/pub3.txt"; return 1; }
    [ "$(cat "$WORK/pub-file")" = "file" ] || { echo "file root destroyed"; return 1; }
    rm -rf "$root"
}

case_publish_appearing() {
    # A destination appearing inside the publish window must reject — never
    # nest, never clobber the appearing content.
    local root="$WORK/appear-root" top pid marker="$WORK/appear-ready"
    top="contract-scribe-$(manifest_get "['toolVersion']")-$(manifest_get "['runtimeIdentifier']")"
    (
        PAYLOAD_TEST_STALL=3 PAYLOAD_TEST_PUBLISH_READY="$marker"
        payload_install "$ARCHIVE" "$EXPECTED_SHA" "$root" >/dev/null
    ) &
    pid=$!
    local i
    # The publish-ready marker fires after the destination check and inside
    # the pre-mv stall — the appearing destination lands exactly in the
    # checked-but-not-yet-moved window regardless of runner speed.
    for i in $(seq 1 600); do
        [ -f "$marker" ] && break
        sleep 0.1
    done
    [ -f "$marker" ] || {
        kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null
        echo "publish window never reached"; return 1; }
    mkdir -p "$root/$top" && echo concurrent > "$root/$top/occupant"
    wait "$pid" && { echo "install into appearing destination succeeded"; return 1; }
    [ -f "$root/$top/occupant" ] || { echo "appearing destination destroyed"; return 1; }
    [ ! -d "$root/$top/$top" ] || { echo "nested publication occurred"; return 1; }
    compgen -G "$root/.install-staging.*" >/dev/null && {
        echo "staging residue after publish-window rejection"; return 1; }

    # An EMPTY appearing destination must also reject: expected-absence is
    # violated regardless of whether the appearing directory has content.
    rm -rf "$root" "$marker"
    (
        PAYLOAD_TEST_STALL=3 PAYLOAD_TEST_PUBLISH_READY="$marker"
        payload_install "$ARCHIVE" "$EXPECTED_SHA" "$root" >/dev/null
    ) &
    pid=$!
    for i in $(seq 1 600); do
        [ -f "$marker" ] && break
        sleep 0.1
    done
    [ -f "$marker" ] || {
        kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null
        echo "publish window never reached (empty case)"; return 1; }
    mkdir -p "$root/$top"
    wait "$pid" && { echo "install into empty appearing destination succeeded"; return 1; }
    [ -d "$root/$top" ] || { echo "appearing empty directory destroyed"; return 1; }
    [ -z "$(ls -A "$root/$top")" ] || {
        echo "appearing empty directory gained payload content"; return 1; }
    compgen -G "$root/.install-staging.*" >/dev/null && {
        echo "staging residue after empty-destination rejection"; return 1; }

    # The publication primitive itself: mv -T -n never replaces a live dir.
    mkdir -p "$WORK/mv-src" "$WORK/mv-dest"
    echo x > "$WORK/mv-dest/x"
    mv -T -n "$WORK/mv-src" "$WORK/mv-dest" 2>/dev/null
    [ -d "$WORK/mv-src" ] || { echo "mv -n replaced a non-empty destination"; return 1; }
    [ -f "$WORK/mv-dest/x" ] || { echo "mv -n clobbered destination"; return 1; }
    mv -T -n "$WORK/mv-src" "$WORK/mv-new" 2>/dev/null
    [ -d "$WORK/mv-src" ] && { echo "mv -n declined an absent destination"; return 1; }
    [ -d "$WORK/mv-new" ] || { echo "mv -n lost the moved directory"; return 1; }
    rm -rf "$root" "$WORK/mv-src" "$WORK/mv-dest" "$WORK/mv-new"
}

case_install_sigterm() {
    # Controlled interruption reclaims owned staging; SIGKILL is out of
    # contract scope by definition.
    local root="$WORK/term-root" pid i
    (
        PAYLOAD_TEST_STALL=5
        payload_install "$ARCHIVE" "$EXPECTED_SHA" "$root" >/dev/null
    ) &
    pid=$!
    for i in $(seq 1 100); do
        compgen -G "$root/.install-staging.*" >/dev/null && break
        sleep 0.05
    done
    compgen -G "$root/.install-staging.*" >/dev/null || {
        kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null
        echo "staging never appeared"; return 1; }
    kill -TERM "$pid"
    wait "$pid" && { echo "install returned success after SIGTERM"; return 1; }
    compgen -G "$root/.install-staging.*" >/dev/null && {
        echo "staging left after SIGTERM"; return 1; }
    rm -rf "$root"
}

case_work_parent_file() {
    # --work naming an existing regular file must reject; the invocation
    # directory and every caller-owned entry must survive.
    local d="$WORK/work-parent"
    mkdir -p "$d"
    echo sentinel > "$d/keep.txt"
    echo occupied > "$d/parentfile"
    (
        cd "$d" && bash "$SCRIPT_SELF"             --archive "$ARCHIVE" --sha256-file "$SHA_FILE"             ${REPO:+--repo "$REPO"} ${KIT:+--kit "$KIT"}             ${EXPECTED:+--expected "$EXPECTED"}             --work "$d/parentfile" --only "definitely-not-a-case"             >"$WORK/work-parent-run.txt" 2>&1
    ) && { echo "file work-parent accepted"; return 1; }
    [ -f "$d/keep.txt" ] || { echo "invocation dir content deleted"; return 1; }
    [ -f "$d/parentfile" ] || { echo "work parent file deleted"; return 1; }
    rm -rf "$d"
}

# ---------- run matrix ----------

echo "verify-payload: archive=$ARCHIVE"
echo "verify-payload: expected sha=$EXPECTED_SHA"
echo "verify-payload: fixture=$FIXTURE expected=$EXPECTED"
echo "verify-payload: netns=${NETNS_MODE:-unavailable}"

run_case toolchain case_toolchain

if ! case_install_happy >"$LOGDIR/install-happy.log" 2>&1; then
    CASES_RUN=$((CASES_RUN + 1)); CASES_FAILED=$((CASES_FAILED + 1))
    FAILED_CASES+=("install-happy")
    echo "FAIL install-happy (log: $LOGDIR/install-happy.log)"
    echo "verify-payload: no installation — install-dependent cases skipped"
    echo "verify-payload: $CASES_RUN cases, $CASES_FAILED failed"
    exit 1
fi
CASES_RUN=$((CASES_RUN + 1))
echo "PASS install-happy"

run_case acquire-missing-artifact case_acquire_missing_artifact
run_case acquire-wrong-sha case_acquire_wrong_sha
run_case acquire-truncated case_acquire_truncated_archive
run_case acquire-corrupt case_acquire_corrupt_archive
run_case hazard-traversal case_hazard_traversal
run_case hazard-absolute case_hazard_absolute
run_case hazard-dotdot case_hazard_dotdot
run_case hazard-symlink case_hazard_symlink
run_case hazard-hardlink case_hazard_hardlink
run_case hazard-device case_hazard_device
run_case hazard-fifo case_hazard_fifo
run_case hazard-sparse case_hazard_sparse
run_case hazard-paxsparse case_hazard_paxsparse
run_case hazard-duplicate case_hazard_duplicate
run_case hazard-collision case_hazard_collision
run_case hazard-secondtop case_hazard_secondtop
run_case hazard-paxbomb case_hazard_paxbomb
run_case hazard-longnamebomb case_hazard_longnamebomb
run_case selector-pinning case_selector_pinning
run_case identity-version case_identity_version
run_case identity-help case_identity_help
run_case identity-invalid-usage case_identity_invalid_usage
run_case location-doctor case_location_doctor
run_case defaults-exact-bytes case_defaults_exact_bytes
run_case prereq-no-host case_prereq_no_host
run_case prereq-no-runtime case_prereq_no_runtime
run_case prereq-no-sdk case_prereq_no_sdk
run_case inventory-negatives case_inventory_negative
run_case audit-equivalence case_audit_equivalence
run_case offline-audit case_offline_audit
run_case packed-tests case_packed_tests
run_case pin-update-rollback case_pin_update_rollback
run_case uninstall-sentinel case_uninstall_sentinel
run_case cleanup-confinement case_cleanup_confinement
run_case publish-failure case_publish_failure
run_case publish-appearing case_publish_appearing
run_case install-sigterm case_install_sigterm
run_case work-parent-file case_work_parent_file

echo "verify-payload: $CASES_RUN cases, $CASES_FAILED failed"
if [ "$CASES_FAILED" -gt 0 ]; then
    echo "failed: ${FAILED_CASES[*]}"
    exit 1
fi
