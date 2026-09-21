#!/usr/bin/env bash
# verify-examples.sh — local validation for the caller-owned campaign
# workflow example (issue #186 / M6-A3). POSIX runner assumed (CI: ubuntu);
# legs that cannot run elsewhere are skipped rather than faked.
#
#   1. python syntax checks for every test/helper artifact
#   2. YAML-aware structural validation of the example workflow (PyYAML
#      6.0.3 in an isolated venv — the pinned test-dependency precedent)
#   3. the functional leg matrix via driver.py against fake_actions_api.py
#   4. textual invariants that parsing cannot express
#
# Hosted transport/Action coverage lives in ci.yml's action_examples_* jobs;
# this script does not need dotnet, credentials, or network beyond the
# PyYAML install.

set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
EXAMPLE="$REPO/examples/github-actions/contract-scribe-campaign.yml"
HELPER="$REPO/examples/github-actions/handoff.py"
WORK="${WORK:-$(mktemp -d)}"
mkdir -p "$WORK"
ONLY="${ONLY:-}"

echo "== syntax =="
python3 -m py_compile \
    "$HELPER" \
    "$HERE/fake_actions_api.py" \
    "$HERE/fake_github_state.py" \
    "$HERE/check_workflow.py" \
    "$HERE/driver.py"
echo "py_compile ok"

echo "== workflow structure =="
VENV="$WORK/venv"
python3 -m venv "$VENV"
# shellcheck disable=SC1091
if [ -f "$VENV/bin/activate" ]; then . "$VENV/bin/activate"; else . "$VENV/Scripts/activate"; fi
python -m pip install --quiet PyYAML==6.0.3
python "$HERE/check_workflow.py" "$EXAMPLE"
deactivate

echo "== textual invariants =="
# The three Action credentials are exactly the documented secrets.
for name in CONTRACTSCRIBE_GITHUB_TOKEN CONTRACTSCRIBE_PROVIDER_API_KEY \
    CONTRACTSCRIBE_ACQUISITION_TOKEN; do
    grep -q "secrets.$name" "$EXAMPLE" || {
        echo "missing credential input $name"; exit 1; }
done
# No secrets anywhere but inside `with:` blocks (which the YAML checker
# confines to the Action steps).
python3 - "$EXAMPLE" <<'PYEOF'
import sys
text = open(sys.argv[1], encoding="utf-8").read().splitlines()
in_with = False
with_depth = 0
bad = []
for i, line in enumerate(text, 1):
    stripped = line.strip()
    indent = len(line) - len(line.lstrip())
    if "secrets." in line and not in_with and not stripped.startswith("#"):
        bad.append(i)
    if stripped == "with:":
        in_with, with_depth = True, indent
    elif in_with and stripped and indent <= with_depth:
        in_with = False
if bad:
    print(f"secrets outside with: at lines {bad}")
    sys.exit(1)
PYEOF
# No checkpoint bytes ever surfaced: no `cat`/`echo` of the state file and
# no secrets-style interpolation of state content.
! grep -n 'cat.*checkpoint\.json\|echo.*checkpoint' "$EXAMPLE"
# The caller activation and campaign claims come only from vars.
grep -q 'vars.CONTRACTSCRIBE_ACTIVATION' "$EXAMPLE"
grep -q 'vars.CONTRACTSCRIBE_CAMPAIGN' "$EXAMPLE"
# Ambient token is github.token only — never a secret.
! grep -n 'GITHUB_TOKEN:.*secrets\.' "$EXAMPLE"
# No caches, no glob/latest artifact acquisition, no retries.
! grep -n 'actions/cache\|merge-multiple\|retry\|name: \*' "$EXAMPLE"
echo "textual invariants ok"

echo "== functional legs =="
if [ -n "$ONLY" ]; then
    HANDOFF_PY="$HELPER" FAKE_API_PY="$HERE/fake_actions_api.py" \
        python3 "$HERE/driver.py" run "$WORK/legs" --only "$ONLY"
else
    HANDOFF_PY="$HELPER" FAKE_API_PY="$HERE/fake_actions_api.py" \
        python3 "$HERE/driver.py" run "$WORK/legs"
fi

echo "verify-examples: ok"
