#!/usr/bin/env python3
"""install.py — install the verified payload archive under the frozen D2
install semantics (issue #18 / M6-A1, ported for real acquisition).

Stage order and markers are the frozen `payload-install stage=<name>` set:
acquire -> digest -> scan -> extract -> inventory -> prereq -> publish, then
the installer-owned `current` selector repoint. Cache handling uses the
separate `action-cache` marker vocabulary and is anchored to the retained
verified archive: a previously installed tree is reused only after the
retained archive's SHA-256 re-verifies against the map AND the installed
tree's inventory equals that archive's manifest exactly.

Runs as the step entry process; SIGINT/SIGTERM reclaim owned staging and
exit 130.
"""

import hashlib
import json
import os
import shutil
import signal
import stat
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C
import payload_extract

_STAGE_MARK = "payload-install"


def fail_install(stage, reason):
    C.marker(_STAGE_MARK, f"stage={stage}", "fail", reason)
    C.write_output("action-status", f"action.install-{reason}")
    raise SystemExit(1)


def fail_cache(reason):
    C.marker_kv("action-cache", ("check=hit",), "fail", reason)
    C.write_output("action-status", f"action.install-cache-{reason}")
    raise SystemExit(1)


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


class Staging:
    """Owned staging directory inside the install root (same filesystem as
    the publish destination so publication is a rename)."""

    def __init__(self, root):
        self.root = root
        self.path = None

    def create(self):
        import tempfile
        self.path = tempfile.mkdtemp(prefix=".install-staging.", dir=self.root)
        os.chmod(self.path, 0o700)
        return self.path

    def cleanup(self):
        if self.path and os.path.lexists(self.path):
            shutil.rmtree(self.path, ignore_errors=True)
            self.path = None


def verify_inventory(top_dir):
    """The frozen manifest/tree identity + inventory check (port of the A1
    installer's inventory stage). Returns the parsed manifest."""
    for required in ("payload.json", "ContractScribe.Cli.dll",
                     "ContractScribe.Cli.deps.json",
                     "ContractScribe.Cli.runtimeconfig.json",
                     "config/defaults.json"):
        if not os.path.isfile(os.path.join(top_dir, *required.split("/"))):
            fail_install("inventory", "missing-member")
    manifest_path = os.path.join(top_dir, "payload.json")
    if os.path.islink(manifest_path):
        fail_install("inventory", "linked-manifest")
    manifest = C.load_json_file(manifest_path, C.BOUND_METADATA, "manifest")
    if manifest.get("payloadFormat") != 1:
        fail_install("inventory", "payload-format")
    if manifest.get("tool") != "contract-scribe":
        fail_install("inventory", "tool")
    if manifest.get("distributionChannel") != "d2-framework-dependent-dll":
        fail_install("inventory", "channel")
    if manifest.get("runtimeIdentifier") != "linux-x64":
        fail_install("inventory", "rid")
    if manifest.get("targetFramework") != "net10.0":
        fail_install("inventory", "tfm")
    if manifest.get("entrypoint") != "ContractScribe.Cli.dll":
        fail_install("inventory", "entrypoint")
    revision = manifest.get("sourceRevision")
    if not isinstance(revision, str) or not C._HEX40.fullmatch(revision):
        fail_install("inventory", "source-revision")
    version = manifest.get("toolVersion")
    if not isinstance(version, str) or not version.endswith("+" + revision):
        fail_install("inventory", "tool-version")
    expected_top = f"contract-scribe-{version}-linux-x64"
    if os.path.basename(top_dir.rstrip("/")) != expected_top:
        fail_install("inventory", "top-directory")
    files = manifest.get("files")
    if not isinstance(files, list):
        fail_install("inventory", "files")
    paths = [e.get("path") for e in files if isinstance(e, dict)]
    if len(paths) != len(set(paths)):
        fail_install("inventory", "duplicate-path")
    for entry in files:
        if entry.get("mode") not in ("0644", "0755"):
            fail_install("inventory", "mode")
        if not isinstance(entry.get("path"), str) or entry["path"].startswith("/"):
            fail_install("inventory", "path")
    expected = {e["path"]: e for e in files}
    defaults_path = os.path.join(top_dir, "config", "defaults.json")
    if sha256_file(defaults_path) != manifest.get("defaultsJsonSha256"):
        fail_install("inventory", "defaults-digest")
    try:
        verify_tree(top_dir, expected)
    except InventoryFailure as error:
        fail_install("inventory", str(error))
    return manifest


class InventoryFailure(Exception):
    pass


def _inv_fail(reason):
    raise InventoryFailure(reason)


