#!/usr/bin/env python3
"""prepare-candidate.py — build and freeze one release candidate (M6-R1).

Manually invoked by .github/workflows/release.yml (operation=prepare) or
locally by a maintainer. Produces the frozen candidate identity plus the
payload-map pair content that must be recorded through a reviewed change
(ADR 0006); this script never mutates GitHub state.

Steps:
  1. Validate input shapes.
  2. Prove both revisions are ancestors of origin/main before any build.
  3. Record wrapper identity (action.yml name + sha256 at source_revision).
  4. Build the payload from payload_source_revision in a detached worktree
     using that revision's own build-payload.sh (or the gated test builder).
  5. Verify manifest/archive digests and derive the release identity.
  6. Inventory bundled package dependencies from the archive's deps.json.
  7. Capture toolchain/runner/provenance observations.
  8. Emit candidate.json (+ candidate.sha256), the built artifacts, the
     proposed payload-map.json, and a human summary.

Marker contract: release-prepare stage=<name> result=ok|fail reason=<short>
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import urllib.parse

BOUND_METADATA_JSON = 2 * 1024 * 1024
BOUND_FILE_TEXT = 512 * 1024

_HEX40 = re.compile(r"[0-9a-f]{40}")
_RELEASE_VERSION = re.compile(r"v\d+\.\d+\.\d+")
_WRAPPER_NAME = re.compile(r"[a-z0-9][a-z0-9-]{0,63}")
_SAFE_REASON = re.compile(r"[a-z0-9-]{1,48}")


def marker(stage, result, reason="-"):
    print(f"release-prepare stage={stage} result={result} reason={reason}",
          flush=True)


def fail(stage, reason):
    assert _SAFE_REASON.fullmatch(reason)
    marker(stage, "fail", reason)
    raise SystemExit(1)


def check(stage, condition, reason):
    if not condition:
        fail(stage, reason)


def run_git(repo, *args):
    env = dict(os.environ)
    env["GIT_TERMINAL_PROMPT"] = "0"
    proc = subprocess.run(
        ["git", "-C", repo, *args], env=env,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        stdin=subprocess.DEVNULL)
    return proc.returncode, proc.stdout.decode("utf-8", "replace")


def require_git(repo, stage, *args):
    code, out = run_git(repo, *args)
    check(stage, code == 0, "git-" + args[0].replace(" ", "-"))
    return out


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def canonical_bytes(document):
    """Canonical JSON: UTF-8, sorted keys, two-space indent, LF endings,
    one trailing newline. The candidate digest hashes these exact bytes."""
    return (json.dumps(document, indent=2, sort_keys=True,
                       ensure_ascii=False) + "\n").encode("utf-8")


def load_json_file(path, what, limit=BOUND_METADATA_JSON):
    info = os.lstat(path)
    if os.path.islink(path) or not stat.S_ISREG(info.st_mode):
        fail("read", f"{what}-not-regular")
    if info.st_size > limit:
        fail("read", f"{what}-oversize")
    try:
        with open(path, "rb") as fh:
            return json.loads(fh.read().decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        fail("read", f"{what}-malformed")


def write_private(path, data):
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    flags |= getattr(os, "O_NOFOLLOW", 0)
    fd = os.open(path, flags, 0o600)
    with os.fdopen(fd, "wb") as out:
        out.write(data)


def write_json(path, document):
    write_private(path, canonical_bytes(document))


def env_text(name):
    value = os.environ.get(name)
    if value is None or value == "" or "\x00" in value or "\n" in value:
        return None
    return value


def test_builder():
    """Release-test seam: CONTRACTSCRIBE_RELEASE_TEST_BUILDER supplies a
    stand-in builder executable; honored only under the complete test gate
    so a stray environment can never redirect production preparation."""
    builder = os.environ.get("CONTRACTSCRIBE_RELEASE_TEST_BUILDER")
    if os.environ.get("CONTRACTSCRIBE_RELEASE_TEST") != "1":
        if builder is not None:
            fail("test-gate", "test-env-without-mode")
        return None
    if builder is None:
        return None
    if os.environ.get("GITHUB_ACTIONS") != "true":
        fail("test-gate", "outside-actions")
    if os.environ.get("GITHUB_REPOSITORY") != "SolusQuest/contract-scribe":
        fail("test-gate", "foreign-repository")
    if not os.path.isabs(builder) or os.path.islink(builder) \
            or not os.path.isfile(builder):
        fail("test-gate", "builder-invalid")
    return builder


def find_bash():
    """Resolve a POSIX bash. On Windows, C:\\Windows\\System32\\bash.exe is
    the WSL launcher and cannot see Win32 paths — prefer Git's bash."""
    candidates = []
    for entry in os.environ.get("PATH", "").split(os.pathsep):
        for name in ("bash.exe", "bash"):
            path = os.path.join(entry, name)
            if os.path.isfile(path):
                candidates.append(path)
    for path in candidates:
        if "system32" not in path.lower():
            return path
    return candidates[0] if candidates else "bash"


