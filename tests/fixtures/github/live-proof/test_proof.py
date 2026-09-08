import contextlib
import copy
import datetime as dt
import fnmatch
import io
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

import proof
from proof_contract import *


def inputs():
    config = {'product_sha': PRODUCT, 'source_sha': 'a' * 40, 'workflow_sha': 'b' * 40, 'base_sha': 'b' * 40,
              'workflow_digest': sha((proof.SOURCE / WORKFLOW).read_bytes()), 'workflow_id': 123,
              'activation': 'c' * 32, 'not_before': '2026-09-09T01:00:00Z', 'not_after': '2026-09-09T03:00:00Z',
              'manual_run_number': 7, 'scheduled_run_number': 8, 'actor_id': 16307884, 'actor_login': 'Yuee98',
              'owner': 'Yuee98', 'issue': 166, 'scenarios': ['stale', 'published', 'replayed']}
    ctx = {'repository': REPOSITORY, 'repository_id': REPOSITORY_ID, 'event': 'workflow_dispatch',
           'ref': 'refs/heads/main', 'cron': '', 'workflow_sha': config['workflow_sha'], 'sha': config['base_sha'],
           'run_id': 100, 'run_number': 7, 'attempt': 1}
    repository = {'id': REPOSITORY_ID, 'node_id': REPOSITORY_NODE, 'full_name': REPOSITORY,
                  'private': False, 'archived': False, 'default_branch': 'main', 'topics': ['m5-h3-' + config['activation']]}
    run = {'id': 100, 'workflow_id': 123, 'path': WORKFLOW, 'head_sha': config['base_sha'],
           'event': 'workflow_dispatch', 'repository': {'id': REPOSITORY_ID}, 'run_attempt': 1, 'run_number': 7,
           'created_at': '2026-09-09T01:10:00Z', 'actor': {'id': 16307884, 'login': 'Yuee98'},
           'triggering_actor': {'id': 16307884, 'login': 'Yuee98'}}
    return config, ctx, repository, run


def zip_bytes(content=b'{}', name='checkpoint.json', mode=stat.S_IFREG | 0o600, extra=False):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w') as archive:
        info = zipfile.ZipInfo(name)
        info.external_attr = mode << 16
        archive.writestr(info, content)
        if extra:
            archive.writestr('extra.json', b'{}')
    return stream.getvalue()


