#!/usr/bin/env python3
"""payload_extract.py — inspect and safely extract the D2 payload archive.

Single implementation of the archive member policy used by the install
contract (issue #18, M6-A1). tar -tvzf cannot express this policy: GNU and
PAX sparse members print as ordinary regular files, and link/device/member
metadata distinctions are lost in the human-readable listing. This helper
inspects real member metadata (name, type, sparse/PAX extension flags) and
extracts only validated regular files and directories.

Usage:
    payload_extract.py scan    <archive> [--top <expected-top-dir>]
    payload_extract.py extract <archive> <dest-dir> [--top <expected-top-dir>]

scan prints a JSON report:
    {"ok": bool, "errors": [...], "top": "<top>", "expandedBytes": N,
     "members": [{"name","kind","size","mode"} ...]}

extract validates the same policy, then writes members beneath <dest-dir>.
Both commands exit 0 only when the archive satisfies the whole policy.
"""

import json
import os
import stat
import sys
import tarfile

BOUND_COMPRESSED = 256 * 1024 * 1024
BOUND_EXPANDED = 512 * 1024 * 1024
BOUND_FILE = 128 * 1024 * 1024
BOUND_COUNT = 4096
BOUND_PATH = 1024
BOUND_DEPTH = 16
# Cumulative decoded PAX/GNU extension metadata (key+value bytes). A
# legitimate payload carries none; this bound stops metadata amplification
# before member-policy evaluation continues.
BOUND_METADATA = 4 * 1024 * 1024

REGULAR_TYPES = (tarfile.REGTYPE, tarfile.AREGTYPE)
ACCEPTED_TYPES = REGULAR_TYPES + (tarfile.DIRTYPE,)


def _canonical(name):
    """Return the canonical member path or None when it is unsafe."""
    if not name or name.startswith("/") or "\\" in name or "\x00" in name:
        return None
    if len(name) > 1 and name[1] == ":":
        return None
    parts = name.split("/")
    if any(part in ("", ".", "..") for part in parts):
        return None
    if len(name.encode("utf-8")) > BOUND_PATH or len(parts) > BOUND_DEPTH:
        return None
    return "/".join(parts)


def _inspect(path, expected_top):
    errors = []
    members = []
    top = expected_top
    expanded = 0
    seen = set()
    file_paths = set()
    metadata_bytes = 0

    try:
        compressed = os.path.getsize(path)
    except OSError as exc:
        return {"ok": False, "errors": [f"archive unreadable: {exc}"],
                "top": top, "expandedBytes": 0, "members": []}
    if compressed > BOUND_COMPRESSED:
        return {"ok": False,
                "errors": [f"compressed size {compressed} exceeds bound {BOUND_COMPRESSED}"],
                "top": top, "expandedBytes": 0, "members": []}

    try:
        archive = tarfile.open(path, "r:gz")
    except (tarfile.TarError, OSError, EOFError) as exc:
        return {"ok": False, "errors": [f"archive not a readable gzip tar: {exc}"],
                "top": top, "expandedBytes": 0, "members": []}

    # Iterate members incrementally — never materialize the whole list — and
    # stop at every consumer-owned bound rather than consuming the input
    # first and reporting afterwards.
    with archive:
        member = archive.next()
        while member is not None:
            canonical = _canonical(member.name)
            if canonical is None:
                errors.append(f"unsafe member name: {member.name!r}")
                break
            parts = canonical.split("/")
            if top is None:
                top = parts[0]
            if parts[0] != top:
                errors.append(f"member outside top directory: {canonical}")
                break
            if canonical == top and member.type != tarfile.DIRTYPE:
                errors.append(f"top directory member is not a directory: {canonical}")
                break
            if len(parts) < 2 and canonical != top:
                errors.append(f"member at top level but not the top dir: {canonical}")
                break
            if canonical in seen:
                errors.append(f"duplicate member: {canonical}")
                break
            if member.type not in ACCEPTED_TYPES:
                errors.append(
                    f"rejected member type {member.type!r}: {canonical}")
                break
            if member.sparse is not None or member.type == b"S":
                errors.append(f"sparse member rejected: {canonical}")
                break
            pax = member.pax_headers or {}
            if any(k.lower().startswith("gnu.sparse") for k in pax):
                errors.append(f"sparse (pax) member rejected: {canonical}")
                break
            metadata_bytes += sum(len(k) + len(v) for k, v in pax.items())
            if metadata_bytes > BOUND_METADATA:
                errors.append(
                    f"extension metadata exceeds bound {BOUND_METADATA}: {canonical}")
                break
            kind = "dir" if member.type == tarfile.DIRTYPE else "file"
            if kind == "file":
                if member.size > BOUND_FILE:
                    errors.append(
                        f"member exceeds file bound {BOUND_FILE}: {canonical}")
                    break
                expanded += member.size
                if expanded > BOUND_EXPANDED:
                    errors.append(
                        f"expanded size exceeds bound {BOUND_EXPANDED}: {canonical}")
                    break
                file_paths.add(canonical)
            seen.add(canonical)
            if len(seen) > BOUND_COUNT:
                errors.append(f"member count exceeds bound {BOUND_COUNT}")
                break
            members.append({
                "name": canonical,
                "kind": kind,
                "size": member.size if kind == "file" else 0,
                "mode": "0755" if (member.mode & 0o111) else "0644",
            })
            member = archive.next()

    if not errors:
        for canonical in sorted(seen):
            ancestor = canonical
            while "/" in ancestor:
                ancestor = ancestor.rsplit("/", 1)[0]
                if ancestor in file_paths:
                    errors.append(f"path under a file member: {canonical}")
                    break
        if top is None:
            errors.append("archive contains no members")

    return {"ok": not errors, "errors": errors, "top": top,
            "expandedBytes": expanded, "members": members}


