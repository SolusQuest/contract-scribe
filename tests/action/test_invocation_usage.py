"""Current CLI -> Action usage projection/failure regressions (stdlib only)."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent.parent / "scripts" / "action"))
import common as C
import driver
import invoke
import usage as U


def fixture(kind):
    value = json.loads((HERE / "invocation-usage-no-provider.json").read_text())
    value.update(completeness=kind, campaignLineage="campaign.private-summary-sentinel",
                 snapshotId="a" * 64, batchId="b" * 64)
    value["calls"].update(physicalProviderDispatches=2, logicalProviderCalls=1, retryDispatches=1, toolCalls=3)
    values = dict(input=12, cachedInput=8, uncachedInput=4, output=7, reasoning=3)
    for name, observed in values.items():
        missing = 2 if kind == "unknown" else 1 if kind == "partial" and name in ("cachedInput", "reasoning") else 0
        value["tokens"][name] = dict(observed=None if missing == 2 else observed, missingDispatches=missing)
    charge = dict(observed=12, conservativeUnobserved=100, totalCharged=112)
    value["history"] = {name: dict(charge) for name in ("providerRequests",) + U.TOKENS}
    value["accounting"] = {name: dict(charge) for name in U.TOKENS[:4]}
    value["accounting"].update(unsettledDispatches=0, activeExposure=None)
    value["termination"]["stopReason"] = "completed"
    return value


def envelope(value):
    return dict(githubProposalEnvelopeVersion=1, terminalLayer="publication", cliContractBaseline="b",
                toolVersion="t", campaignOperation="start", publicationOperationId="op", generationId="gen",
                outcome="github-proposal.published", diagnosticCodes=[], checkpointRevision=7,
                pullRequestUrl=None, publicationDiagnostic=None, invocationUsage=value)


def encode(value):
    return (json.dumps(value, separators=(",", ":")) + "\n").encode()


class InvocationUsageTests(unittest.TestCase):
    def test_known_partial_unknown_roundtrip_and_bounded_summary(self):
        for kind in ("complete", "partial", "unknown"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temp:
                product = envelope(fixture(kind))
                raw = encode(product)
                self.assertGreater(len(json.dumps(product["invocationUsage"])), C.BOUND_OUTPUT_VALUE)
                self.assertLess(len(raw), C.BOUND_ENVELOPE)
                source, emitted, summary = [str(Path(temp) / name) for name in ("invoke.txt", "emit.txt", "summary.md")]
                with patch.dict(os.environ, {"GITHUB_OUTPUT": source}):
                    parsed = invoke.parse_envelope(raw, b"", 0)
                    invoke.emit_outputs(parsed, raw, 0)
                    C.write_output("action-status", "ok")
                driver.cmd_usage_roundtrip([source, emitted, summary, "2"])
                text = Path(summary).read_text()
                self.assertIn(kind, text)
                self.assertNotIn("private-summary-sentinel", text)
                self.assertNotIn("a" * 64, text)

    def test_no_provider_usage_is_known_zero(self):
        value = json.loads((HERE / "invocation-usage-no-provider.json").read_text())
        parsed = invoke.parse_envelope(encode(envelope(value)), b"", 0)
        self.assertEqual(0, parsed["invocationUsage"]["calls"]["physicalProviderDispatches"])
        for metric in parsed["invocationUsage"]["tokens"].values():
            self.assertEqual(dict(observed=0, missingDispatches=0), metric)

    def test_missing_version_boolean_and_malformed_usage_fail_without_product_outputs(self):
        mutations = [lambda p: p.pop("invocationUsage"),
                     lambda p: p["invocationUsage"].update(invocationUsageVersion=2),
                     lambda p: p["invocationUsage"].update(invocationUsageVersion=True),
                     lambda p: p["invocationUsage"]["tokens"]["input"].update(observed="private-provider-sentinel"),
                     lambda p: p["invocationUsage"]["calls"].update(physicalProviderDispatches=True),
                     lambda p: p["invocationUsage"]["tokens"].pop("reasoning")]
        for mutate in mutations:
            with self.subTest(mutate=mutate), tempfile.TemporaryDirectory() as temp:
                product = envelope(fixture("complete"))
                mutate(product)
                output = Path(temp) / "out.txt"
                with patch.dict(os.environ, {"GITHUB_OUTPUT": str(output)}):
                    with self.assertRaises(SystemExit):
                        invoke.parse_envelope(encode(product), b"", 0)
                text = output.read_text()
                self.assertIn("action-status=action.envelope-", text)
                self.assertNotIn("usage-json=", text)
                self.assertNotIn("result=", text)
                self.assertNotIn("private-provider-sentinel", text)

    def test_controlled_product_failure_retains_usage_and_single_product_annotation(self):
        with tempfile.TemporaryDirectory() as temp:
            product = envelope(fixture("partial"))
            product.update(outcome="github-proposal.permission", diagnosticCodes=["github-proposal.permission"])
            source, emitted, summary = [str(Path(temp) / name) for name in ("invoke.txt", "emit.txt", "summary.md")]
            with patch.dict(os.environ, {"GITHUB_OUTPUT": source}):
                parsed = invoke.parse_envelope(encode(product), b"github proposal stopped: github-proposal.permission\n", 4)
                invoke.emit_outputs(parsed, encode(product), 4)
                C.write_output("action-status", "ok")
            driver.cmd_usage_roundtrip([source, emitted, summary, "2"])
            self.assertEqual("ok", driver.read_outputs(emitted)["action-status"])
            self.assertEqual("4", driver.read_outputs(emitted)["exit-code"])

    def test_wrapper_failure_has_unavailable_usage_and_one_wrapper_annotation(self):
        with tempfile.TemporaryDirectory() as temp:
            output, summary = [str(Path(temp) / name) for name in ("out.txt", "summary.md")]
            env = dict(os.environ, GITHUB_OUTPUT=output, GITHUB_STEP_SUMMARY=summary,
                       CS_STATUS_PREPARE="action.prepare-missing-campaign")
            for name in list(env):
                if name.startswith("CS_OUT_") or name.startswith("CS_STATUS_") and name != "CS_STATUS_PREPARE":
                    del env[name]
            result = subprocess.run([sys.executable, str(HERE.parent.parent / "scripts/action/emit.py")],
                                    env=env, text=True, capture_output=True, check=True)
            self.assertEqual(1, result.stdout.count("::error::"))
            self.assertIn("action.prepare-missing-campaign", result.stdout)
            self.assertIn("usage | unavailable", Path(summary).read_text())
            self.assertEqual("", driver.read_outputs(output)["usage-json"])

    def test_summary_uses_only_numbers_and_closed_stop_not_raw_identity_or_source(self):
        value = fixture("partial")
        value["termination"]["source"] = "`evil | private-provider-sentinel`"
        text = "\n".join(U.summary_rows(value))
        self.assertNotIn("evil", text)
        self.assertNotIn("private", text)
        value["termination"]["stopReason"] = "`evil`"
        self.assertEqual(["| usage | unavailable |"], U.summary_rows(value))


if __name__ == "__main__":
    unittest.main()