class GateTests(unittest.TestCase):
    def gate(self, config, ctx, repository, run, topics=None, now=None):
        return role_gate(config, ctx, repository, run, repository['topics'] if topics is None else topics,
                         now or timestamp('2026-09-09T02:00:00Z'))

    def test_roles_and_inactive_after_consumed_slot_and_expiry(self):
        config, ctx, repository, run = inputs()
        self.assertEqual('manual', self.gate(config, ctx, repository, run))
        ctx.update(event='schedule', cron=CRON, run_number=8)
        run.update(event='schedule', run_number=8)
        self.assertEqual('scheduled', self.gate(config, ctx, repository, run))
        ctx['run_number'] = run['run_number'] = 9
        self.assertEqual('inactive', self.gate(config, ctx, repository, run, topics=[], now=timestamp('2026-09-10T02:00:00Z')))
        with self.assertRaisesRegex(ProofFailure, 'run-slot'):
            self.gate(config, ctx, repository, run)

    def test_current_identity_failures_reject_even_with_old_gate_outputs(self):
        cases = [
            ('context-repository', 1, 'repository', 'SolusQuest/contract-scribe'),
            ('context-repository', 1, 'repository_id', 1),
            ('event-or-ref', 1, 'ref', 'refs/heads/alternate'),
            ('event-or-ref', 1, 'event', 'pull_request'),
            ('executing-revision', 1, 'workflow_sha', 'd' * 40),
            ('executing-revision', 1, 'sha', 'd' * 40),
            ('run-identity', 3, 'id', 101),
            ('run-identity', 3, 'workflow_id', 124),
            ('run-attempt', 3, 'run_attempt', 2),
            ('run-attempt', 1, 'attempt', 2),
            ('run-slot', 3, 'run_number', 8),
            ('repository-identity', 2, 'private', True),
            ('repository-identity', 2, 'default_branch', 'other'),
        ]
        for code, index, key, value in cases:
            with self.subTest(code=code, key=key):
                values = list(inputs())
                values[index][key] = value
                with self.assertRaisesRegex(ProofFailure, code):
                    self.gate(*values)
        config, ctx, repository, run = inputs()
        run['triggering_actor'] = {'id': 999, 'login': 'other'}
        with self.assertRaisesRegex(ProofFailure, 'run-actor'):
            self.gate(config, ctx, repository, run)

    def test_window_cron_and_ambiguous_activation(self):
        config, ctx, repository, run = inputs()
        for now in ('2026-09-09T00:59:59Z', '2026-09-09T03:00:00Z'):
            with self.subTest(now=now), self.assertRaisesRegex(ProofFailure, 'activation-window'):
                self.gate(config, ctx, repository, run, now=timestamp(now))
        with self.assertRaisesRegex(ProofFailure, 'activation-topic'):
            self.gate(config, ctx, repository, run, topics=repository['topics'] + ['m5-h3-other'])
        ctx.update(event='schedule', cron='0 * * * *')
        with self.assertRaisesRegex(ProofFailure, 'schedule-cron'):
            self.gate(config, ctx, repository, run)

    def test_configuration_has_closed_fields_and_finite_scope(self):
        config = inputs()[0]
        self.assertEqual(config, configuration(canonical(config).decode()))
        for key, value in [('extra', 'unexpected'), ('manual_run_number', True), ('scheduled_run_number', 10),
                           ('product_sha', 'd' * 40), ('base_sha', BOOTSTRAP), ('activation', '../bad'),
                           ('not_after', '2026-09-10T01:00:00Z'), ('scenarios', ['published'])]:
            changed = dict(config, **{key: value})
            with self.subTest(key=key), self.assertRaises(ProofFailure):
                configuration(canonical(changed).decode())
        with self.assertRaisesRegex(ProofFailure, 'duplicate-property'):
            parse_json('{"a":1,"a":2}')

    def test_current_workflow_is_permission_source_and_filters_precede_jobs(self):
        config = inputs()[0]
        data = (proof.SOURCE / WORKFLOW).read_bytes()
        document = validate_workflow(data, config)
        patterns = document['on']['pull_request']['paths']
        eligible = lambda path: any(fnmatch.fnmatchcase(path, pattern) for pattern in patterns)
        self.assertFalse(eligible('Synthetic.cs'))  # B001: no H3 run is created by its own proposal.
        for path in (WORKFLOW, 'docs/20_architecture/validation/m5-github-proposal-proof.md',
                     'tests/fixtures/github/live-proof/test_proof.py'):
            self.assertTrue(eligible(path))
        self.assertIn("github.event_name == 'pull_request'", document['jobs']['offline']['if'])
        self.assertNotIn('pull_request_target', document['on'])
        self.assertNotIn('workflow_call', document['on'])
        self.assertNotIn('pull_request', document['jobs']['preflight']['if'])
        changed = data.replace(b'      pull-requests: write', b'      issues: write')
        changed_config = dict(config, workflow_digest=sha(changed))
        with self.assertRaisesRegex(ProofFailure, 'product-permissions'):
            validate_workflow(changed, changed_config)
        with self.assertRaisesRegex(ProofFailure, 'duplicate-property'):
            workflow_document(b'jobs: {}\njobs: {}\n')

    def test_actual_workflow_token_attachments_and_native_transfer(self):
        document = workflow_document((proof.SOURCE / WORKFLOW).read_bytes())
        attachments = []
        for job_name, job in document['jobs'].items():
            self.assertNotIn('CONTRACTSCRIBE_GITHUB_TOKEN', job.get('env', {}))
            for step in job['steps']:
                self.assertNotIn('continue-on-error', step)
                if 'CONTRACTSCRIBE_GITHUB_TOKEN' in step.get('env', {}):
                    attachments.append((job_name, step['run']))
                if step.get('uses', '').startswith('actions/download-artifact@'):
                    values = step['with']
                    self.assertNotIn('github-token', values)
                    self.assertNotIn('run-id', values)
                    self.assertEqual('true', values['skip-decompress'])
                    self.assertEqual('error', values['digest-mismatch'])
                    self.assertIn('relay_id', values['artifact-ids'])
                if step.get('uses', '').startswith('actions/upload-artifact@'):
                    self.assertEqual('true', step['with']['archive'])
                    self.assertEqual('false', step['with']['overwrite'])
                    self.assertEqual('1', step['with']['retention-days'])
                    self.assertTrue(step['with']['path'].endswith('/checkpoint.json'))
        self.assertEqual([('product', '"$M5_PYTHON" "$M5_DRIVER" invoke ' + scenario)
                          for scenario in ('negative', 'positive', 'replay')], attachments)
        self.assertNotIn('CONTRACTSCRIBE_GITHUB_TOKEN', document['env'])


