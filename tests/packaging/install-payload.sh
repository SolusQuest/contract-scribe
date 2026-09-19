# install-payload.sh — reference implementation of the frozen D2 install
# contract (issue #18 / M6-A1). Sourced by verify-payload.sh; not executed
# directly. The M6-A2 Action wrapper reimplements these semantics for real
# acquisition (GitHub Release lookup/auth/redirects are its responsibility).
#
# Stages, in order — every stage emits a marker line:
#   payload-install stage=<name> result=ok|fail reason=<short>
# Stage failure means the product entrypoint was never executed.
#
#   acquire   archive copied into an owned private file; compressed bound
#   digest    expected SHA-256 verified against the independent sidecar input
#   scan      member policy via payload_extract.py (names, types, sparse/PAX,
#             bounds) — rejects traversal/links/devices/dupes/collisions
#   extract   regular files/dirs only into a fresh private staging dir
#   inventory installed tree equals payload.json inventory exactly
#             (set equality + per-file size/sha256; required members present)
#   prereq    dotnet host on PATH consistent with invocation host;
#             Microsoft.NETCore.App 10.x present. SDK presence is recorded
#             separately — required for semantic commands, not for install.
#   publish   atomic same-filesystem rename into
#             <root>/contract-scribe-<ver>-linux-x64/ (must not exist)
#
# Selector: <root>/current is an installer-owned symlink repointed
# atomically for pinning/update/rollback. Cleanup removes owned content
# only; uninstall rmdir's the root only when empty.

PAYLOAD_EXTRACT_PY="${PAYLOAD_EXTRACT_PY:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/payload_extract.py}"

_payload_log() {
    echo "payload-install stage=$1 result=$2 reason=$3"
}

# payload_install <archive> <expected-sha256> <install-root>
# Sets PAYLOAD_INSTALL_DIR on success; returns non-zero on rejection.
payload_install() {
    local archive="$1" expected_sha="$2" root="$3"
    PAYLOAD_INSTALL_DIR=""

    if [ ! -f "$archive" ]; then
        _payload_log acquire fail missing-artifact; return 1
    fi
    local staging
    staging="$(mktemp -d "${TMPDIR:-/tmp}/contract-scribe-install.XXXXXX")"
    local private_archive="$staging/archive.tar.gz"
    cp -- "$archive" "$private_archive"
    local compressed
    compressed="$(stat -c %s "$private_archive")"
    if [ "$compressed" -gt $((256 * 1024 * 1024)) ]; then
        rm -rf "$staging"; _payload_log acquire fail compressed-bound; return 1
    fi
    _payload_log acquire ok -

    local actual_sha
    actual_sha="$(sha256sum "$private_archive" | cut -d' ' -f1)"
    if [ "$actual_sha" != "$expected_sha" ]; then
        rm -rf "$staging"; _payload_log digest fail sha256-mismatch; return 1
    fi
    _payload_log digest ok -

    local scan_report
    if ! scan_report="$(python3 "$PAYLOAD_EXTRACT_PY" scan "$private_archive")"; then
        rm -rf "$staging"; _payload_log scan fail member-policy; return 1
    fi
    local top
    top="$(printf '%s' "$scan_report" | python3 -c 'import json,sys; print(json.load(sys.stdin)["top"] or "")')"
    if [ -z "$top" ]; then
        rm -rf "$staging"; _payload_log scan fail no-top-directory; return 1
    fi
    _payload_log scan ok -

    local extract_dir="$staging/extracted"
    mkdir -p "$extract_dir"
    if ! python3 "$PAYLOAD_EXTRACT_PY" extract "$private_archive" "$extract_dir" --top "$top"; then
        rm -rf "$staging"; _payload_log extract fail member-policy; return 1
    fi
    _payload_log extract ok -

    if ! _payload_verify_inventory "$extract_dir/$top"; then
        rm -rf "$staging"; _payload_log inventory fail mismatch; return 1
    fi
    _payload_log inventory ok -

    if ! _payload_prereq_runtime; then
        rm -rf "$staging"; _payload_log prereq fail missing-runtime; return 1
    fi
    _payload_log prereq ok -

    mkdir -p "$root"
    local dest="$root/$top"
    if [ -e "$dest" ] || [ -L "$dest" ]; then
        rm -rf "$staging"; _payload_log publish fail destination-exists; return 1
    fi
    mv "$extract_dir/$top" "$dest"
    rm -rf "$staging"
    _payload_log publish ok -
    PAYLOAD_INSTALL_DIR="$dest"
    return 0
}