def verify_tree(top_dir, expected):
    """Exact file-set equality + per-file length/sha256/mode; no links."""
    actual = set()
    for dirpath, dirnames, filenames in os.walk(top_dir):
        for name in dirnames:
            if os.path.islink(os.path.join(dirpath, name)):
                _inv_fail("linked-directory")
        for name in filenames:
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, top_dir).replace(os.sep, "/")
            if rel == "payload.json":
                continue
            if os.path.islink(full) or not os.path.isfile(full):
                _inv_fail("linked-or-special")
            actual.add(rel)
    if actual != set(expected):
        _inv_fail("file-set")
    for rel, entry in expected.items():
        full = os.path.join(top_dir, *rel.split("/"))
        if os.path.getsize(full) != entry["length"]:
            _inv_fail("length")
        if sha256_file(full) != entry["sha256"]:
            _inv_fail("sha256")
        # POSIX mode is only expressible on POSIX hosts; the Action always
        # runs on Linux, where the check is authoritative.
        if os.name == "posix" \
                and stat.S_IMODE(os.stat(full).st_mode) != int(entry["mode"], 8):
            _inv_fail("mode-mismatch")


def manifest_from_archive(archive_path, top):
    """Read <top>/payload.json out of a verified archive without extracting."""
    import tarfile
    wanted = top + "/payload.json"
    try:
        with tarfile.open(archive_path, "r:gz") as tar:
            member = tar.getmember(wanted)
            data = tar.extractfile(member).read(C.BOUND_METADATA + 1)
            if len(data) > C.BOUND_METADATA:
                raise InventoryFailure("manifest-oversize")
            return json.loads(data.decode("utf-8"))
    except (KeyError, tarfile.TarError, OSError, EOFError, UnicodeDecodeError,
            json.JSONDecodeError, AttributeError):
        raise InventoryFailure("manifest-unreadable")


def prereq(dotnet):
    """dotnet host prereq stage: the resolved host path from prepare is the
    same binary probed here and invoked later."""
    try:
        _, out_b, _ = C.run_owned([dotnet, "--list-runtimes"], timeout=30)
        out = out_b.decode("utf-8", "replace")
    except OSError:
        fail_install("prereq", "missing-runtime")
    if not any(line.startswith("Microsoft.NETCore.App 10.")
               for line in out.splitlines()):
        fail_install("prereq", "missing-runtime")


def publish(staged_top, dest):
    """Atomic same-filesystem no-clobber rename (mv -T -n semantics)."""
    if os.path.lexists(dest):
        fail_install("publish", "destination-exists")
    rc, _, _ = C.run_owned(["mv", "-T", "-n", "--", staged_top, dest],
                            timeout=30)
    if rc != 0:
        fail_install("publish", "rename-failed")
    if os.path.lexists(staged_top):
        fail_install("publish", "destination-exists")


def _mv(args):
    rc, _, _ = C.run_owned(["mv", "-T"] + args, timeout=30)
    return rc


def select_current(root, version):
    """Atomically repoint the installer-owned `current` symlink (port of the
    A1 selector); a foreign non-symlink entry is never overwritten."""
    current = os.path.join(root, "current")
    if os.path.lexists(current) and not os.path.islink(current):
        fail_install("select", "foreign-current")
    tmp = os.path.join(root, f".current.tmp.{os.getpid()}")
    swap = os.path.join(root, f".current.swap.{os.getpid()}")
    try:
        os.symlink(version, tmp)
        if os.path.islink(current):
            if _mv(["--", current, swap]) != 0:
                os.unlink(tmp)
                fail_install("select", "failed")
            if not os.path.islink(swap):
                _mv(["-n", "--", swap, current])
                os.unlink(tmp)
                fail_install("select", "foreign-current")
        _mv(["-n", "--", tmp, current])
        if os.path.lexists(tmp):
            # Declined — a destination raced in; restore the moved-aside
            # owned link with no-clobber too.
            if os.path.lexists(swap):
                _mv(["-n", "--", swap, current])
            os.unlink(tmp)
            fail_install("select", "raced")
        if os.path.lexists(swap):
            os.unlink(swap)
    except OSError:
        fail_install("select", "failed")