def _extract(path, dest, expected_top):
    report = _inspect(path, expected_top)
    if not report["ok"]:
        return report
    top = report["top"]
    if os.name == "nt":
        # Member names are deep enough to exceed the legacy 260-char limit
        # on Windows development hosts; extended paths keep local runs honest.
        dest = "\\\\?\\" + os.path.abspath(dest)
    try:
        archive = tarfile.open(path, "r:gz")
    except (tarfile.TarError, OSError, EOFError) as exc:
        return {"ok": False, "errors": [f"archive not a readable gzip tar: {exc}"],
                "top": top, "expandedBytes": 0, "members": []}
    with archive:
        for member in archive.getmembers():
            canonical = _canonical(member.name)
            if canonical is None:
                continue  # already rejected during scan; unreachable
            target = os.path.join(dest, *canonical.split("/"))
            if member.type == tarfile.DIRTYPE:
                os.makedirs(target, exist_ok=True)
                os.chmod(target, 0o755)
            else:
                os.makedirs(os.path.dirname(target), exist_ok=True)
                source = archive.extractfile(member)
                if source is None:
                    return {"ok": False,
                            "errors": [f"cannot read member: {canonical}"],
                            "top": top, "expandedBytes": 0, "members": []}
                remaining = member.size
                with open(target, "wb") as out:
                    while remaining > 0:
                        chunk = source.read(min(1 << 20, remaining))
                        if not chunk:
                            return {"ok": False,
                                    "errors": [f"truncated member: {canonical}"],
                                    "top": top, "expandedBytes": 0,
                                    "members": []}
                        out.write(chunk)
                        remaining -= len(chunk)
                os.chmod(target, 0o755 if (member.mode & 0o111) else 0o644)
    return report


def main(argv):
    expected_top = None
    positional = []
    i = 1
    while i < len(argv):
        if argv[i] == "--top":
            i += 1
            if i >= len(argv):
                print(__doc__, file=sys.stderr)
                return 2
            expected_top = argv[i]
        else:
            positional.append(argv[i])
        i += 1
    if len(positional) < 2:
        print(__doc__, file=sys.stderr)
        return 2
    command, archive = positional[0], positional[1]
    if command == "scan" and len(positional) == 2:
        report = _inspect(archive, expected_top)
        json.dump(report, sys.stdout, indent=2)
        sys.stdout.write("\n")
    elif command == "extract" and len(positional) == 3:
        report = _extract(archive, positional[2], expected_top)
        if not report["ok"]:
            for error in report["errors"]:
                print(f"payload_extract: {error}", file=sys.stderr)
    else:
        print(__doc__, file=sys.stderr)
        return 2
    return 0 if report["ok"] else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