def command_line(argv):
    try:
        proc = subprocess.run(argv, stdout=subprocess.PIPE,
                              stderr=subprocess.DEVNULL,
                              stdin=subprocess.DEVNULL, timeout=30)
    except OSError:
        return None
    if proc.returncode != 0:
        return None
    return proc.stdout.decode("utf-8", "replace").splitlines()[0].strip()


def dotnet_runtime():
    try:
        proc = subprocess.run(["dotnet", "--list-runtimes"],
                              stdout=subprocess.PIPE,
                              stderr=subprocess.DEVNULL,
                              stdin=subprocess.DEVNULL, timeout=30)
    except OSError:
        return None
    for line in proc.stdout.decode("utf-8", "replace").splitlines():
        if line.startswith("Microsoft.NETCore.App "):
            return line.split()[1]
    return None


def dependency_inventory(archive_path):
    """Stream the CLI deps.json out of the archive and reduce it to a
    sorted name/version package inventory. The A1 file inventory already
    gives byte-level closure; this is the package-level view."""
    entries = []
    found = False
    with tarfile.open(archive_path, "r:gz") as archive:
        for member in archive:
            if member.isfile() and member.name.endswith(
                    "ContractScribe.Cli.deps.json"):
                if found:
                    fail("inventory", "deps-json-duplicate")
                handle = archive.extractfile(member)
                data = handle.read(BOUND_METADATA_JSON + 1)
                if len(data) > BOUND_METADATA_JSON:
                    fail("inventory", "deps-json-oversize")
                try:
                    deps = json.loads(data.decode("utf-8"))
                except (UnicodeDecodeError, json.JSONDecodeError):
                    fail("inventory", "deps-json-malformed")
                libraries = deps.get("libraries")
                check("inventory", isinstance(libraries, dict),
                      "deps-json-libraries")
                for key, value in libraries.items():
                    if isinstance(value, dict) \
                            and value.get("type") == "package":
                        name, _, version = key.rpartition("/")
                        entries.append({"name": name, "version": version})
                found = True
    check("inventory", found, "deps-json-missing")
    entries.sort(key=lambda item: (item["name"], item["version"]))
    return entries


