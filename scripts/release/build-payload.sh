#!/usr/bin/env bash
# build-payload.sh — build the D2 framework-dependent DLL payload archive.
#
# Produces test-only build outputs (issue #18 / M6-A1):
#   <out>/contract-scribe-<toolVersion>-linux-x64.tar.gz
#   <out>/contract-scribe-<toolVersion>-linux-x64.sha256
#   <out>/contract-scribe-<toolVersion>-linux-x64.payload.json
#
# The archive is a single gzip-compressed tar containing one top directory
#   contract-scribe-<toolVersion>-linux-x64/
#     payload.json            manifest + bounded file inventory (no self-hash)
#     <complete publish output>
#
# Build contract:
#   - standard framework-dependent publish for linux-x64;
#   - UseAppHost=false; no trimming, AOT, self-contained, or single-file;
#   - complete publish tree preserved (BuildHost/, satellite dirs, config/);
#   - archive identity binds the actual embedded informational version and
#     the exact HEAD revision; a mismatch fails the build.
#
# Requirements: bash, git, dotnet SDK (per global.json), python3, GNU tar,
# sha256sum. The checkout must be clean for product-input paths.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT_DIR="$REPO_ROOT/artifacts/payload"
WORK_DIR=""

usage() {
    echo "usage: $0 [--output <dir>] [--work <dir>]" >&2
    exit 2
}

while [ $# -gt 0 ]; do
    case "$1" in
        --output) OUT_DIR="$2"; shift 2 ;;
        --work) WORK_DIR="$2"; shift 2 ;;
        *) usage ;;
    esac
done

if [ -z "$WORK_DIR" ]; then
    WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/contract-scribe-payload.XXXXXX")"
    trap 'rm -rf "$WORK_DIR"' EXIT
else
    mkdir -p "$WORK_DIR"
fi
mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"

PRODUCT_INPUTS=(src config Directory.Build.props Directory.Packages.props
                global.json ContractScribe.slnx NuGet.Config)
if [ -n "$(git -C "$REPO_ROOT" status --porcelain -- "${PRODUCT_INPUTS[@]}")" ]; then
    echo "build-payload: refusing to build from a dirty checkout (product inputs changed)" >&2
    git -C "$REPO_ROOT" status --porcelain -- "${PRODUCT_INPUTS[@]}" >&2
    exit 1
fi

HEAD="$(git -C "$REPO_ROOT" rev-parse HEAD)"
case "$HEAD" in
    *[!0-9a-f]*|?????????????????????????????????????????*)
        echo "build-payload: unexpected HEAD revision: $HEAD" >&2; exit 1 ;;
esac
[ "${#HEAD}" -eq 40 ] || { echo "build-payload: HEAD is not a 40-hex sha: $HEAD" >&2; exit 1; }

CLI_PROJ="$REPO_ROOT/src/ContractScribe.Cli/ContractScribe.Cli.csproj"
[ -f "$CLI_PROJ" ] || { echo "build-payload: missing $CLI_PROJ" >&2; exit 1; }

# Consumer-owned resource limits recorded into payload.json; the installer
# enforces these bounds and never raises them from archive metadata.
BOUND_COMPRESSED=$((256 * 1024 * 1024))
BOUND_EXPANDED=$((512 * 1024 * 1024))
BOUND_FILE=$((128 * 1024 * 1024))
BOUND_COUNT=4096
BOUND_PATH=1024
BOUND_DEPTH=16

STAGE="$WORK_DIR/stage"
CONTENT="$STAGE/content"
mkdir -p "$CONTENT"

echo "build-payload: restoring CLI for linux-x64"
dotnet restore "$CLI_PROJ" -r linux-x64 --nologo

echo "build-payload: publishing framework-dependent linux-x64 payload"
dotnet publish "$CLI_PROJ" \
    -c Release --no-restore \
    -r linux-x64 --self-contained false \
    -p:UseAppHost=false \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    -p:PublishAot=false \
    -o "$CONTENT"

# Debug symbols are not part of the executable payload.
find "$CONTENT" -name '*.pdb' -type f -delete

ENTRYPOINT="$CONTENT/ContractScribe.Cli.dll"
[ -f "$ENTRYPOINT" ] || { echo "build-payload: publish produced no ContractScribe.Cli.dll" >&2; exit 1; }

# Redirect instead of $(dotnet ...): on MSYS2 a Windows host inside command
# substitution can fail before exec; file redirection is portable.
dotnet "$ENTRYPOINT" --version >"$WORK_DIR/version-line.txt" || {
    echo "build-payload: packed CLI did not execute --version" >&2; exit 1; }
VERSION_LINE="$(cat "$WORK_DIR/version-line.txt")"
# Expected: "ContractScribe 0.1.0-dev+<40-hex-sha>"
TOOL_VERSION="${VERSION_LINE#ContractScribe }"
if [ "$VERSION_LINE" != "ContractScribe $TOOL_VERSION" ] || [ -z "$TOOL_VERSION" ]; then
    echo "build-payload: cannot parse informational version from: $VERSION_LINE" >&2; exit 1
