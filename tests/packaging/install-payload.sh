# install-payload.sh — reference implementation of the frozen D2 install
# contract (issue #18 / M6-A1). Sourced by verify-payload.sh; not executed
# directly. The M6-A2 Action wrapper reimplements these semantics for real
# acquisition (GitHub Release lookup/auth/redirects are its responsibility).
#
# Stages, in order — every stage emits a marker line:
#   payload-install stage=<name> result=ok|fail reason=<short>
# Stage failure means the product entrypoint was never executed.
#
#   acquire   archive copied into an owned private file under the
#             installation root's filesystem; compressed bound enforced
#             before and during the copy
#   digest    expected SHA-256 verified against the independent sidecar input
#   scan      member policy via payload_extract.py (names, types, sparse/PAX,
#             bounds) — rejects traversal/links/devices/dupes/collisions
#   extract   regular files/dirs only into a fresh private staging dir
#   inventory installed tree equals payload.json inventory exactly
#             (set equality + per-file size/sha256; required members present;
#             manifest identity consistent with itself and the top dir)
#   prereq    dotnet host on PATH consistent with invocation host;
#             Microsoft.NETCore.App 10.x present. SDK presence is recorded
#             separately — required for semantic commands, not for install.
#   publish   checked same-filesystem rename into
#             <root>/contract-scribe-<ver>-linux-x64/ (must not exist)
#
# Selector: <root>/current is an installer-owned symlink repointed
# atomically for pinning/update/rollback; a foreign non-symlink entry at
# that name is never overwritten. Cleanup removes owned content only —
# single validated basenames, no linked entries — and uninstall rmdir's the
# root only when empty.
#
# On any rejection the owned staging directory is removed; an installation
# root created by this call is removed only when it is still empty.

PAYLOAD_EXTRACT_PY="${PAYLOAD_EXTRACT_PY:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/payload_extract.py}"

_payload_log() {
    echo "payload-install stage=$1 result=$2 reason=$3"
}

# _payload_cleanup <staging> <root> <created-root:0|1>
_payload_cleanup() {
    [ -n "$1" ] && rm -rf "$1"
    [ "$3" = 1 ] && rmdir "$2" 2>/dev/null
    return 0
}