# _payload_verify_inventory <installed-top-dir>
_payload_verify_inventory() {
    local dir="$1"
    [ -f "$dir/payload.json" ] || return 1
    [ -f "$dir/ContractScribe.Cli.dll" ] || return 1
    [ -f "$dir/ContractScribe.Cli.deps.json" ] || return 1
    [ -f "$dir/ContractScribe.Cli.runtimeconfig.json" ] || return 1
    [ -f "$dir/config/defaults.json" ] || return 1
    python3 - "$dir" <<'PYEOF'
import hashlib, json, os, sys

root = sys.argv[1]
with open(os.path.join(root, "payload.json"), encoding="utf-8") as fh:
    manifest = json.load(fh)
if manifest.get("payloadFormat") != 1:
    sys.exit(1)
expected = {e["path"]: e for e in manifest.get("files", [])}
actual = set()
for dirpath, dirnames, filenames in os.walk(root):
    for name in filenames:
        full = os.path.join(dirpath, name)
        rel = os.path.relpath(full, root).replace(os.sep, "/")
        if rel == "payload.json":
            continue
        if not os.path.isfile(full) or os.path.islink(full):
            sys.exit(1)
        actual.add(rel)
if actual != set(expected):
    sys.exit(1)
for rel, entry in expected.items():
    full = os.path.join(root, *rel.split("/"))
    if os.path.getsize(full) != entry["length"]:
        sys.exit(1)
    h = hashlib.sha256()
    with open(full, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    if h.hexdigest() != entry["sha256"]:
        sys.exit(1)
sys.exit(0)
PYEOF
}

# payload_verify_inventory <installed-top-dir> — public re-check of the
# installed tree against its payload.json inventory.
payload_verify_inventory() {
    _payload_verify_inventory "$1"
}

# _payload_prereq_runtime — dotnet host on PATH + Microsoft.NETCore.App 10.x.
_payload_prereq_runtime() {
    command -v dotnet >/dev/null 2>&1 || return 1
    dotnet --list-runtimes 2>/dev/null | grep -q '^Microsoft\.NETCore\.App 10\.' || return 1
    return 0
}

# payload_sdk_present — separate SDK observation (not an install gate).
payload_sdk_present() {
    command -v dotnet >/dev/null 2>&1 && \
        dotnet --list-sdks 2>/dev/null | grep -q .
}

# payload_select <install-root> <version-dir-name>
# Atomically repoint the installer-owned `current` selector.
payload_select() {
    local root="$1" version="$2"
    [ -d "$root/$version" ] || return 1
    local tmp="$root/.current.tmp.$$"
    ln -sfn "$version" "$tmp"
    mv -T "$tmp" "$root/current"
}

# payload_remove <install-root> <version-dir-name>
# Remove one owned versioned installation directory.
payload_remove() {
    local root="$1" version="$2"
    case "$version" in
        contract-scribe-*) ;;
        *) return 1 ;;
    esac
    [ -d "$root/$version" ] || return 1
    [ -f "$root/$version/payload.json" ] || return 1
    if [ -L "$root/current" ] && [ "$(readlink "$root/current")" = "$version" ]; then
        rm -f "$root/current"
    fi
    rm -rf "$root/$version"
}

# payload_uninstall <install-root>
# Remove owned versioned dirs + owned selector; rmdir root only if empty.
# Caller-owned content (state, sentinels, unrelated files) survives.
payload_uninstall() {
    local root="$1"
    [ -d "$root" ] || return 0
    local dir
    for dir in "$root"/contract-scribe-*/; do
        [ -d "$dir" ] || continue
        [ -f "$dir/payload.json" ] || continue
        rm -rf "$dir"
    done
    [ -L "$root/current" ] && rm -f "$root/current"
    rmdir "$root" 2>/dev/null || true
}
