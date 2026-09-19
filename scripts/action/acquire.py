#!/usr/bin/env python3
"""acquire.py — resolve the authorized release asset and download it bounded.

Steps: load the checked-in payload map -> resolve release/asset identity via
the fixed GitHub API (public by-tag, or authenticated bounded enumeration for
draft-capable candidates) -> download with the redirect/credential policy ->
verify the archive SHA-256 -> retain the verified archive as the install
oracle. Emits `action-acquire` and `action-cache` markers; the frozen
`payload-install` stages run in install.py.

The checked-in map file is the only authority for repository/version/hash —
this script has no argument surface that could replace it. Direct tests
inject alternate maps/api roots by importing acquire_archive() from
tests/action/driver.py (a test-only launcher), never through this entrypoint.
The env API-root seam applies the strict test gate in common.py.
"""

import hashlib
import json
import os
import re
import sys
import urllib.parse

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C

_PAYLOAD_ASSET_PATTERN = re.compile(
    r"^contract-scribe-[A-Za-z0-9.+-]+-linux-x64\.tar\.gz$")


def fail_acquire(stage, reason):
    C.marker("action-acquire", f"stage={stage}", "fail", reason)
    C.write_output("action-status", f"action.acquire-{reason}")
    raise SystemExit(1)


def load_map(path):
    try:
        document = C.load_json_file(path, 64 * 1024, "payload-map")
    except SystemExit:
        fail_acquire("map", "unreadable")
    if not isinstance(document, dict) or document.get("mapVersion") != 1 \
            or document.get("wrapper") != "contract-scribe-action":
        fail_acquire("map", "malformed")
    payload = document.get("payload")
    if payload is None:
        # Pre-release: no pair is authorized until R1 promotion records one.
        fail_acquire("map", "no-authorized-payload")
    if not isinstance(payload, dict) or set(payload) != {
            "repository", "releaseTag", "toolVersion", "sourceRevision",
            "runtimeIdentifier", "assetName", "sha256", "assets"}:
        fail_acquire("map", "malformed")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", payload["repository"]):
        fail_acquire("map", "repository")
    if not isinstance(payload["releaseTag"], str) or not payload["releaseTag"]:
        fail_acquire("map", "release-tag")
    if not re.fullmatch(r"[0-9a-f]{40}", str(payload["sourceRevision"])):
        fail_acquire("map", "source-revision")
    if not isinstance(payload["toolVersion"], str) \
            or not payload["toolVersion"].endswith("+" + payload["sourceRevision"]):
        fail_acquire("map", "tool-version")
    if payload["runtimeIdentifier"] != "linux-x64":
        fail_acquire("map", "runtime-identifier")
    expected_name = ("contract-scribe-" + payload["toolVersion"]
                     + "-" + payload["runtimeIdentifier"] + ".tar.gz")
    if payload["assetName"] != expected_name:
        fail_acquire("map", "asset-name")
    if not re.fullmatch(r"[0-9a-f]{64}", str(payload["sha256"])):
        fail_acquire("map", "sha256")
    if payload["assets"] != [payload["assetName"]]:
        fail_acquire("map", "asset-set")
    return payload


def resolve_release(root, repository, tag, token):
    """Return the pinned release object for the exact tag.

    Public mode (no token): the by-tag endpoint, which exposes published
    releases only. Authenticated mode: bounded enumeration, which is the only
    path that can see draft candidates. Both feed one identity check set.
    """
    owner, name = repository.split("/", 1)
    base = f"{root}/repos/{owner}/{name}"
    if token:
        matches = []
        for page in range(1, C.BOUND_MAX_PAGES + 1):
            try:
                rows = C.get_json(
                    f"{base}/releases?per_page=100&page={page}", token,
                    "releases")
            except C.ActionFailure as error:
                fail_acquire("resolve", str(error))
            if not isinstance(rows, list):
                fail_acquire("resolve", "releases-shape")
            matches.extend(r for r in rows if isinstance(r, dict)
                           and r.get("tag_name") == tag)
            if len(rows) < 100:
                break
        else:
            fail_acquire("resolve", "pagination-bound")
        if len(matches) != 1:
            fail_acquire("resolve",
                         "release-ambiguous" if matches else "release-not-found")
        return matches[0]
    try:
        release = C.get_json(
            f"{base}/releases/tags/{urllib.parse.quote(tag, safe='')}",
            None, "release")
    except C.ActionFailure as error:
        fail_acquire("resolve", str(error))
    if release.get("draft") is True:
        fail_acquire("resolve", "draft-needs-credential")
    return release