# payload_install <archive> <expected-sha256> <install-root>
# Sets PAYLOAD_INSTALL_DIR on success; returns non-zero on rejection.
payload_install() {
    local archive="$1" expected_sha="$2" root="$3"
    PAYLOAD_INSTALL_DIR=""

    if [ ! -f "$archive" ]; then
        _payload_log acquire fail missing-artifact; return 1
    fi
    local source_size
    source_size="$(stat -c %s "$archive" 2>/dev/null)" || {
        _payload_log acquire fail stat-failed; return 1; }
    if [ "$source_size" -gt $((256 * 1024 * 1024)) ]; then
        _payload_log acquire fail compressed-bound; return 1
    fi

    # Stage inside the installation root so publication is a checked
    # same-filesystem rename, never a copy across filesystems.
    local created_root=0
    if [ ! -d "$root" ]; then
        if ! mkdir -p "$root" 2>/dev/null; then
            _payload_log acquire fail root-unwritable; return 1
        fi
        created_root=1
    fi
    local staging
    staging="$(mktemp -d "$root/.install-staging.XXXXXX")" || {
        _payload_cleanup "" "$root" "$created_root"
        _payload_log acquire fail staging-failed; return 1; }
    local private_archive="$staging/archive.tar.gz"
    # Cap while copying — the source may grow after the stat above.
    if ! head -c $((256 * 1024 * 1024 + 1)) -- "$archive" > "$private_archive"; then
        _payload_cleanup "$staging" "$root" "$created_root"
        _payload_log acquire fail copy-failed; return 1
    fi
    if [ "$(stat -c %s "$private_archive")" -gt $((256 * 1024 * 1024)) ]; then
        _payload_cleanup "$staging" "$root" "$created_root"
        _payload_log acquire fail compressed-bound; return 1
    fi
    _payload_log acquire ok -

    local actual_sha
    actual_sha="$(sha256sum "$private_archive" | cut -d' ' -f1)"
    if [ "$actual_sha" != "$expected_sha" ]; then
        _payload_cleanup "$staging" "$root" "$created_root"
        _payload_log digest fail sha256-mismatch; return 1
    fi
    _payload_log digest ok -

    local scan_report
    if ! scan_report="$(python3 "$PAYLOAD_EXTRACT_PY" scan "$private_archive")"; then
        _payload_cleanup "$staging" "$root" "$created_root"
        _payload_log scan fail member-policy; return 1
    fi
    local top
    top="$(printf '%s' "$scan_report" | python3 -c 'import json,sys; print(json.load(sys.stdin)["top"] or "")')"
    if [ -z "$top" ]; then
        _payload_cleanup "$staging" "$root" "$created_root"
        _payload_log scan fail no-top-directory; return 1
    fi
    _payload_log scan ok -

    local extract_dir="$staging/extracted"
    mkdir -p "$extract_dir"
    if ! python3 "$PAYLOAD_EXTRACT_PY" extract "$private_archive" "$extract_dir" --top "$top"; then
        _payload_cleanup "$staging" "$root" "$created_root"
        _payload_log extract fail member-policy; return 1
    fi
    _payload_log extract ok -

    if ! _payload_verify_inventory "$extract_dir/$top"; then
        _payload_cleanup "$staging" "$root" "$created_root"
        _payload_log inventory fail mismatch; return 1
    fi
    _payload_log inventory ok -

    if ! _payload_prereq_runtime; then
        _payload_cleanup "$staging" "$root" "$created_root"
        _payload_log prereq fail missing-runtime; return 1
    fi
    _payload_log prereq ok -

    local dest="$root/$top"
    if [ -e "$dest" ] || [ -L "$dest" ]; then
        _payload_cleanup "$staging" "$root" "$created_root"
        _payload_log publish fail destination-exists; return 1
    fi
    if ! mv "$extract_dir/$top" "$dest"; then
        _payload_cleanup "$staging" "$root" "$created_root"
        _payload_log publish fail rename-failed; return 1
    fi
    rm -rf "$staging"
    _payload_log publish ok -
    PAYLOAD_INSTALL_DIR="$dest"
    return 0
}

