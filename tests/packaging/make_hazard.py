#!/usr/bin/env python3
"""make_hazard.py — build structurally hazardous payload archives.

    make_hazard.py <out.tar.gz> hazard:<name>

Every archive has an otherwise-plausible shape — a single top directory
(contract-scribe-9.9.9-hazard-linux-x64), a payload.json, and one regular
payload member — so the only violation is the hazard itself. The caller
recomputes the real sha256: rejection must come from the member policy,
never from a digest mismatch.
"""

import io
import sys
import tarfile

TOP = "contract-scribe-9.9.9-hazard-linux-x64"


def _file(name, data=b"x"):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    return info, io.BytesIO(data)


def _dir(name):
    info = tarfile.TarInfo(name)
    info.type = tarfile.DIRTYPE
    return info


def _base(archive):
    archive.addfile(_dir(TOP))
    archive.addfile(_dir(f"{TOP}/payload"))
    info, data = _file(f"{TOP}/payload/ContractScribe.Cli.dll", b"MZ")
    archive.addfile(info, data)
    info, data = _file(f"{TOP}/payload.json", b"{}")
    archive.addfile(info, data)


def _add(archive, info, data=None):
    archive.addfile(info, data)


def main(argv):
    if len(argv) != 3 or not argv[2].startswith("hazard:"):
        print(__doc__, file=sys.stderr)
        return 2
    out, hazard = argv[1], argv[2].split(":", 1)[1]

    # PAX format is required for pax_headers to be written; GNU format is
    # required for the GNUTYPE_SPARSE member type.
    fmt = tarfile.PAX_FORMAT if hazard in ("paxsparse", "paxbomb") else tarfile.GNU_FORMAT
    with tarfile.open(out, "w:gz", format=fmt) as archive:
        _base(archive)
        if hazard == "traversal":
            info, data = _file(f"{TOP}/../../outside/evil.txt")
            _add(archive, info, data)
        elif hazard == "absolute":
            info, data = _file(f"/{TOP}/payload/evil.txt")
            _add(archive, info, data)
        elif hazard == "dotdot":
            info, data = _file(f"{TOP}/payload/../evil.txt")
            _add(archive, info, data)
        elif hazard == "symlink":
            info = tarfile.TarInfo(f"{TOP}/payload/link")
            info.type = tarfile.SYMTYPE
            info.linkname = "/etc/passwd"
            archive.addfile(info)
        elif hazard == "hardlink":
            info = tarfile.TarInfo(f"{TOP}/payload/hardlink")
            info.type = tarfile.LNKTYPE
            info.linkname = f"{TOP}/payload/ContractScribe.Cli.dll"
            archive.addfile(info)
        elif hazard == "device":
            info = tarfile.TarInfo(f"{TOP}/payload/null")
            info.type = tarfile.CHRTYPE
            info.devmajor = 1
            info.devminor = 3
            archive.addfile(info)
        elif hazard == "fifo":
            info = tarfile.TarInfo(f"{TOP}/payload/pipe")
            info.type = tarfile.FIFOTYPE
            archive.addfile(info)
        elif hazard == "sparse":
            info = tarfile.TarInfo(f"{TOP}/payload/sparse.bin")
            info.type = tarfile.GNUTYPE_SPARSE
            info.size = 65536
            info.sparse = [(0, 65536)]
            archive.addfile(info, io.BytesIO(b"\x00" * 65536))
        elif hazard == "paxsparse":
            info = tarfile.TarInfo(f"{TOP}/payload/sparse-pax.bin")
            info.size = 1
            info.pax_headers = {
                "GNU.sparse.major": "1",
                "GNU.sparse.minor": "0",
                "GNU.sparse.size": "65536",
            }
            archive.addfile(info, io.BytesIO(b"\x00"))
        elif hazard == "duplicate":
            info, data = _file(f"{TOP}/payload/dup.txt", b"one")
            _add(archive, info, data)
            info, data = _file(f"{TOP}/payload/dup.txt", b"two")
            _add(archive, info, data)
        elif hazard == "collision":
            info, data = _file(f"{TOP}/blocked", b"file")
            _add(archive, info, data)
            info, data = _file(f"{TOP}/blocked/child.txt", b"child")
            _add(archive, info, data)
        elif hazard == "secondtop":
            info, data = _file("other-root/payload/evil.txt")
            _add(archive, info, data)
        elif hazard == "paxbomb":
            # PAX extension body far beyond the metadata bound — must reject
            # without the body ever being decoded/allocated.
            info, data = _file(f"{TOP}/payload/padded.txt")
            info.pax_headers = {"x" * 200: "y" * (5 * 1024 * 1024)}
            _add(archive, info, data)
        elif hazard == "longnamebomb":
            # GNU longname continuation beyond the metadata bound — the raw
            # size field alone must trigger rejection before any body read.
            long_top = "contract-scribe-9.9.9-hazard-linux-x64"
            info = tarfile.TarInfo(long_top + "/" + "n" * 200)
            info.type = tarfile.GNUTYPE_LONGNAME
            info.name = "././@LongLink"
            info.size = 5 * 1024 * 1024
            archive.addfile(info, io.BytesIO(b"n" * (5 * 1024 * 1024)))
        else:
            print(f"make_hazard: unknown hazard {hazard}", file=sys.stderr)
            return 2
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