def pin_asset(release, payload):
    if not isinstance(release.get("id"), int):
        fail_acquire("resolve", "release-identity")
    assets = release.get("assets")
    if not isinstance(assets, list):
        fail_acquire("resolve", "assets-shape")
    matches = [a for a in assets if isinstance(a, dict)
               and a.get("name") == payload["assetName"]]
    if not matches:
        fail_acquire("resolve", "asset-missing")
    if len(matches) != 1:
        fail_acquire("resolve", "asset-duplicate")
    siblings = [a for a in assets if isinstance(a, dict)
                and _PAYLOAD_ASSET_PATTERN.match(str(a.get("name", "")))
                and a.get("name") != payload["assetName"]]
    if siblings:
        # A differently-named payload-shaped asset is an ambiguity hazard;
        # unrelated asset names are the release's own inventory (R1-owned).
        fail_acquire("resolve", "asset-unexpected")
    asset = matches[0]
    if not isinstance(asset.get("id"), int):
        fail_acquire("resolve", "asset-identity")
    if isinstance(asset.get("size"), int) and asset["size"] > C.BOUND_COMPRESSED:
        fail_acquire("resolve", "asset-oversize")
    return asset


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def acquire_archive(plan, map_path, api_root, token):
    payload = load_map(map_path)
    install_root = plan["installRoot"]
    archives = os.path.join(install_root, ".archives")
    os.makedirs(archives, exist_ok=True)
    retained = os.path.join(archives, payload["sha256"] + ".tar.gz")

    if os.path.lexists(retained):
        if os.path.islink(retained) or not os.path.isfile(retained):
            C.marker("action-cache", "check=hit", "fail", "poisoned")
            C.write_output("action-status", "action.cache-poisoned")
            raise SystemExit(1)
        actual = sha256_file(retained)
        if actual != payload["sha256"]:
            C.marker("action-cache", "check=hit", "fail", "poisoned")
            C.write_output("action-status", "action.cache-poisoned")
            raise SystemExit(1)
        C.marker("action-cache", "check=hit", "ok")
        return retained, payload

    C.marker("action-cache", "check=miss", "ok")

    release = resolve_release(api_root, payload["repository"],
                              payload["releaseTag"], token)
    C.marker("action-acquire", "stage=resolve", "ok")

    asset = pin_asset(release, payload)
    owner, name = payload["repository"].split("/", 1)
    asset_url = (f"{api_root}/repos/{owner}/{name}"
                 f"/releases/assets/{asset['id']}")

    staging = os.path.join(
        C.work_dir(), "archive-" + payload["sha256"] + ".partial")
    try:
        with open(staging, "xb") as sink:
            try:
                C.download_asset(asset_url, token, api_root, sink)
            except C.ActionFailure as error:
                fail_acquire("download", str(error))
        if sha256_file(staging) != payload["sha256"]:
            fail_acquire("download", "sha256-mismatch")
        os.replace(staging, retained)
    finally:
        if os.path.lexists(staging):
            os.unlink(staging)
    C.marker("action-acquire", "stage=download", "ok")
    return retained, payload


DEFAULT_MAP = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           "payload-map.json")


def main():
    """Production entry: the checked-in map and the fixed/gated API root are
    the only authorities — no argument surface exists to override either."""
    token = os.environ.get("CONTRACTSCRIBE_ACQUISITION_TOKEN") or None
    C.mask(token)
    if C.test_mode_enabled() and token is not None \
            and token != C.SYNTHETIC_ACQUISITION_TOKEN:
        fail_acquire("credential", "test-credential")

    plan = C.load_plan()
    archive, payload = acquire_archive(plan, DEFAULT_MAP, C.api_root(), token)
    C.write_private(C.work_file("acquired.json"),
                    json.dumps({"archive": archive, "payload": payload},
                               ensure_ascii=False).encode("utf-8"))
    C.write_output("payload-version", payload["toolVersion"])
    C.write_output("payload-sha256", payload["sha256"])


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:
        try:
            fail_acquire("internal", "exception")
        except Exception:
            raise SystemExit(1)
