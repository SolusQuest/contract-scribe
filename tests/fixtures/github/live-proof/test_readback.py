"""Read-adapter regressions; these observations never substitute for Stage B."""
import copy
import hashlib
import unittest
from unittest import mock

import proof
from proof_contract import *
from test_proof import inputs


class ReadbackTests(unittest.TestCase):
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
        refs = [{'ref': 'refs/heads/contract-scribe/coordination/' + key, 'object': {'sha': 'e' * 40}},
                {'ref': proposal_ref, 'object': {'sha': 'f' * 40}}]
        original = (proof.HERE / 'Synthetic.cs').read_bytes()
        candidate = original.replace(b'    public static void Run()', b'    /// <summary>Runs the synthetic operation.</summary>\n    public static void Run()')
        marker = '<!-- contract-scribe-publication-v1 ownership=sha256:' + '1' * 64 + ' -->\n'
        state = {'stage': 'published', 'repositoryId': REPOSITORY, 'targetRef': 'refs/heads/main',
                 'targetCommitOid': config['base_sha'], 'operationId': 'operation.issue166.' + config['activation'],
                 'generationId': 'generation.issue166.' + config['activation'], 'transition': 'initial',
                 'expectedBaseOid': config['base_sha'], 'observedBaseOid': config['base_sha'],
                 'proposalRefOid': 'f' * 40, 'proposalCommitOid': 'f' * 40, 'contentCommitOid': 'f' * 40,
                 'pullRequestNumber': 1, 'pullRequestCreationOperationCommitmentSha256': '1' * 64,
                 'snapshotCommitmentSha256': '2' * 64, 'policyCommitmentSha256': '3' * 64,
                 'operationCommitmentSha256': '4' * 64, 'ownershipMarkerSha256': sha(marker.encode()),
                 'proposalTreeOid': '5' * 40, 'coordinationPredecessorOid': '6' * 40,
                 'cumulativeChangedFiles': [{'path': 'Synthetic.cs', 'candidateSha256': sha(candidate)}],
                 'cumulativeDocumentationBlocks': 1}
        body = marker + 'campaign=sha256:' + key + '\ngeneration=sha256:' + generation + '\nsnapshot=sha256:' + '2' * 64
        body += '\npolicy=sha256:' + '3' * 64 + '\nheadRef=sha256:' + sha(proposal_ref.encode())
        body += '\nbaseRef=sha256:' + sha(b'refs/heads/main') + '\ncreationOperationId=sha256:' + '4' * 64 + '\n'
        pr = {'number': 1, 'state': 'open', 'draft': True, 'merged': False, 'user': dict(ACTOR),
              'head': {'sha': 'f' * 40, 'ref': proposal_ref.removeprefix('refs/heads/'), 'repo': {'id': REPOSITORY_ID}},
              'base': {'sha': config['base_sha'], 'ref': 'main', 'repo': {'id': REPOSITORY_ID}}, 'body': body,
              'title': 'ContractScribe proposal ' + generation, 'html_url': 'https://github.com/' + REPOSITORY + '/pull/1',
              'created_at': '2026-09-09T01:10:00Z'}
        commit = {'sha': 'f' * 40, 'parents': [{'sha': config['base_sha']}], 'tree': {'sha': '5' * 40}}
        oid = lambda data: hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()
        entry = {'path': 'Synthetic.cs', 'mode': '100644', 'type': 'blob', 'sha': oid(original)}
        base_tree = {'sha': '7' * 40, 'truncated': False, 'tree': [entry]}
        candidate_tree = {'sha': '5' * 40, 'truncated': False, 'tree': [dict(entry, sha=oid(candidate))]}
        values = {'pulls/1': pr, 'git/commits/' + 'f' * 40: commit,
                  'git/commits/' + config['base_sha']: {'sha': config['base_sha'], 'tree': {'sha': '7' * 40}},
                  'git/trees/' + '7' * 40 + '?recursive=1': base_tree,
                  'git/trees/' + '5' * 40 + '?recursive=1': candidate_tree,
                  'git/commits/' + 'e' * 40: {'sha': 'e' * 40, 'parents': [{'sha': '6' * 40}]}}
        changes = [{'filename': 'Synthetic.cs', 'status': 'modified'}]
        api = mock.Mock()
        api.get.side_effect = values.__getitem__
        api.pages.side_effect = lambda suffix: refs if suffix.startswith('git/') else changes if suffix.endswith('/files') else [pr]
        api.content.side_effect = lambda path, commit: canonical(state) if path.startswith('.contract-scribe/') else candidate
        facts = proof.published_facts(config, api, inspect_trees=True)
        self.assertEqual(1, facts['pull_request'])
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