class TransferTests(unittest.TestCase):
    def test_original_and_relay_archives_preserve_one_content_identity(self):
        content = b'{"safe":"synthetic"}'
        original = zip_bytes(content)
        relay = zip_bytes(content, mode=stat.S_IFREG | 0o644)
        self.assertNotEqual(sha(original), sha(relay))
        admitted = archive_checkpoint(original, sha(original))
        self.assertEqual(content, archive_checkpoint(relay, sha(relay), sha(admitted)))
        for archive, digest, content_digest in ((original, None, None), (original, '0' * 64, None),
                                                 (relay, sha(relay), '0' * 64)):
            with self.subTest(digest=digest), self.assertRaises(ProofFailure):
                archive_checkpoint(archive, digest, content_digest)

    def test_unsafe_and_oversized_archives_are_rejected_before_writing(self):
        for data in (zip_bytes(name='../checkpoint.json'), zip_bytes(mode=stat.S_IFLNK | 0o777),
                     zip_bytes(extra=True), zip_bytes(b'x' * (MAX_CHECKPOINT + 1)), zip_bytes(b'{"x":1,"x":2}'), b'not-zip'):
            with self.subTest(size=len(data)), self.assertRaises(ProofFailure):
                archive_checkpoint(data, sha(data))

    @unittest.skipUnless(sys.platform == 'linux', 'Linux private-store admission')
    def test_private_file_admission_is_exclusive_and_preserves_exact_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            checkpoint = proof.private_checkpoint(parent / 'state', b'{}')
            self.assertEqual(b'{}', checkpoint.read_bytes())
            self.assertEqual(0o600, stat.S_IMODE(checkpoint.stat().st_mode))
            self.assertEqual(0o700, stat.S_IMODE(checkpoint.parent.stat().st_mode))
            with self.assertRaises(FileExistsError):
                proof.private_checkpoint(parent / 'state', b'replacement')
            (parent / 'alias').symlink_to(checkpoint.parent, target_is_directory=True)
            with self.assertRaises(FileExistsError):
                proof.private_checkpoint(parent / 'alias', b'replacement')

    def test_relay_outputs_require_original_and_current_run_identity(self):
        config, ctx, _, _ = inputs()
        origin = {'producer_run': 90, 'producer_attempt': 1, 'artifact_id': 91, 'archive_digest': 'd' * 64,
                  'content_digest': 'e' * 64, 'config_digest': sha(canonical(config)), 'receiver_run': 100, 'receiver_attempt': 1}
        environment = {'M5_ORIGIN': canonical(origin).decode(), 'M5_RELAY_ID': '92', 'M5_RELAY_DIGEST': 'f' * 64}
        with mock.patch.dict(os.environ, environment):
            self.assertEqual(origin, proof.relay_inputs(config, ctx))
            for key, value in [('receiver_run', 101), ('receiver_attempt', 2), ('config_digest', '0' * 64), ('artifact_id', 0)]:
                with self.subTest(key=key), mock.patch.dict(os.environ, M5_ORIGIN=canonical(dict(origin, **{key: value})).decode()):
                    with self.assertRaises(ProofFailure):
                        proof.relay_inputs(config, ctx)
            for name in ('M5_RELAY_ID', 'M5_RELAY_DIGEST'):
                with mock.patch.dict(os.environ, {name: ''}), self.assertRaises(ProofFailure):
                    proof.relay_inputs(config, ctx)

    def test_producer_absent_ambiguous_failed_expired_or_substituted_is_rejected(self):
        config, ctx, repository, run = inputs()
        producer = dict(run, status='completed', conclusion='success')
        data = zip_bytes()
        artifact = {'id': 201, 'name': artifact_name(config), 'expired': False, 'size_in_bytes': len(data),
                    'digest': 'sha256:' + sha(data), 'expires_at': '2026-09-10T02:00:00Z',
                    'workflow_run': {'id': 100, 'repository_id': REPOSITORY_ID, 'head_repository_id': REPOSITORY_ID,
                                     'head_sha': config['base_sha']}}
        class FakeApi:
            def __init__(self):
                self.runs, self.artifacts = [producer], [artifact]
            def pages(self, suffix, key):
                return self.runs if key == 'workflow_runs' else self.artifacts
            def get(self, suffix):
                return repository if suffix == '' else producer if suffix.startswith('actions/runs') else artifact
            def request(self, *args, **kwargs):
                return data
        api = FakeApi()
        with mock.patch.object(proof, 'context', return_value=ctx):
            content, origin = proof.producer_artifact(config, api, timestamp('2026-09-09T02:00:00Z'))
            self.assertEqual(b'{}', content)
            self.assertEqual(201, origin['artifact_id'])
            for runs in ([], [producer, producer]):
                api.runs = runs
                with self.assertRaisesRegex(ProofFailure, 'producer-count'):
                    proof.producer_artifact(config, api, timestamp('2026-09-09T02:00:00Z'))
            api.runs = [producer]
            for key, bad in [('conclusion', 'failure'), ('run_attempt', 2), ('head_sha', 'd' * 40)]:
                original = producer[key]
                producer[key] = bad
                with self.subTest(key=key), self.assertRaises(ProofFailure):
                    proof.producer_artifact(config, api, timestamp('2026-09-09T02:00:00Z'))
                producer[key] = original
            for key, bad in [('expired', True), ('name', 'substituted'), ('digest', None), ('expires_at', '2026-09-09T01:00:00Z')]:
                original = artifact[key]
                artifact[key] = bad
                with self.subTest(key=key), self.assertRaises(ProofFailure):
                    proof.producer_artifact(config, api, timestamp('2026-09-09T02:00:00Z'))
                artifact[key] = original