def main():
    parser = argparse.ArgumentParser(
        description="Build and freeze one ContractScribe release candidate.")
    parser.add_argument("--repo", required=True,
                        help="Path to a contract-scribe checkout")
    parser.add_argument("--output", required=True,
                        help="Output directory for the candidate bundle")
    parser.add_argument("--source-revision", required=True,
                        help="40-hex release/tag target and wrapper revision")
    parser.add_argument("--payload-source-revision", required=True,
                        help="40-hex A1 build revision (map sourceRevision)")
    parser.add_argument("--release-version", required=True,
                        help="Fixed release version, vX.Y.Z")
    parser.add_argument("--wrapper", required=True,
                        help="Wrapper component name (payload-map wrapper)")
    parser.add_argument("--repository", default="SolusQuest/contract-scribe")
    args = parser.parse_args()

    stage = "validate"
    check(stage, _HEX40.fullmatch(args.source_revision), "source-revision")
    check(stage, _HEX40.fullmatch(args.payload_source_revision),
          "payload-source-revision")
    check(stage, _RELEASE_VERSION.fullmatch(args.release_version),
          "release-version")
    check(stage, _WRAPPER_NAME.fullmatch(args.wrapper), "wrapper-name")
    check(stage, re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+",
                              args.repository), "repository-name")
    check(stage, os.path.isdir(os.path.join(args.repo, ".git"))
          or os.path.isfile(os.path.join(args.repo, ".git")), "repo-missing")
    output = os.path.abspath(args.output)
    check(stage, not os.path.exists(output) or
          (os.path.isdir(output) and not os.listdir(output)),
          "output-not-empty")

    stage = "reachability"
    require_git(args.repo, stage, "fetch", "origin", "main")
    _, main_tip = run_git(args.repo, "rev-parse", "refs/remotes/origin/main")
    main_tip = main_tip.strip()
    check(stage, _HEX40.fullmatch(main_tip), "main-ref")
    for rev in (args.source_revision, args.payload_source_revision):
        code, _ = run_git(args.repo, "merge-base", "--is-ancestor",
                          rev, "refs/remotes/origin/main")
        check(stage, code == 0, "not-main-reachable")

    stage = "wrapper"
    map_rel = "scripts/action/payload-map.json"
    action_rel = "action.yml"
    action_text = require_git(args.repo, stage, "show",
                              f"{args.source_revision}:{action_rel}")
    action_sha = sha256_bytes(action_text.encode("utf-8"))
    action_name = None
    for line in action_text.splitlines():
        match = re.match(r"^name:\s*'?\"?([A-Za-z0-9_-]+)'?\"?\s*$", line)
        if match:
            action_name = match.group(1)
            break
    check(stage, action_name is not None, "action-name")
    map_text = require_git(args.repo, stage, "show",
                           f"{args.source_revision}:{map_rel}")
    try:
        map_doc = json.loads(map_text)
    except json.JSONDecodeError:
        fail(stage, "map-malformed")
    check(stage, isinstance(map_doc, dict)
          and map_doc.get("wrapper") == args.wrapper
          and map_doc.get("mapVersion") == 1, "wrapper-mismatch")

    stage = "build"
    work = os.path.join(output, ".build")
    os.makedirs(work, mode=0o700)
    source_dir = os.path.join(work, "payload-src")
    builder = test_builder()
    try:
        require_git(args.repo, stage, "worktree", "add", "--detach",
                    source_dir, args.payload_source_revision)
        build_out = os.path.join(work, "out")
        os.makedirs(build_out)
        bash = find_bash()
        if builder is None:
            builder_cmd = [bash, os.path.join(
                source_dir, "scripts", "release", "build-payload.sh"),
                "--output", build_out, "--work",
                os.path.join(work, "builder")]
        else:
            builder_cmd = [bash, builder, "--output", build_out,
                           "--work", os.path.join(work, "builder")]
        env = dict(os.environ)
        env["GIT_TERMINAL_PROMPT"] = "0"
        proc = subprocess.run(builder_cmd, cwd=source_dir, env=env)
        check(stage, proc.returncode == 0, "builder-failed")
    finally:
        run_git(args.repo, "worktree", "remove", "--force", source_dir)

    stage = "manifest"
    archives = [name for name in os.listdir(build_out)
                if name.endswith(".tar.gz")]
    check(stage, len(archives) == 1, "archive-count")
    asset_name = archives[0]
    archive_path = os.path.join(build_out, asset_name)
    sha_path = archive_path[:-len(".tar.gz")] + ".sha256"
    manifest_path = archive_path[:-len(".tar.gz")] + ".payload.json"
    check(stage, os.path.isfile(sha_path), "sidecar-missing")
    check(stage, os.path.isfile(manifest_path), "manifest-missing")
    archive_sha = sha256_file(archive_path)
    with open(sha_path, "rb") as fh:
        sidecar = fh.read(4096).decode("utf-8", "replace")
    check(stage, sidecar.split()[0] == archive_sha, "archive-sha-mismatch")
    manifest = load_json_file(manifest_path, "manifest")
    check(stage, manifest.get("payloadFormat") == 1, "manifest-format")
    check(stage, manifest.get("sourceRevision")
          == args.payload_source_revision, "manifest-source")
    check(stage, manifest.get("runtimeIdentifier") == "linux-x64",
          "manifest-runtime")
    tool_version = manifest.get("toolVersion", "")
    check(stage, isinstance(tool_version, str)
          and tool_version.endswith("+" + args.payload_source_revision)
          and asset_name == f"contract-scribe-{tool_version}-linux-x64.tar.gz",
          "manifest-toolversion")
    manifest_sha = sha256_file(manifest_path)
    archive_bytes = os.lstat(archive_path).st_size
    inventory = dependency_inventory(archive_path)
    defaults_sha = manifest.get("defaultsJsonSha256", "")
    check(stage, isinstance(defaults_sha, str)
          and re.fullmatch(r"[0-9a-f]{64}", defaults_sha), "defaults-sha")

    stage = "candidate"
    release_tag = f"payload-{tool_version}"
    release_name = f"contract-scribe payload {tool_version}"
    release_body = (
        f"ContractScribe payload candidate for release "
        f"{args.release_version}.\n\n"
        f"- toolVersion: {tool_version}\n"
        f"- payloadSourceRevision: {args.payload_source_revision}\n"
        f"- sourceRevision: {args.source_revision}\n"
        f"- archiveSha256: {archive_sha}\n")
    identity = {
        "repository": args.repository,
        "repositoryId": env_text("GITHUB_REPOSITORY_ID"),
        "sourceRevision": args.source_revision,
        "wrapper": {
            "name": args.wrapper,
            "revision": args.source_revision,
            "actionYmlName": action_name,
            "actionYmlSha256": action_sha,
        },
        "payloadSourceRevision": args.payload_source_revision,
        "releaseVersion": args.release_version,
        "releaseTag": release_tag,
        "assetName": asset_name,
        "archiveSha256": archive_sha,
        "archiveBytes": archive_bytes,
        "manifestSha256": manifest_sha,
        "defaultsJsonSha256": defaults_sha,
        "releaseName": release_name,
        "releaseBodySha256": sha256_bytes(release_body.encode("utf-8")),
        "prerelease": False,
        "toolchain": {
            "dotnetSdk": command_line(["dotnet", "--version"]),
            "dotnetRuntime": dotnet_runtime(),
            "python": command_line(
                [sys.executable, "--version"]),
            "tar": command_line(["tar", "--version"]),
        },
        "runner": {
            "os": env_text("RUNNER_OS"),
            "arch": env_text("RUNNER_ARCH"),
            "imageOs": env_text("ImageOS"),
            "imageVersion": env_text("ImageVersion"),
        },
        "dependencyInventory": inventory,
    }
    provenance = {
        "workflowSha": env_text("GITHUB_WORKFLOW_SHA"),
        "sha": env_text("GITHUB_SHA"),
        "runId": env_text("GITHUB_RUN_ID"),
        "runAttempt": env_text("GITHUB_RUN_ATTEMPT"),
        "actor": env_text("GITHUB_ACTOR"),
    }
    candidate = {
        "candidateVersion": 1,
        "identity": identity,
        "provenance": provenance,
    }
    candidate_digest = sha256_bytes(canonical_bytes(identity))

    payload_map = {
        "mapVersion": 1,
        "wrapper": args.wrapper,
        "payload": {
            "repository": args.repository,
            "releaseTag": release_tag,
            "toolVersion": tool_version,
            "sourceRevision": args.payload_source_revision,
            "runtimeIdentifier": "linux-x64",
            "assetName": asset_name,
            "sha256": archive_sha,
            "assets": [asset_name],
        },
    }

    os.makedirs(output, exist_ok=True)
    write_json(os.path.join(output, "candidate.json"), candidate)
    write_private(os.path.join(output, "candidate.sha256"),
                  (candidate_digest + "  candidate.json\n").encode("utf-8"))
    for name in (asset_name, os.path.basename(sha_path),
                 os.path.basename(manifest_path)):
        source = os.path.join(build_out, name)
        with open(source, "rb") as fh:
            write_private(os.path.join(output, name), fh.read())
    write_json(os.path.join(output, "payload-map.json"), payload_map)

    summary = os.path.join(output, "summary.md")
    lines = [
        "# Release candidate",
        "",
        f"- candidateDigest: `{candidate_digest}`",
        f"- releaseVersion: `{args.release_version}`",
        f"- sourceRevision (tag/wrapper): `{args.source_revision}`",
        f"- payloadSourceRevision: `{args.payload_source_revision}`",
        f"- releaseTag: `{release_tag}`",
        f"- assetName: `{asset_name}`",
        f"- archiveSha256: `{archive_sha}`",
        f"- archiveBytes: {archive_bytes}",
        f"- defaultsJsonSha256: `{defaults_sha}`",
        f"- manifestSha256: `{manifest_sha}`",
        f"- dependencies: {len(inventory)} packages",
        "",
        "Next steps:",
        "1. Review `payload-map.json` and record it as a reviewed change to "
        "`scripts/action/payload-map.json` on main (the `action_packaged` "
        "CI job must pass its byte-equality rebuild on that change).",
        "2. Re-run prepare against the merged main revision so the final "
        "candidate binds the reviewed wrapper revision.",
        "3. Stage the unpublished draft with operation `stage-draft` "
        "(requires the maintainer-provisioned release-candidate "
        "environment).",
    ]
    write_private(summary, ("\n".join(lines) + "\n").encode("utf-8"))

    github_output = env_text("GITHUB_OUTPUT")
    if github_output:
        with open(github_output, "a", encoding="utf-8", newline="\n") as fh:
            fh.write(f"candidate-digest={candidate_digest}\n")
            fh.write(f"release-tag={release_tag}\n")
            fh.write(f"asset-name={asset_name}\n")
    shutil.rmtree(work, ignore_errors=True)
    marker("outputs", "ok", "-")
    marker("candidate", "ok", candidate_digest)
    print(f"release-prepare: candidate {candidate_digest} at {output}",
          flush=True)


if __name__ == "__main__":
    main()
