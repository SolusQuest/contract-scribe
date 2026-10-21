#!/usr/bin/env bash
# Offline helper and structural validation; packaged product/remote persistence
# coverage runs in the hosted action_examples jobs.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
WORK="${WORK:-$(mktemp -d)}"
mkdir -p "$WORK"
python3 -m py_compile "$REPO/examples/github-actions/caller.py" "$HERE/driver.py" \
  "$HERE/check_workflow.py" "$HERE/fake_actions_api.py" "$HERE/fake_github_state.py"
python3 -m venv "$WORK/venv"
"$WORK/venv/bin/python" -m pip install --quiet PyYAML==6.0.3
"$WORK/venv/bin/python" "$HERE/check_workflow.py" "$REPO/examples/github-actions/contract-scribe-campaign.yml"
"$WORK/venv/bin/python" - "$REPO/examples/github-actions/contract-scribe-campaign.yml" "$HERE/check_workflow.py" "$WORK" <<'PY'
import copy, pathlib, subprocess, sys, yaml
source, checker, work = sys.argv[1:]
doc = yaml.safe_load(pathlib.Path(source).read_text())
mutations = [lambda d: d[True].update(pull_request={}),
             lambda d: d['jobs']['invoke']['permissions'].update(contents='read'),
             lambda d: d['jobs']['invoke']['steps'][0]['with'].update({'persist-credentials': True}),
             lambda d: d['env'].update(CS_OPERATION='auto'),
             lambda d: d['jobs']['invoke']['env'].update(CS_STATE_DIR='${{ runner.temp }}/state')]
for i, mutate in enumerate(mutations):
    changed = copy.deepcopy(doc)
    changed['jobs']['invoke'].setdefault('env', {})
    mutate(changed)
    path = pathlib.Path(work) / f'invalid-{i}.yml'
    path.write_text(yaml.safe_dump(changed))
    result = subprocess.run([sys.executable, checker, str(path)], capture_output=True)
    assert result.returncode != 0, i
optional = copy.deepcopy(doc)
optional[True]['schedule'] = [{'cron': '*/30 * * * *'}]
path = pathlib.Path(work) / 'optional-schedule.yml'
path.write_text(yaml.safe_dump(optional))
subprocess.run([sys.executable, checker, str(path)], check=True)
print('workflow negative cases: 5 passed; optional schedule passed')
PY
python3 "$HERE/driver.py" run "$WORK/legs"
echo 'verify-examples: ok'
