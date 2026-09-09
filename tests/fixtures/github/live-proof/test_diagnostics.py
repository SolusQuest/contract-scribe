import contextlib
import io
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

import proof
import proof_contract
from proof_contract import PRODUCT, ProofFailure, canonical
from test_proof import inputs


class UnexpectedResultDiagnosticsTests(unittest.TestCase):
    def test_real_invoke_retains_closed_failure_codes_and_stops_without_success(self):
        payload = {'terminalLayer': 'publication', 'outcome': 'github-proposal.host-failure',
                   'campaignOperation': 'start', 'cliContractBaseline': PRODUCT,
                   'diagnosticCodes': ['github-proposal.host-failure', 'private-sentinel'],
                   'privateField': 'private-sentinel'}
        text = self.invoke_failure(5, canonical(payload))
        self.assertIn('"outcome":"github-proposal.host-failure"', text)
        self.assertIn('"exitCode":5', text)

    def test_individual_predicate_failures_and_malformed_stdout_keep_rejection(self):
        accepted = {'terminalLayer': 'publication', 'outcome': 'github-proposal.published',
                    'campaignOperation': 'start', 'cliContractBaseline': PRODUCT}
        cases = [(5, canonical(accepted), 'unexpected-scenario-result')]
        for key, value in [('terminalLayer', 'campaign'), ('outcome', 'github-proposal.no-op'),
                           ('campaignOperation', 'resume'), ('cliContractBaseline', 'private-sentinel')]:
            cases.append((0, canonical(dict(accepted, **{key: value})), 'unexpected-scenario-result'))
        cases.extend([(0, b'private-sentinel', 'invalid-json'),
                      (0, b'{"outcome":1,"outcome":2}', 'duplicate-property'),
                      (0, b'[]', 'unexpected-scenario-result')])
        for exit_code, data, error in cases:
            with self.subTest(exit_code=exit_code, data=data):
                self.invoke_failure(exit_code, data, error)

    def invoke_failure(self, exit_code, data, error='unexpected-scenario-result'):
        config = inputs()[0]
        with tempfile.TemporaryDirectory() as temporary, contextlib.ExitStack() as stack:
            root = Path(temporary)
            (root / 'negative.ok').write_text('stale')
            stack.enter_context(mock.patch.object(proof, 'current_config', return_value=config))
            stack.enter_context(mock.patch.object(proof, 'context', return_value={}))
            stack.enter_context(mock.patch.object(proof, 'fresh_gate', return_value='manual'))
            stack.enter_context(mock.patch.object(proof, 'work', return_value=root))
            provider = mock.Mock()
            stack.enter_context(mock.patch.object(proof.subprocess, 'Popen', return_value=provider))
            run = stack.enter_context(mock.patch.object(proof.subprocess, 'run', return_value=
                    subprocess.CompletedProcess([], exit_code, data, b'private-sentinel')))
            stack.enter_context(mock.patch('provider.wait_ready'))
            stack.enter_context(mock.patch.dict(os.environ, CONTRACTSCRIBE_GITHUB_TOKEN='private-sentinel'))
            output = stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
            with self.assertRaisesRegex(ProofFailure, error):
                proof.invoke('positive')
            self.assertEqual(1, run.call_count)
            self.assertEqual(1, provider.terminate.call_count)
            self.assertFalse((root / 'positive-result.json').exists())
            self.assertFalse((root / 'facts.json').exists())
            text = output.getvalue()
            self.assertIn('scenario-unexpected:', text)
            self.assertNotIn('private-sentinel', text)
            return text

    def test_malformed_or_unknown_fields_are_bounded_and_never_echoed(self):
        for raw in [b'private-sentinel', b'"private-sentinel"', b'[]', b'x' * 8193,
                    b'{"outcome":"private-sentinel","outcome":"duplicate"}',
                    canonical({'terminalLayer': ['private-sentinel'], 'outcome': {'private': 'private-sentinel'},
                               'campaignOperation': 'private-sentinel', 'cliContractBaseline': 'private-sentinel',
                               'diagnosticCodes': ['private-sentinel', {}, 'github-proposal.permission'] * 100})]:
            with self.subTest(raw=raw[:30]):
                summary = proof_contract.unexpected_result_summary(5, raw)
                rendered = canonical(summary)
                self.assertNotIn(b'private-sentinel', rendered)
                self.assertLess(len(rendered), 2048)
                self.assertLessEqual(len(summary['diagnosticCodes']), 16)
        self.assertIsNone(proof_contract.unexpected_result_summary(True, b'{}')['exitCode'])


if __name__ == '__main__':
    unittest.main()
