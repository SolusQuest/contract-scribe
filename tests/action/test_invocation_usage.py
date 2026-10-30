"""Current CLI -> Action usage projection/failure regressions (stdlib only)."""
import json
import os
import re
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
    value["providerDiagnostics"]["missingDispatches"] = 2
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
    def test_checkpoint_parent_observation_has_narrow_context_and_preserves_old_values(self):
        for source in U.CHECKPOINT_CANCELLATIONS:
            observation = dict(operationOrdinal=7, operation="Write", attemptedRevision=17,
                               stage="CheckpointReadback", reason="Cancelled", transportCode="Cancelled",
                               httpStatus=None, cancellation=source, refComparison=None)
            value = fixture("partial")
            value["checkpointFailure"] = observation
            raw = encode(envelope(value))
            parsed = invoke.parse_envelope(raw, b"", 6)
            self.assertEqual(observation, parsed["invocationUsage"]["checkpointFailure"])
            self.assertLess(len(raw), C.BOUND_ENVELOPE)
            if source not in ("OperationCheckpointAcceptanceDeadline", "OperationSettlementToken"):
                continue
            for key, invalid in (("operation", "Read"), ("operation", "Renew"),
                                 ("stage", "FinalRef"), ("attemptedRevision", None), ("reason", "Transport")):
                self.assertFalse(U.checkpoint_failure(dict(observation, **{key: invalid})))
            self.assertFalse(U.checkpoint_failure(dict(observation, token="private-marker")))
            self.assertNotIn("private-marker", "\n".join(U.summary_rows(value)))

    def test_final_ref_comparison_is_operation_local_closed_and_bounded(self):
        comparison = dict(expectedCommit="a" * 64, predecessorCommit="b" * 64,
                          observedCommit="b" * 64, relation="Predecessor", delivery="NeedsReadback", acknowledged=True)
        observation = dict(operationOrdinal=69, operation="Renew", attemptedRevision=None, stage="FinalRef",
                           reason="RefMismatch", transportCode=None, httpStatus=None, cancellation=None,
                           refComparison=comparison)
        value = fixture("partial")
        value["checkpointFailure"] = observation
        with tempfile.TemporaryDirectory() as temp:
            output = str(Path(temp) / "out.txt")
            raw = encode(envelope(value))
            with patch.dict(os.environ, {"GITHUB_OUTPUT": output}):
                parsed = invoke.parse_envelope(raw, b"", 0)
                invoke.emit_outputs(parsed, raw, 0)
            self.assertEqual(observation, json.loads(driver.read_outputs(output)["usage-json"])["checkpointFailure"])
            summary = "\n".join(U.summary_rows(value))
            self.assertIn("Predecessor / NeedsReadback / yes", summary)
            self.assertNotIn("a" * 64, summary)
            self.assertLess(len(raw), C.BOUND_ENVELOPE)
        for relation, observed, predecessor in (("Expected", "a" * 64, None), ("Other", "c" * 64, "b" * 64),
                                                ("Unavailable", None, None)):
            row = dict(comparison, relation=relation, observedCommit=observed, predecessorCommit=predecessor,
                       delivery="Ambiguous", acknowledged=False)
            self.assertTrue(U.ref_comparison(row))
        invalid = (("expectedCommit", "a" * 40), ("expectedCommit", "A" * 64), ("expectedCommit", None),
                   ("predecessorCommit", "private-marker"), ("observedCommit", "private-marker"),
                   ("observedCommit", None), ("relation", "Other"), ("relation", []),
                   ("delivery", "Read"), ("delivery", {}), ("acknowledged", 1), ("acknowledged", False))
        for key, item in invalid:
            row = dict(comparison, **{key: item})
            self.assertFalse(U.ref_comparison(row), (key, item))
            broken = fixture("partial")
            broken["checkpointFailure"] = dict(observation, refComparison=row)
            self.assertFalse(U.valid(broken))
            self.assertEqual(["| usage | unavailable |"], U.summary_rows(broken))
        self.assertFalse(U.ref_comparison(dict(comparison, rawOid="private-marker")))
        self.assertFalse(U.ref_comparison({k: v for k, v in comparison.items() if k != "delivery"}))
        self.assertFalse(U.checkpoint_failure(dict(observation, stage="PredecessorRead")))
        self.assertFalse(U.checkpoint_failure({k: v for k, v in observation.items() if k != "refComparison"}))

    def test_checkpoint_failure_is_closed_bounded_and_lossless_through_action(self):
        observation = dict(operationOrdinal=7, operation="Write", attemptedRevision=17,
                           stage="CheckpointReadback", reason="Transport", transportCode="NotFound",
                           httpStatus=404, cancellation=None, refComparison=None)
        with tempfile.TemporaryDirectory() as temp:
            value = fixture("partial")
            value["checkpointFailure"] = observation
            raw = encode(envelope(value))
            output = str(Path(temp) / "out.txt")
            with patch.dict(os.environ, {"GITHUB_OUTPUT": output}):
                parsed = invoke.parse_envelope(raw, b"", 0)
                invoke.emit_outputs(parsed, raw, 0)
            self.assertEqual(observation, json.loads(driver.read_outputs(output)["usage-json"])["checkpointFailure"])
            summary = "\n".join(U.summary_rows(value))
            self.assertIn("CheckpointReadback / Transport", summary)
            self.assertIn("NotFound / 404 / unknown", summary)
            self.assertNotIn("campaign.private-summary-sentinel", summary)
            self.assertLess(len(raw), C.BOUND_ENVELOPE)
        vocabularies = (("operation", U.CHECKPOINT_OPERATIONS), ("stage", U.CHECKPOINT_STAGES),
                        ("reason", U.CHECKPOINT_REASONS), ("transportCode", U.CHECKPOINT_TRANSPORT),
                        ("cancellation", U.CHECKPOINT_CANCELLATIONS))
        for key, choices in vocabularies:
            for choice in choices:
                row = dict(observation, **{key: choice})
                if key == "cancellation" or row["reason"] == "Cancelled":
                    row.update(reason="Cancelled", cancellation=choice if key == "cancellation" else "Unknown")
                self.assertTrue(U.checkpoint_failure(row), (key, choice))
        for key, invalid in (("operationOrdinal", 0), ("operationOrdinal", True),
                             ("operationOrdinal", 2**63), ("attemptedRevision", -1),
                             ("attemptedRevision", True), ("httpStatus", True),
                             ("httpStatus", 99), ("httpStatus", 600),
                             ("reason", []), ("stage", {}), ("operation", None),
                             ("cancellation", "caller-timeout"), ("transportCode", "private-error-sentinel"),
                             ("transportCode", None), ("reason", "Cancelled")):
            row = dict(observation, **{key: invalid})
            self.assertFalse(U.checkpoint_failure(row), (key, invalid))
            bad = fixture("partial")
            bad["checkpointFailure"] = row
            self.assertFalse(U.valid(bad))
            self.assertNotIn("private-error-sentinel", "\n".join(U.summary_rows(bad)))
        self.assertFalse(U.checkpoint_failure(dict(observation, privateText="private-error-sentinel")))
        self.assertFalse(U.checkpoint_failure({key: value for key, value in observation.items() if key != "stage"}))

    def test_logical_attempts_use_their_contract_bound_independently_of_physical_requests(self):
        contracts = (HERE.parent.parent / "src/ContractScribe.Core/DocumentationScribeContracts.cs").read_text()
        maximum_attempts = int(re.search(r"MaximumAttempts = ([\d_]+);", contracts).group(1).replace("_", ""))
        self.assertEqual(1_000_000, maximum_attempts)
        for attempt_number in (129, maximum_attempts):
            with self.subTest(attempt_number=attempt_number), tempfile.TemporaryDirectory() as temp:
                value = fixture("partial")
                value["calls"].update(physicalProviderDispatches=1, logicalProviderCalls=1, retryDispatches=0)
                row = dict(physicalDispatchOrdinal=1, attemptId="scribe-attempt." + "a" * 32,
                           attemptNumber=attempt_number, providerRequestNumber=1, failureCode=None,
                           failureOrigin=None, httpStatusCode=None, codecDisposition="codec.accepted-tool")
                value["providerDiagnostics"] = dict(observations=[row], missingDispatches=0)
                raw = encode(envelope(value))
                self.assertTrue(U.valid(value))
                output = str(Path(temp) / "out.txt")
                with patch.dict(os.environ, {"GITHUB_OUTPUT": output}):
                    parsed = invoke.parse_envelope(raw, b"", 0)
                    invoke.emit_outputs(parsed, raw, 0)
                self.assertEqual(value, parsed["invocationUsage"])
                self.assertEqual(value, json.loads(driver.read_outputs(output)["usage-json"]))
                self.assertIn("codec.accepted-tool", "\n".join(U.summary_rows(value)))
                for key, invalid in (("attemptNumber", 0), ("attemptNumber", True),
                                     ("attemptNumber", maximum_attempts + 1), ("providerRequestNumber", 0),
                                     ("providerRequestNumber", True), ("providerRequestNumber", 129)):
                    with self.subTest(key=key, invalid=invalid):
                        broken = json.loads(json.dumps(value))
                        broken["providerDiagnostics"]["observations"][0][key] = invalid
                        self.assertFalse(U.valid(broken))
                        with patch.dict(os.environ, {"GITHUB_OUTPUT": output}), self.assertRaises(SystemExit):
                            invoke.parse_envelope(encode(envelope(broken)), b"", 0)

    def test_physical_diagnostics_cross_outputs_and_summary_with_closed_vocabulary_only(self):
        protocol = (HERE.parent.parent / "docs/20_architecture/validation/m3-provider-evaluation-protocol.md").read_text()
        allowlist = protocol.split("The complete first-disposition allowlist is:")[1].split(
            "`codec.response.exceeds-limit` covers")[0]
        self.assertEqual(set(U.CODECS), set(re.findall(r"`(codec\.[a-z0-9.-]+)`", allowlist)))
        value = fixture("partial")
        row = dict(physicalDispatchOrdinal=2, attemptId="scribe-attempt." + "a" * 32,
                   attemptNumber=1, providerRequestNumber=2, failureCode="model.failure.permanent-unavailable",
                   failureOrigin="model.failure-origin.http-status", httpStatusCode=402, codecDisposition=None)
        value["providerDiagnostics"] = dict(observations=[row], missingDispatches=1)
        self.assertTrue(U.valid(value))
        summary = "\n".join(U.summary_rows(value))
        self.assertIn("402", summary)
        self.assertIn("model.failure.permanent-unavailable", summary)
        self.assertNotIn(row["attemptId"], summary)
        for finish in ("length", "stop"):
            classified = json.loads(json.dumps(value))
            classified["providerDiagnostics"]["observations"][0].update(
                failureCode="model.failure.unsupported", failureOrigin="model.failure-origin.response-codec",
                httpStatusCode=None, codecDisposition="codec.finish-reason." + finish)
            self.assertTrue(U.valid(classified))
        for disposition in ("codec.arguments.not-string", "codec.arguments.duplicate-property"):
            with self.subTest(disposition=disposition), tempfile.TemporaryDirectory() as temp:
                classified = json.loads(json.dumps(value))
                classified["providerDiagnostics"]["observations"][0].update(
                    failureCode="model.failure.malformed-response", failureOrigin="model.failure-origin.response-codec",
                    httpStatusCode=None, codecDisposition=disposition)
                self.assertTrue(U.valid(classified))
                raw = encode(envelope(classified))
                output = str(Path(temp) / "out.txt")
                with patch.dict(os.environ, {"GITHUB_OUTPUT": output}):
                    parsed = invoke.parse_envelope(raw, b"", 0)
                    invoke.emit_outputs(parsed, raw, 0)
                self.assertEqual(classified, json.loads(driver.read_outputs(output)["usage-json"]))
                self.assertIn(disposition, "\n".join(U.summary_rows(classified)))
        invalid = [lambda r: r.update(httpStatusCode=True),
                   lambda r: r.update(failureOrigin=None),
                   lambda r: r.update(failureCode="private-provider-body-marker"),
                   lambda r: r.update(codecDisposition="private-reasoning-marker"),
                   lambda r: r.update(body="private-provider-body-marker"),
                   lambda r: r.update(attemptId="private-prompt-marker"),
                   lambda r: r.update(physicalDispatchOrdinal=3)]
        for mutate in invalid:
            broken = json.loads(json.dumps(value))
            mutate(broken["providerDiagnostics"]["observations"][0])
            self.assertFalse(U.valid(broken))
            self.assertEqual(["| usage | unavailable |"], U.summary_rows(broken))

    def test_latest_bounded_rows_fit_existing_envelope_and_omissions_remain_unknown(self):
        value = fixture("partial")
        value["calls"]["physicalProviderDispatches"] = 128
        value["checkpointFailure"] = dict(operationOrdinal=2**63 - 1, operation="Publication",
                                        attemptedRevision=2**63 - 1, stage="FinalRef", reason="Cancelled",
                                        transportCode="InvalidResponse", httpStatus=599, cancellation="OperationToken",
                                        refComparison=dict(expectedCommit="a" * 64, predecessorCommit="b" * 64,
                                                           observedCommit="c" * 64, relation="Other",
                                                           delivery="NeedsReadback", acknowledged=True))
        value["providerDiagnostics"] = dict(missingDispatches=112, observations=[
            dict(physicalDispatchOrdinal=n, attemptId="scribe-attempt." + "a" * 32,
                 attemptNumber=1_000_000, providerRequestNumber=128, failureCode="model.failure.permanent-unavailable",
                 failureOrigin="model.failure-origin.successful-response", httpStatusCode=None,
                 codecDisposition="codec.tool-call.arguments-exceeds-limit") for n in range(113, 129)])
        self.assertTrue(U.valid(value))
        self.assertLess(len(encode(envelope(value))), C.BOUND_ENVELOPE)
        value["providerDiagnostics"]["missingDispatches"] = 0
        self.assertFalse(U.valid(value))

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