class ResultTests(unittest.TestCase):
    def test_only_the_three_exact_publication_results_satisfy_scenarios(self):
        for scenario, exit_code, outcome, operation in [('negative', 4, 'stale', 'start'),
                                                        ('positive', 0, 'published', 'start'), ('replay', 0, 'replayed', 'resume')]:
            result = {'terminalLayer': 'publication', 'outcome': 'github-proposal.' + outcome,
                      'campaignOperation': operation, 'cliContractBaseline': PRODUCT}
            self.assertEqual(result, scenario_result(scenario, exit_code, canonical(result)))
            for key, value in [('outcome', 'github-proposal.no-op'), ('terminalLayer', 'campaign'),
                               ('campaignOperation', 'other'), ('cliContractBaseline', 'd' * 40)]:
                with self.subTest(scenario=scenario, key=key), self.assertRaises(ProofFailure):
                    scenario_result(scenario, exit_code, canonical(dict(result, **{key: value})))

    def test_issued_permissions_must_come_from_setup_group_and_match_exact_vector(self):
        prefix = '2026-09-09T01:10:00.1234567Z '
        log = '\n'.join(prefix + row for row in ['##[group]GITHUB_TOKEN Permissions', 'Contents: write',
                                                  'Metadata: read', 'PullRequests: write', '##[endgroup]'])
        bounds = ('2026-09-09T01:10:00Z', '2026-09-09T01:10:01Z')
        self.assertEqual({'contents': 'write', 'metadata': 'read', 'pullrequests': 'write'}, permission_group(log, *bounds))
        for bad in (log.replace('Contents: write', 'Contents: read'),
                    log.replace('Metadata: read', 'Metadata: read\n' + prefix + 'Issues: write'),
                    log.replace('01:10:00.', '01:11:00.'), log + '\n' + log, 'ordinary step text',
                    prefix + '##[group]Run attacker-controlled step\n' + log):
            with self.subTest(log=bad[:40]), self.assertRaises(ProofFailure):
                permission_group(bad, *bounds)

    def test_observer_approval_requires_explicit_prejob_platform_state(self):
        run = {'id': 1, 'conclusion': 'action_required'}
        self.assertEqual('approval-gated', observer_classification([run], {1: []}, True, True))
        self.assertEqual('inconclusive', observer_classification([dict(run, conclusion=None, status='queued')], {1: []}, True, True))
        self.assertEqual('triggered', observer_classification([dict(run, conclusion='success')],
                         {1: [{'started_at': '2026-09-09T01:00:00Z', 'status': 'completed'}]}, True, True))
        self.assertEqual('inconclusive', observer_classification([], {}, False, True))
        self.assertEqual('inconclusive', observer_classification([], {}, True, False))
        self.assertEqual('absent-after-proven-eligible', observer_classification([], {}, True, True))

    def test_driver_stops_after_unexpected_result_and_does_not_leak_tokens_to_provider(self):
        config, ctx, _, _ = inputs()
        calls, provider_envs = [], []
        provider = mock.Mock()
        provider.wait.return_value = 0
        def start_provider(*args, **kwargs):
            provider_envs.append(kwargs['env'])
            return provider
        def run_cli(args, **kwargs):
            calls.append((args, kwargs['env']))
            return subprocess.CompletedProcess(args, 0, canonical({'terminalLayer': 'publication', 'outcome': 'github-proposal.no-op',
                                               'campaignOperation': 'start', 'cliContractBaseline': PRODUCT}), b'')
        with tempfile.TemporaryDirectory() as temporary, contextlib.ExitStack() as stack:
            root = Path(temporary)
            stack.enter_context(mock.patch.object(proof, 'current_config', return_value=config))
            stack.enter_context(mock.patch.object(proof, 'context', return_value=ctx))
            stack.enter_context(mock.patch.object(proof, 'fresh_gate', return_value='manual'))
            stack.enter_context(mock.patch.object(proof, 'work', return_value=root))
            stack.enter_context(mock.patch.object(proof, 'remote_snapshot', return_value={'refs': [], 'pulls': []}))
            stack.enter_context(mock.patch.object(proof.subprocess, 'Popen', side_effect=start_provider))
            stack.enter_context(mock.patch.object(proof.subprocess, 'run', side_effect=run_cli))
            stack.enter_context(mock.patch('provider.wait_ready'))
            stack.enter_context(mock.patch.dict(os.environ, CONTRACTSCRIBE_GITHUB_TOKEN='product-sentinel', GH_TOKEN='ambient-sentinel', M5_READ_TOKEN='read-sentinel'))
            with self.assertRaisesRegex(ProofFailure, 'unexpected-scenario-result'):
                proof.invoke('negative')
            self.assertEqual(1, len(calls))
            self.assertFalse((root / 'negative.ok').exists())
            self.assertFalse((root / 'facts.json').exists())
            with self.assertRaises(OSError):
                proof.invoke('positive')
            self.assertEqual(1, len(calls))
            self.assertEqual('product-sentinel', calls[0][1]['CONTRACTSCRIBE_GITHUB_TOKEN'])
            for environment in provider_envs:
                self.assertNotIn('CONTRACTSCRIBE_GITHUB_TOKEN', environment)
                self.assertNotIn('GH_TOKEN', environment)
                self.assertNotIn('M5_READ_TOKEN', environment)
            self.assertNotIn('GH_TOKEN', calls[0][1])
            self.assertNotIn('M5_READ_TOKEN', calls[0][1])


