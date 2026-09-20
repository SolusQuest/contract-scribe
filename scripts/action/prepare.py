#!/usr/bin/env python3
"""prepare.py — validate Action host inputs and materialize private files.

Runs as the step entry process (action.yml execs it). Validates only
host-owned concerns: operation name, required input presence, transport
bounds/encoding, runner support, .NET prerequisites, private file
materialization, and test-seam legality. Product-level validation
(repository/input/policy/request semantics) is the CLI's contract — this
wrapper passes values as data and never classifies product input.

Inputs arrive as CS_INPUT_* environment variables (mapped by action.yml);
caller bytes never pass through argv or $GITHUB_ENV.
"""

import os
import platform
import shutil
import sys
import uuid

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C

OPERATIONS = ("github-proposal-start", "github-proposal-resume")

PATH_INPUTS = ("repository-root", "input", "policy", "configuration")
OBJECT_INPUTS = ("request", "configuration-override")


def fail_prepare(reason):
    C.marker("action-prepare", "stage=validate", "fail", reason)
    C.write_output("action-status", "action.prepare-" + reason)
    raise SystemExit(1)


def check(condition, reason):
    if not condition:
        fail_prepare(reason)


def dotnet_prereq(dotnet):
    """Probe the resolved dotnet host; the same absolute path is invoked later."""
    try:
        _, out_b, _ = C.run_owned([dotnet, "--list-runtimes"], timeout=30)
        runtimes = out_b.decode("utf-8", "replace")
    except OSError:
        fail_prepare("dotnet-probe")
    if not any(line.startswith("Microsoft.NETCore.App 10.")
               for line in runtimes.splitlines()):
        fail_prepare("runtime-missing")
    try:
        _, out_b, _ = C.run_owned([dotnet, "--list-sdks"], timeout=30)
        sdks = out_b.decode("utf-8", "replace")
    except OSError:
        fail_prepare("dotnet-probe")
    if not sdks.strip():
        # github-proposal is a semantic operation: BuildHost/MSBuild need an SDK.
        fail_prepare("sdk-missing")
    return dotnet


def materialize(name, value):
    if len(value.encode("utf-8")) > C.BOUND_INLINE_OBJECT:
        fail_prepare(f"{name}-oversize")
    path = C.work_file("input-" + name + ".json")
    C.write_private(path, value.encode("utf-8"))
    return path


def main():
    os.environ["CS_ACTION_STAGE"] = "prepare"
    # Validate the test-seam gate before anything else can act on it.
    C.test_api_root()

    runner_os = os.environ.get("RUNNER_OS") or platform.system()
    runner_arch = os.environ.get("RUNNER_ARCH") or platform.machine()
    if runner_os != "Linux":
        fail_prepare("unsupported-runner-os")
    if runner_arch not in ("X64", "x86_64", "AMD64"):
        fail_prepare("unsupported-runner-arch")

    operation = C.env_input("CS_INPUT_OPERATION")
    check(operation in OPERATIONS, "operation")

    values = {}
    for name in ("repository-root", "input", "policy", "request"):
        value = C.env_input("CS_INPUT_" + name.replace("-", "_").upper())
        check(value is not None, f"missing-{name}")
        values[name] = value
    for name in ("configuration", "configuration-override"):
        values[name] = C.env_input("CS_INPUT_" + name.replace("-", "_").upper())

    for name in PATH_INPUTS:
        value = values.get(name)
        if value is not None:
            check(len(value) <= C.BOUND_PATH_INPUT, f"{name}-oversize")
            check("\x00" not in value, f"{name}-nul")

    work = os.path.join(
        os.environ.get("RUNNER_TEMP") or "/tmp",
        "contract-scribe-action-" + uuid.uuid4().hex)
    C.private_dir(work)
    C.write_env("CS_WORK_DIR", work)
    os.environ["CS_WORK_DIR"] = work

    values["request"] = materialize("request", values["request"])
    if values["configuration-override"] is not None:
        values["configuration-override"] = materialize(
            "configuration-override", values["configuration-override"])

    dotnet = shutil.which("dotnet")
    if not dotnet:
        fail_prepare("dotnet-missing")
    dotnet_prereq(dotnet)

    workspace = os.environ.get("GITHUB_WORKSPACE") or os.getcwd()
    repository_root = values["repository-root"]
    if not os.path.isabs(repository_root):
        repository_root = os.path.join(workspace, repository_root)

    install_root = os.path.join(
        os.environ.get("RUNNER_TEMP") or "/tmp", "contract-scribe-action", "payloads")
    os.makedirs(install_root, exist_ok=True)

    plan = {
        "operation": "start" if operation == "github-proposal-start" else "resume",
        "repositoryRoot": repository_root,
        "input": values["input"],
        "policy": values["policy"],
        "request": values["request"],
        "configuration": values["configuration"],
        "configurationOverride": values["configuration-override"],
        "dotnet": dotnet,
        "cwd": workspace,
        "installRoot": install_root,
    }
    import json
    C.write_private(C.work_file("plan.json"),
                    json.dumps(plan, ensure_ascii=False).encode("utf-8"))
    C.marker("action-prepare", "stage=validate", "ok")
    C.write_output("action-status", "ok")


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:
        try:
            fail_prepare("exception")
        except Exception:
            raise SystemExit(1)