fi
EMBEDDED_SHA="${TOOL_VERSION##*+}"
if [ "$EMBEDDED_SHA" != "$HEAD" ]; then
    echo "build-payload: embedded source revision $EMBEDDED_SHA != HEAD $HEAD" >&2; exit 1
fi

TOP_DIR="contract-scribe-$TOOL_VERSION-linux-x64"
mv "$CONTENT" "$STAGE/$TOP_DIR"

DEFAULTS_SHA="$(sha256sum "$STAGE/$TOP_DIR/config/defaults.json" | cut -d' ' -f1)" || {
    echo "build-payload: publish output lacks config/defaults.json" >&2; exit 1; }

# Manifest + bounded inventory. Covers every regular file in the top dir
# except payload.json itself (the archive digest covers the manifest; no
# self-hash). Paths are archive-relative; mode is the permitted install mode.
python3 - "$STAGE/$TOP_DIR" "$TOOL_VERSION" "$HEAD" "$DEFAULTS_SHA" \
        "$BOUND_COMPRESSED" "$BOUND_EXPANDED" "$BOUND_FILE" "$BOUND_COUNT" \
        "$BOUND_PATH" "$BOUND_DEPTH" <<'PYEOF'
import hashlib, json, os, sys

top, tool_version, head, defaults_sha = sys.argv[1:5]
b_compressed, b_expanded, b_file, b_count, b_path, b_depth = map(int, sys.argv[5:11])

entries = []
total = 0
for dirpath, dirnames, filenames in os.walk(top):
    dirnames.sort()
    for name in sorted(filenames):
        full = os.path.join(dirpath, name)
        rel = os.path.relpath(full, top).replace(os.sep, "/")
        if rel == "payload.json":
            continue
        st = os.lstat(full)
        if not os.path.isfile(full) or os.path.islink(full):
            print(f"build-payload: refusing non-regular member {rel}", file=sys.stderr)
            sys.exit(1)
        if st.st_size > b_file:
            print(f"build-payload: member exceeds file bound: {rel}", file=sys.stderr)
            sys.exit(1)
        if len(rel.encode()) > b_path or rel.count("/") + 1 > b_depth:
            print(f"build-payload: member path exceeds bounds: {rel}", file=sys.stderr)
            sys.exit(1)
        h = hashlib.sha256()
        with open(full, "rb") as fh:
            for chunk in iter(lambda: fh.read(1 << 20), b""):
                h.update(chunk)
        mode = "0755" if (st.st_mode & 0o111) else "0644"
        entries.append({"path": rel, "length": st.st_size,
                        "sha256": h.hexdigest(), "mode": mode})
        total += st.st_size

entries.sort(key=lambda e: e["path"])
if len(entries) > b_count:
    print("build-payload: file count exceeds bound", file=sys.stderr); sys.exit(1)
if total > b_expanded:
    print("build-payload: expanded size exceeds bound", file=sys.stderr); sys.exit(1)

manifest = {
    "payloadFormat": 1,
    "tool": "contract-scribe",
    "distributionChannel": "d2-framework-dependent-dll",
    "toolVersion": tool_version,
    "sourceRevision": head,
    "cleanCheckout": True,
    "runtimeIdentifier": "linux-x64",
    "targetFramework": "net10.0",
    "entrypoint": "ContractScribe.Cli.dll",
    "defaultsJsonSha256": defaults_sha,
    "bounds": {
        "compressedBytes": b_compressed,
        "expandedBytes": b_expanded,
        "fileBytes": b_file,
        "fileCount": b_count,
        "memberPathBytes": b_path,
        "memberDepth": b_depth,
    },
    "files": entries,
}
out = os.path.join(top, "payload.json")
with open(out, "w", encoding="utf-8", newline="\n") as fh:
    json.dump(manifest, fh, indent=2, ensure_ascii=False)
    fh.write("\n")
print(f"build-payload: inventoried {len(entries)} members, {total} bytes")
PYEOF

ARCHIVE_NAME="$TOP_DIR.tar.gz"
SHA_NAME="$TOP_DIR.sha256"
MANIFEST_NAME="$TOP_DIR.payload.json"

tar -C "$STAGE" \
    --sort=name --mtime="@0" --owner=0 --group=0 --numeric-owner \
    -cf - "$TOP_DIR" | gzip -n > "$OUT_DIR/$ARCHIVE_NAME"

COMPRESSED="$(stat -c %s "$OUT_DIR/$ARCHIVE_NAME")"
if [ "$COMPRESSED" -gt "$BOUND_COMPRESSED" ]; then
    echo "build-payload: archive exceeds compressed bound ($COMPRESSED)" >&2; exit 1
fi

(cd "$OUT_DIR" && sha256sum "$ARCHIVE_NAME" > "$SHA_NAME")
cp "$STAGE/$TOP_DIR/payload.json" "$OUT_DIR/$MANIFEST_NAME"

echo "build-payload: wrote $OUT_DIR/$ARCHIVE_NAME ($COMPRESSED bytes)"
echo "build-payload: wrote $OUT_DIR/$SHA_NAME"
echo "build-payload: wrote $OUT_DIR/$MANIFEST_NAME"