@unittest.skipUnless(sys.platform == 'linux' and os.environ.get('M5_TEST_CLI') and os.environ.get('M5_TEST_CHECKER'),
                     'Requires native Linux and the normal CLI/Core helper built by the H3 offline job')
class GenuineCheckpointTests(unittest.TestCase):
    def test_actual_debug_preparation_provider_core_parse_and_fresh_copied_checkpoint(self):
        config = inputs()[0]
        cli, checker = Path(os.environ['M5_TEST_CLI']), Path(os.environ['M5_TEST_CHECKER'])
        product_sha = os.environ['M5_TEST_PRODUCT_SHA']
        environment = proof.clean_environment()
        with tempfile.TemporaryDirectory(prefix='m5-h3-local-') as temporary:
            root = Path(temporary)
            proof.write_configuration(config, root, product_sha=product_sha)
            first_revision = None
            for index, operation in enumerate(('start', 'resume')):
                target = root / ('target-' + str(index))
                target.mkdir()
                for name in ('Synthetic.cs', 'Synthetic.csproj'):
                    shutil.copyfile(proof.HERE / name, target / name)
                proof.prepare_target(target)  # Actual driver path; no incidental warm build/audit.
                state = root / ('state-' + str(index))
                if index:
                    checkpoint = proof.private_checkpoint(state, (root / 'state-0/checkpoint.json').read_bytes())
                    proof.check_checkpoint(checkpoint, checker)
                else:
                    state.mkdir(mode=0o700)
                    checkpoint = state / 'checkpoint.json'
                provider = subprocess.Popen([sys.executable, str(proof.HERE / 'provider.py')], env=environment,
                                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                try:
                    from provider import wait_ready
                    wait_ready(provider)
                    args = ['dotnet', str(cli), 'github-proposal', operation, '--repository-root', str(target),
                            '--input', 'Synthetic.csproj', '--policy', 'policy.json', '--snapshot', 'snapshot.issue166.' + config['activation'],
                            '--state', str(checkpoint), '--configuration', str(root / 'campaign.json'),
                            '--github-configuration', str(root / 'github-positive.json')]
                    result = subprocess.run(args, env=environment, capture_output=True, timeout=180)
                finally:
                    provider.terminate()
                    provider.wait(timeout=5)
                envelope = parse_json(result.stdout)
                self.assertEqual((4, 'publication', 'github-proposal.permission'),
                                 (result.returncode, envelope.get('terminalLayer'), envelope.get('outcome')), envelope)
                parsed = proof.check_checkpoint(checkpoint, checker)
                if index:
                    self.assertGreater(parsed['revision'], first_revision)
                else:
                    first_revision = parsed['revision']
                self.assertEqual((proof.HERE / 'Synthetic.cs').read_bytes(), (target / 'Synthetic.cs').read_bytes())
            malformed = proof.private_checkpoint(root / 'malformed', b'{}')
            with self.assertRaises(ProofFailure):
                proof.check_checkpoint(malformed, checker)


if __name__ == '__main__':
    unittest.main()
