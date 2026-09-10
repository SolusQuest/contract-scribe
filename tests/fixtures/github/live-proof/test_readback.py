"""Read-adapter regressions; these observations never substitute for Stage B."""
import copy
import base64
import hashlib
import io
import os
from pathlib import Path
import tempfile
import unittest
import urllib.error
from unittest import mock

import proof
from proof_contract import *
from test_proof import inputs


class ReadbackTests(unittest.TestCase):
    def test_only_exact_retained_refs_and_pull_are_excluded_from_the_new_trial(self):
        config = inputs()[0]
        before = copy.deepcopy(RETAINED_HISTORY)
        self.assertEqual({'refs': [], 'pulls': []}, proof.trial_snapshot(config, before))
        self.assertEqual(RETAINED_HISTORY, before)
        for activation in ('30e43175076e492e95133b8d98a5ff89', '345888b4f36248fe99263cb201611f52'):
            with self.subTest(activation=activation), self.assertRaisesRegex(ProofFailure, 'history-overlaps-trial'):
                proof.trial_snapshot(dict(config, activation=activation), before)
        cases = []
        for index in range(len(before['refs'])):
            for field, value in (('ref', before['refs'][index]['ref'] + '-other'), ('oid', '0' * 40)):
                changed = copy.deepcopy(before)
                changed['refs'][index][field] = value
                cases.append((changed, 'retained-history-changed'))
            for duplicate in (False, True):
                changed = copy.deepcopy(before)
                row = changed['refs'].pop(index)
                if duplicate:
                    changed['refs'][index:index] = [row, dict(row)]
                cases.append((changed, 'retained-history-changed'))
        for field, value in (('number', 99), ('head', '0' * 40), ('state', 'closed'), ('draft', False),
                             ('body_digest', '0' * 64), ('actor', 1)):
            changed = copy.deepcopy(before)
            changed['pulls'][0][field] = value
            cases.append((changed, 'retained-history-changed'))
        for pulls in ([], before['pulls'] * 2):
            changed = copy.deepcopy(before)
            changed['pulls'] = pulls
            cases.append((changed, 'retained-history-changed'))
        for key, row in (('refs', {'ref': 'refs/heads/contract-scribe/unexpected', 'oid': 'a' * 40}),
                         ('pulls', {'number': 99})):
            changed = copy.deepcopy(before)
            changed[key].append(row)
            cases.append((changed, 'preexisting-work'))
        for snapshot, error in cases:
            with self.subTest(snapshot=snapshot), tempfile.TemporaryDirectory() as temporary, \
                 mock.patch.object(proof, 'current_config', return_value=config), \
                 mock.patch.object(proof, 'context', return_value={}), \
                 mock.patch.object(proof, 'fresh_gate', return_value='manual'), \
                 mock.patch.object(proof, 'work', return_value=Path(temporary)), \
                 mock.patch.object(proof, 'remote_snapshot', return_value=snapshot), \
                 mock.patch.object(proof.subprocess, 'Popen') as provider, \
                 mock.patch.object(proof.subprocess, 'run') as cli:
                with self.assertRaisesRegex(ProofFailure, error):
                    proof.invoke('negative')
                provider.assert_not_called()
                cli.assert_not_called()

    def test_request_construction_preserves_root_children_and_repository_boundary(self):
        root = 'repos/' + REPOSITORY
        observed = []
        def respond(request, timeout):
            observed.append(request)
            return io.BytesIO(b'{}')
        opener = mock.Mock()
        opener.open.side_effect = respond
        with mock.patch.object(proof.urllib.request, 'build_opener', return_value=opener):
            api = proof.Api()
            for suffix in ('', 'actions/runs/100', 'git/matching-refs/heads/contract-scribe/'):
                api.get(suffix)
                expected = 'https://api.github.com/' + root + ('/' + suffix if suffix else '')
                self.assertEqual(expected, observed[-1].full_url)
                self.assertEqual('GET', observed[-1].method)
                self.assertIsNone(observed[-1].data)
                self.assertIsNone(observed[-1].get_header('Authorization'))
            for path in (root + '/', root + '-other', root + '-other/actions/runs/100',
                         'repos/SolusQuest/another-repository', 'users/Yuee98'):
                count = len(observed)
                with self.subTest(path=path), self.assertRaisesRegex(ProofFailure, 'api-target'):
                    api.request(path)
                self.assertEqual(count, len(observed))
            proof.Api('read-sentinel').get('')
            self.assertEqual('Bearer read-sentinel', observed[-1].get_header('Authorization'))

    def test_complete_active_inactive_and_failed_gate_through_real_http_request_boundary(self):
        config, ctx, repository, run = inputs()
        prefix = 'https://api.github.com/repos/' + REPOSITORY
        observer_bytes = b'fixed observer bytes for request-construction regression'
        def content(data):
            return {'type': 'file', 'encoding': 'base64', 'size': len(data),
                    'content': base64.b64encode(data).decode(),
                    'sha': hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()}
        values = {prefix: repository, prefix + '/actions/runs/100': run,
                  prefix + '/contents/' + WORKFLOW + '?ref=' + config['workflow_sha']: content((proof.SOURCE / WORKFLOW).read_bytes()),
                  prefix + '/git/ref/heads/main': {'object': {'sha': config['base_sha']}},
                  prefix + '/actions/workflows/' + str(OBSERVER_ID): {'id': OBSERVER_ID, 'path': OBSERVER, 'state': 'active'},
                  prefix + '/contents/' + OBSERVER + '?ref=' + config['base_sha']: content(observer_bytes)}
        observed = []
        def respond(request, timeout):
            observed.append(request)
            if request.full_url not in values:
                raise urllib.error.HTTPError(request.full_url, 404, 'Not Found', {}, None)
            return io.BytesIO(canonical(values[request.full_url]))
        opener = mock.Mock()
        opener.open.side_effect = respond
        with mock.patch.object(proof.urllib.request, 'build_opener', return_value=opener), \
             mock.patch.object(proof, 'OBSERVER_DIGEST', sha(observer_bytes)):
            api = proof.Api()
            now = timestamp('2026-09-09T02:00:00Z')
            self.assertEqual('manual', proof.fresh_gate(config, ctx, api, now))
            repository['topics'] = []
            self.assertEqual('inactive', proof.fresh_gate(config, ctx, api, now))
            self.assertEqual(12, len(observed))
            self.assertTrue(all(request.method == 'GET' and request.get_header('Authorization') is None for request in observed))
            del values[prefix]
            with mock.patch.object(proof, 'current_config', return_value=config), \
                 mock.patch.object(proof, 'context', return_value=ctx), \
                 mock.patch.object(proof.subprocess, 'Popen') as launch, \
                 mock.patch.dict(os.environ, CONTRACTSCRIBE_GITHUB_TOKEN='product-sentinel'):
                with self.assertRaisesRegex(ProofFailure, 'github-read-unavailable'):
                    proof.invoke('negative')
                launch.assert_not_called()
                self.assertIsNone(observed[-1].get_header('Authorization'))

    def test_each_fresh_gate_reads_current_topics_attempt_main_and_observer(self):
        config, ctx, repository, run = inputs()
        observer_bytes = (proof.HERE / 'Synthetic.cs').read_bytes()
        values = {'': repository, 'actions/runs/100': run,
                  'git/ref/heads/main': {'object': {'sha': config['base_sha']}},
                  'actions/workflows/' + str(OBSERVER_ID): {'id': OBSERVER_ID, 'path': OBSERVER, 'state': 'active'}}
        api = mock.Mock()
        api.get.side_effect = values.__getitem__
        api.content.side_effect = lambda path, commit: (proof.SOURCE / WORKFLOW).read_bytes() if path == WORKFLOW else observer_bytes
        now = timestamp('2026-09-09T02:00:00Z')
        with mock.patch.object(proof, 'OBSERVER_DIGEST', sha(observer_bytes)):
            self.assertEqual('manual', proof.fresh_gate(config, ctx, api, now))
            repository['topics'] = []
            self.assertEqual('inactive', proof.fresh_gate(config, ctx, api, now))
            repository['topics'] = ['m5-h3-' + config['activation']]
            for value, key, bad in [(run, 'run_attempt', 2), (values['git/ref/heads/main']['object'], 'sha', 'd' * 40),
                                    (values['actions/workflows/' + str(OBSERVER_ID)], 'state', 'disabled_manually')]:
                original = value[key]
                value[key] = bad
                with self.subTest(key=key), self.assertRaises(ProofFailure):
                    proof.fresh_gate(config, ctx, api, now)
                value[key] = original
            api.content.side_effect = lambda *args: b'changed'
            with self.assertRaises(ProofFailure):
                proof.fresh_gate(config, ctx, api, now)

    def test_remote_actor_marker_diff_commits_and_complete_tree_must_agree(self):
        config = inputs()[0]
        lineage = 'campaign.issue166.' + config['activation']
        key = proof.identity_hash('coordination-ref', 'solusquest', 'contract-scribe-sandbox', 'refs/heads/main', lineage)
        generation = 'd' * 64
        proposal_ref = 'refs/heads/contract-scribe/proposals/' + key + '/' + generation
        refs = [{'ref': row['ref'], 'object': {'sha': row['oid']}} for row in RETAINED_HISTORY['refs']]
        refs += [{'ref': 'refs/heads/contract-scribe/coordination/' + key, 'object': {'sha': 'e' * 40}},
                 {'ref': proposal_ref, 'object': {'sha': 'f' * 40}}]
        original = (proof.HERE / 'Synthetic.cs').read_bytes()
        candidate = original.replace(b'    public static void Run()', b'    /// <summary>Runs the synthetic operation.</summary>\n    public static void Run()')
        marker = '<!-- contract-scribe-publication-v1 ownership=sha256:' + '1' * 64 + ' -->\n'
        state = {'stage': 'published', 'repositoryId': REPOSITORY, 'targetRef': 'refs/heads/main',
                 'targetCommitOid': config['base_sha'], 'operationId': 'operation.issue166.' + config['activation'],
                 'generationId': 'generation.issue166.' + config['activation'], 'transition': 'initial',
                 'expectedBaseOid': config['base_sha'], 'observedBaseOid': config['base_sha'],
                 'proposalRefOid': 'f' * 40, 'proposalCommitOid': 'f' * 40, 'contentCommitOid': 'f' * 40,
                 'pullRequestNumber': 2, 'pullRequestCreationOperationCommitmentSha256': '1' * 64,
                 'snapshotCommitmentSha256': '2' * 64, 'policyCommitmentSha256': '3' * 64,
                 'operationCommitmentSha256': '4' * 64, 'ownershipMarkerSha256': sha(marker.encode()),
                 'proposalTreeOid': '5' * 40, 'coordinationPredecessorOid': '6' * 40,
                 'cumulativeChangedFiles': [{'path': 'Synthetic.cs', 'candidateSha256': sha(candidate)}],
                 'cumulativeDocumentationBlocks': 1}
        body = marker + 'campaign=sha256:' + key + '\ngeneration=sha256:' + generation + '\nsnapshot=sha256:' + '2' * 64
        body += '\npolicy=sha256:' + '3' * 64 + '\nheadRef=sha256:' + sha(proposal_ref.encode())
        body += '\nbaseRef=sha256:' + sha(b'refs/heads/main') + '\ncreationOperationId=sha256:' + '4' * 64 + '\n'
        pr = {'number': 2, 'state': 'open', 'draft': True, 'merged': False, 'user': dict(ACTOR),
              'head': {'sha': 'f' * 40, 'ref': proposal_ref.removeprefix('refs/heads/'), 'repo': {'id': REPOSITORY_ID}},
              'base': {'sha': config['base_sha'], 'ref': 'main', 'repo': {'id': REPOSITORY_ID}}, 'body': body,
              'title': 'ContractScribe proposal ' + generation, 'html_url': 'https://github.com/' + REPOSITORY + '/pull/2',
              'created_at': '2026-09-09T01:10:00Z'}
        historical_body = ('<!-- contract-scribe-publication-v1 ownership=sha256:7e46767b8151d4058af876473d18b9f40e76d315e9230de23e63948b2fecdf5d -->\n'
                           'campaign=sha256:859d9029d66cf17166602bc07f5b0f4172e68d763cf8589a796706ac4d1588f5\n'
                           'generation=sha256:d30f4d3006ee2dbbb1a9587dfa7649347b32ed295a0883422ac9026fc7991272\n'
                           'snapshot=sha256:275d682a952526e7cd483a0b47c31b4ee4feb9dedbe910a64c7cf4cd2cfec390\n'
                           'policy=sha256:f86d1d9deec7fcc47aa53fcd2da040b72a82b6892fa1753d2915c54098b583eb\n'
                           'headRef=sha256:ec3855cf14e57fcaee9b124f1958ad6bfbb721e76484d90555bbe13c383f320e\n'
                           'baseRef=sha256:f921bd05e68b03740c450e565e0e6173e546193170b2dd404ddb6f153e9b5bf3\n'
                           'creationOperationId=sha256:a421db74fc8864a6378424611c867126613982012ad9b6237a8923b98ad401a6\n')
        historical = {'number': 1, 'state': 'open', 'draft': True, 'user': dict(ACTOR), 'body': historical_body,
                      'head': {'ref': RETAINED_HISTORY['refs'][2]['ref'].removeprefix('refs/heads/'),
                               'sha': RETAINED_HISTORY['pulls'][0]['head']}}
        commit = {'sha': 'f' * 40, 'parents': [{'sha': config['base_sha']}], 'tree': {'sha': '5' * 40}}
        oid = lambda data: hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()
        entry = {'path': 'Synthetic.cs', 'mode': '100644', 'type': 'blob', 'sha': oid(original)}
        base_tree = {'sha': '7' * 40, 'truncated': False, 'tree': [entry]}
        candidate_tree = {'sha': '5' * 40, 'truncated': False, 'tree': [dict(entry, sha=oid(candidate))]}
        values = {'pulls/2': pr, 'git/commits/' + 'f' * 40: commit,
                  'git/commits/' + config['base_sha']: {'sha': config['base_sha'], 'tree': {'sha': '7' * 40}},
                  'git/trees/' + '7' * 40 + '?recursive=1': base_tree,
                  'git/trees/' + '5' * 40 + '?recursive=1': candidate_tree,
                  'git/commits/' + 'e' * 40: {'sha': 'e' * 40, 'parents': [{'sha': '6' * 40}]}}
        changes = [{'filename': 'Synthetic.cs', 'status': 'modified'}]
        api = mock.Mock()
        api.get.side_effect = values.__getitem__
        api.pages.side_effect = lambda suffix: refs if suffix.startswith('git/') else changes if suffix.endswith('/files') else [historical, pr]
        api.content.side_effect = lambda path, commit: canonical(state) if path.startswith('.contract-scribe/') else candidate
        facts = proof.published_facts(config, api, inspect_trees=True)
        self.assertEqual(2, facts['pull_request'])
        for row in RETAINED_HISTORY['refs']:
            self.assertIn(row, facts['snapshot']['refs'])
        self.assertEqual(RETAINED_HISTORY['pulls'], facts['snapshot']['pulls'][:1])
        self.assertEqual(facts, proof.published_facts(config, api, inspect_trees=True))
        for residue in ({'ref': 'refs/heads/contract-scribe/unexpected', 'object': {'sha': 'a' * 40}},):
            refs.append(residue)
            with self.assertRaisesRegex(ProofFailure, 'published-resource-count'):
                proof.published_facts(config, api)
            refs.pop()
        refs[0]['object']['sha'] = '0' * 40
        with self.assertRaisesRegex(ProofFailure, 'retained-history-changed'):
            proof.published_facts(config, api)
        refs[0]['object']['sha'] = RETAINED_HISTORY['refs'][0]['oid']
        historical['body'] += 'changed'
        with self.assertRaisesRegex(ProofFailure, 'retained-history-changed'):
            proof.published_facts(config, api)
        historical['body'] = historical_body
        for value, field, bad in [(pr['user'], 'id', 1), (pr, 'body', body + 'changed'), (pr, 'draft', False),
                                  (pr['base'], 'sha', '0' * 40), (changes[0], 'filename', 'Injected.cs'),
                                  (commit, 'parents', []), (candidate_tree, 'truncated', True),
                                  (candidate_tree['tree'][0], 'mode', '120000'),
                                  (state, 'cumulativeDocumentationBlocks', 2)]:
            saved = copy.deepcopy(value[field])
            value[field] = bad
            with self.subTest(field=field), self.assertRaises(ProofFailure):
                proof.published_facts(config, api, inspect_trees=True)
            value[field] = saved
        candidate_tree['tree'].append(dict(entry, path='Injected.cs'))
        with self.assertRaisesRegex(ProofFailure, 'candidate-tree'):
            proof.published_facts(config, api, inspect_trees=True)


if __name__ == '__main__':
    unittest.main()
