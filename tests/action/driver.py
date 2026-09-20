#!/usr/bin/env python3
"""driver.py — test-only launcher for scripts/action internals.

The production helpers deliberately have no argv override surface (their map
path and API root are fixed/gated). Tests inject alternates programmatically
through this file — importing the production modules and supplying map paths,
API roots, plans and payload dicts directly. Nothing here ships in the
Action; production behavior is unreachable from it.

Usage:
  driver.py init <workdir> [--install-root DIR] [--dotnet PATH] [--cwd DIR]
  driver.py acquire <workdir> <map.json> <api-root>
  driver.py install <workdir>
  driver.py install-archive <workdir> <archive> <sha256> <toolVersion>
  driver.py invoke <workdir>            # subprocess, real entry semantics
  driver.py prepare                     # subprocess prepare.py with env
"""

import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ACTION = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "action"))
sys.path.insert(0, ACTION)
sys.dont_write_bytecode = True

import common as C  # noqa: E402


def _init(workdir, install_root=None, dotnet=None, cwd=None):
    os.makedirs(workdir, mode=0o700, exist_ok=True)
    os.chmod(workdir, 0o700)
    plan = {
        "operation": "start",
        "repositoryRoot": cwd or workdir,
        "input": "x.slnx", "policy": "policy.json",
        "request": os.path.join(workdir, "input-request.json"),
        "configuration": None, "configurationOverride": None,
        "dotnet": dotnet or "dotnet",
        "cwd": cwd or workdir,
        "installRoot": install_root or os.path.join(workdir, "install"),
    }
    with open(os.path.join(workdir, "plan.json"), "w", encoding="utf-8") as fh:
        json.dump(plan, fh)
    if not os.path.exists(plan["request"]):
        with open(plan["request"], "w", encoding="utf-8") as fh:
            fh.write("{}")


def _enter(workdir):
    os.environ["CS_WORK_DIR"] = workdir
    os.environ.setdefault("GITHUB_OUTPUT",
                          os.path.join(workdir, "step-outputs.txt"))
    os.environ.setdefault("CONTRACTSCRIBE_ACTION_TEST", "1")


def cmd_init(argv):
    workdir = argv[0]
    opts = dict(zip(argv[1::2], argv[2::2]))
    _init(workdir, install_root=opts.get("--install-root"),
          dotnet=opts.get("--dotnet"), cwd=opts.get("--cwd"))


def cmd_acquire(argv):
    import acquire
    workdir, map_path, api_root = argv
    _enter(workdir)
    plan = C.load_json_file(os.path.join(workdir, "plan.json"),
                            C.BOUND_METADATA_JSON, "plan")
    token = os.environ.get("CONTRACTSCRIBE_ACQUISITION_TOKEN") or None
    archive, payload = acquire.acquire_archive(plan, map_path, api_root, token)
    with open(os.path.join(workdir, "acquired.json"), "w",
              encoding="utf-8") as fh:
        json.dump({"archive": archive, "payload": payload}, fh)
    C.write_output("payload-version", payload["toolVersion"])
    C.write_output("payload-sha256", payload["sha256"])


def cmd_install(argv):
    import install
    workdir = argv[0]
    _enter(workdir)
    plan = C.load_json_file(os.path.join(workdir, "plan.json"),
                            C.BOUND_METADATA_JSON, "plan")
    acquired = C.load_json_file(os.path.join(workdir, "acquired.json"),
                                C.BOUND_METADATA_JSON, "acquired")
    dest = install.install_from_verified(plan, acquired["archive"],
                                         acquired["payload"])
    C.write_output("install-dir", dest)


def cmd_install_archive(argv):
    import install
    workdir, archive, sha, version = argv
    _enter(workdir)
    plan = C.load_json_file(os.path.join(workdir, "plan.json"),
                            C.BOUND_METADATA_JSON, "plan")
    payload = {"sha256": sha, "toolVersion": version}
    dest = install.install_from_verified(plan, archive, payload)
    C.write_output("install-dir", dest)


def cmd_invoke(argv):
    workdir = argv[0]
    _enter(workdir)
    sys.exit(subprocess.call(
        [sys.executable, os.path.join(ACTION, "invoke.py")],
        env=os.environ.copy()))


def cmd_prepare(argv):
    env = dict(os.environ)
    for pair in argv:
        key, _, value = pair.partition("=")
        env[key] = value
    sys.exit(subprocess.call(
        [sys.executable, os.path.join(ACTION, "prepare.py")], env=env))


def main():
    command = sys.argv[1]
    {"init": cmd_init, "acquire": cmd_acquire, "install": cmd_install,
     "install-archive": cmd_install_archive, "invoke": cmd_invoke,
     "prepare": cmd_prepare}[command](sys.argv[2:])


if __name__ == "__main__":
    main()