def install(archive, expected_sha, root, dotnet, staging):
    """The frozen stage sequence. `archive` is the verified retained file."""
    def abort(_signum, _frame):
        staging.cleanup()
        raise SystemExit(130)

    signal.signal(signal.SIGINT, abort)
    signal.signal(signal.SIGTERM, abort)

    # acquire — bounded ingest into owned staging on the install filesystem
    if not os.path.isfile(archive) or os.path.islink(archive):
        fail_install("acquire", "missing-artifact")
    if os.path.getsize(archive) > C.BOUND_COMPRESSED:
        fail_install("acquire", "compressed-bound")
    staging_path = staging.create()
    private_archive = os.path.join(staging_path, "archive.tar.gz")
    try:
        with open(archive, "rb") as src, open(private_archive, "xb") as dst:
            remaining = C.BOUND_COMPRESSED + 1
            while remaining > 0:
                chunk = src.read(min(1 << 20, remaining))
                if not chunk:
                    break
                remaining -= len(chunk)
                dst.write(chunk)
    except OSError:
        staging.cleanup()
        fail_install("acquire", "copy-failed")
    if os.path.getsize(private_archive) > C.BOUND_COMPRESSED:
        staging.cleanup()
        fail_install("acquire", "compressed-bound")
    C.marker(_STAGE_MARK, "stage=acquire", "ok")

    # digest — independently supplied expected SHA-256
    if sha256_file(private_archive) != expected_sha:
        staging.cleanup()
        fail_install("digest", "sha256-mismatch")
    C.marker(_STAGE_MARK, "stage=digest", "ok")

    # scan — member policy (frozen)
    report = payload_extract._inspect(private_archive, None)
    if not report["ok"]:
        staging.cleanup()
        fail_install("scan", "member-policy")
    top = report["top"]
    if not top:
        staging.cleanup()
        fail_install("scan", "no-top-directory")
    C.marker(_STAGE_MARK, "stage=scan", "ok")

    # extract — validated members only, fresh private staging
    extract_dir = os.path.join(staging_path, "extracted")
    os.makedirs(extract_dir)
    report = payload_extract._extract(private_archive, extract_dir, top)
    if not report["ok"]:
        staging.cleanup()
        fail_install("extract", "member-policy")
    C.marker(_STAGE_MARK, "stage=extract", "ok")

    # inventory — installed tree equals the manifest exactly
    verify_inventory(os.path.join(extract_dir, top))
    C.marker(_STAGE_MARK, "stage=inventory", "ok")

    # prereq — dotnet host + Microsoft.NETCore.App 10.x (resolved host path)
    prereq(dotnet)
    C.marker(_STAGE_MARK, "stage=prereq", "ok")

    # publish — checked atomic rename; existing/appearing destination rejects
    dest = os.path.join(root, top)
    publish(os.path.join(extract_dir, top), dest)
    staging.cleanup()
    C.marker(_STAGE_MARK, "stage=publish", "ok")
    return dest


def install_from_verified(plan, archive, payload):
    """Full acquisition-to-publish path anchored to the verified archive."""
    root = plan["installRoot"]
    os.makedirs(root, exist_ok=True)
    version = "contract-scribe-" + payload["toolVersion"] + "-linux-x64"
    dest = os.path.join(root, version)

    # Re-verify the retained archive against the map digest on every path —
    # a cache hit is never trusted without the expected hash check.
    if sha256_file(archive) != payload["sha256"]:
        fail_cache("archive-mismatch")

    if os.path.lexists(dest):
        # Reuse an existing install only when its tree exactly equals the
        # verified archive's own manifest inventory — the archive bytes are
        # the trusted oracle, not the on-disk manifest.
        if os.path.islink(dest) or not os.path.isdir(dest):
            fail_cache("foreign-destination")
        try:
            trusted = manifest_from_archive(archive, version)
            if not isinstance(trusted.get("files"), list):
                raise InventoryFailure("manifest")
            if trusted.get("toolVersion") != payload["toolVersion"]:
                raise InventoryFailure("identity")
            expected = {e["path"]: e for e in trusted["files"]
                        if isinstance(e, dict)}
            verify_tree(dest, expected)
        except InventoryFailure as error:
            fail_cache(str(error))
        C.marker_kv("action-cache", ("check=hit",), "ok", "verified-reuse")
        select_current(root, version)
        return dest

    C.marker_kv("action-cache", ("check=miss",), "ok")
    # Reclaim interrupted-install residue before publishing.
    for name in os.listdir(root):
        if name.startswith(".install-staging."):
            candidate = os.path.join(root, name)
            if os.path.isdir(candidate) and not os.path.islink(candidate):
                shutil.rmtree(candidate, ignore_errors=True)
    staging = Staging(root)
    try:
        dest = install(archive, payload["sha256"], root, plan["dotnet"], staging)
    finally:
        staging.cleanup()
    select_current(root, version)
    return dest


def main():
    os.environ["CS_ACTION_STAGE"] = "install"
    """Production entry: installs only the archive that acquire.py verified
    against the checked-in map — no argument surface exists to substitute
    either."""
    plan = C.load_plan()
    acquired = C.load_json_file(C.work_file("acquired.json"),
                                C.BOUND_METADATA_JSON, "acquired")
    dest = install_from_verified(plan, acquired["archive"],
                                 acquired["payload"])
    C.write_output("install-dir", dest)


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:
        try:
            fail_install("internal", "exception")
        except Exception:
            raise SystemExit(1)