# _payload_verify_inventory <installed-top-dir>
# Validate the manifest identity contract and the installed tree:
# required members, payloadFormat, identity fields, toolVersion ↔
# sourceRevision agreement, top-directory name agreement, defaults digest,
# unique declared paths, permitted modes, exact file-set equality,
# per-file length/sha256, and no links anywhere in the tree.
_payload_verify_inventory() {
    local dir="$1"
    [ -f "$dir/payload.json" ] || return 1
    [ -f "$dir/ContractScribe.Cli.dll" ] || return 1
    [ -f "$dir/ContractScribe.Cli.deps.json" ] || return 1
    [ -f "$dir/ContractScribe.Cli.runtimeconfig.json" ] || return 1
    [ -f "$dir/config/defaults.json" ] || return 1
    python3 - "$dir" <<'PYEOF'
import hashlib, json, os, re, sys

root = sys.argv[1]
if os.name == "nt":
    root = "\\\\?\\" + os.path.abspath(root)
with open(os.path.join(root, "payload.json"), encoding="utf-8") as fh:
    manifest = json.load(fh)

def fail(reason):
    print(f"inventory: {reason}", file=sys.stderr)
    sys.exit(1)

if manifest.get("payloadFormat") != 1:
    fail("payloadFormat")
if manifest.get("tool") != "contract-scribe":
    fail("tool")
if manifest.get("distributionChannel") != "d2-framework-dependent-dll":
    fail("distributionChannel")
rid = manifest.get("runtimeIdentifier")
if not isinstance(rid, str) or not re.fullmatch(r"[a-z0-9]+-[a-z0-9]+", rid):
    fail("runtimeIdentifier")
if manifest.get("targetFramework") != "net10.0":
    fail("targetFramework")
if manifest.get("entrypoint") != "ContractScribe.Cli.dll":
    fail("entrypoint")
revision = manifest.get("sourceRevision")
if not isinstance(revision, str) or not re.fullmatch(r"[0-9a-f]{40}", revision):
    fail("sourceRevision")
version = manifest.get("toolVersion")
if not isinstance(version, str) or not version.endswith("+" + revision):
    fail("toolVersion")
expected_top = f"contract-scribe-{version}-{rid}"
if os.path.basename(root.rstrip("/")) != expected_top:
    fail("top-directory")

files = manifest.get("files")
if not isinstance(files, list):
    fail("files")
paths = [e.get("path") for e in files if isinstance(e, dict)]
if len(paths) != len(set(paths)):
    fail("duplicate-path")
for entry in files:
    if entry.get("mode") not in ("0644", "0755"):
        fail("mode")
    if not isinstance(entry.get("path"), str) or entry["path"].startswith("/"):
        fail("path")
expected = {e["path"]: e for e in files}

defaults_hash = hashlib.sha256(
    open(os.path.join(root, "config", "defaults.json"), "rb").read()).hexdigest()
if manifest.get("defaultsJsonSha256") != defaults_hash:
    fail("defaultsJsonSha256")

actual = set()
for dirpath, dirnames, filenames in os.walk(root):
    for name in dirnames:
        if os.path.islink(os.path.join(dirpath, name)):
            fail("linked-directory")
    for name in filenames:
        full = os.path.join(dirpath, name)
        rel = os.path.relpath(full, root).replace(os.sep, "/")
        if rel == "payload.json":
            continue
        if not os.path.isfile(full) or os.path.islink(full):
            fail("linked-or-special-file")
        actual.add(rel)
if actual != set(expected):
    fail("file-set")
for rel, entry in expected.items():
    full = os.path.join(root, *rel.split("/"))
    if os.path.getsize(full) != entry["length"]:
        fail(f"length:{rel}")
    h = hashlib.sha256()
    with open(full, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    if h.hexdigest() != entry["sha256"]:
        fail(f"sha256:{rel}")
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

# _payload_valid_version_name <name> — owned installation names only.
_payload_valid_version_name() {
    case "$1" in
        ""|.|..|*/*) return 1 ;;
        contract-scribe-*) return 0 ;;
        *) return 1 ;;
    esac
}

# payload_select <install-root> <version-dir-name>
# Atomically repoint the installer-owned `current` selector. A foreign
# non-symlink entry at <root>/current is never overwritten.
payload_select() {
    local root="$1" version="$2"
    _payload_valid_version_name "$version" || return 1
    [ -d "$root/$version" ] || return 1
    [ ! -L "$root/$version" ] || return 1
    if [ -e "$root/current" ] && [ ! -L "$root/current" ]; then
        return 1
    fi
    local tmp="$root/.current.tmp.$$"
    ln -sfn "$version" "$tmp" || return 1
    mv -T "$tmp" "$root/current"
}

# payload_remove <install-root> <version-dir-name>
# Remove one owned versioned installation directory.
payload_remove() {
    local root="$1" version="$2"
    _payload_valid_version_name "$version" || return 1
    [ -d "$root/$version" ] || return 1
    [ ! -L "$root/$version" ] || return 1
    [ -f "$root/$version/payload.json" ] || return 1
    if [ -L "$root/current" ] && [ "$(readlink "$root/current")" = "$version" ]; then
        rm -f "$root/current"
    fi
    rm -rf "$root/$version"
}

# payload_uninstall <install-root>
# Remove owned versioned dirs + owned selector; rmdir root only if empty.
# Caller-owned content (state, sentinels, unrelated files, links) survives.
payload_uninstall() {
    local root="$1"
    [ -d "$root" ] || return 0
    local dir
    for dir in "$root"/contract-scribe-*/; do
        dir="${dir%/}"
        [ -d "$dir" ] || continue
        [ -L "$dir" ] && continue
        [ -f "$dir/payload.json" ] || continue
        rm -rf "$dir"
    done
    # Interrupted installs may leave owned staging residue — reclaim it.
    for dir in "$root"/.install-staging.*; do
        [ -d "$dir" ] || continue
        [ -L "$dir" ] && continue
        rm -rf "$dir"
    done
    [ -L "$root/current" ] && rm -f "$root/current"
    rmdir "$root" 2>/dev/null || true
}
