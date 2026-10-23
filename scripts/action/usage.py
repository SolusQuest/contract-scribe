"""Physical invocation-usage shape and bounded summary projection; no accounting."""

FIELDS = ("invocationUsageVersion", "invocationId", "campaignLineage", "snapshotId",
          "batchId", "checkpointRevision", "publicationOperationId", "generationId", "pullRequestUrl", "work", "calls", "tokens", "accounting", "history", "timing",
          "termination", "completeness", "providerDiagnostics")
WORK = ("selectedTargets", "startedTargets", "resumedTargets", "acceptedTargets",
        "skippedTargets", "failedTargets", "reconstructedTargets", "remainingTargets",
        "deferredTargets")
CALLS = ("physicalProviderDispatches", "logicalProviderCalls", "retryDispatches",
         "uncorrelatedDispatches", "toolCalls", "activeSent", "activeReserved")
TOKENS = ("input", "cachedInput", "uncachedInput", "output", "reasoning")
STOPS = ("completed", "no-op", "target-limit", "resource-limit", "context-limit",
         "awaiting-review", "closed-same-base", "changed-base", "cancelled", "timeout",
         "failure", "unknown")
FAILURES = tuple("model.failure." + suffix for suffix in (
    "transient-unavailable", "rate-limited", "permanent-unavailable", "authentication",
    "unsupported", "context-limit", "malformed-response"))
ORIGINS = tuple("model.failure-origin." + suffix for suffix in (
    "http-status", "transport", "successful-response", "response-codec"))
CODECS = tuple("codec." + suffix for suffix in (
    "accepted-tool", "accepted-terminal", "response.exceeds-limit", "json.invalid",
    "json.duplicate-property", "root.invalid", "choices.invalid", "choice.index.invalid",
    "message.invalid", "message.role.invalid", "message.content.invalid", "message.thinking-content.invalid",
    "message.refusal.invalid", "tool-calls.invalid", "tool-call.index.invalid", "tool-call.envelope.invalid",
    "tool-call.alias.invalid", "arguments.invalid", "terminal.mixed", "usage.invalid",
    "finish-reason.length", "finish-reason.stop", "finish-reason.unsupported", "finish-reason.inconsistent", "terminal.arguments-exceeds-limit",
    "continuation.missing", "tool-calls.exceeds-limit", "tool-call.arguments-exceeds-limit"))
PROVIDER_ROW = ("physicalDispatchOrdinal", "attemptId", "attemptNumber", "providerRequestNumber",
                "failureCode", "failureOrigin", "httpStatusCode", "codecDisposition")


def provider_diagnostics(value, calls):
    if value is None:
        return calls is None
    if not isinstance(value, dict) or set(value) != {"observations", "missingDispatches"} or calls is None:
        return False
    rows, missing = value["observations"], value["missingDispatches"]
    physical = calls["physicalProviderDispatches"]
    if not integer(physical) or physical > 128 or not isinstance(rows, list) or len(rows) > min(physical, 16) \
            or not integer(missing) or missing != physical - len(rows):
        return False
    previous = 0
    for row in rows:
        if not isinstance(row, dict) or set(row) != set(PROVIDER_ROW):
            return False
        ordinal = row["physicalDispatchOrdinal"]
        if not integer(ordinal) or not previous < ordinal <= physical:
            return False
        previous = ordinal
        attempt = row["attemptId"]
        if attempt is not None and (not isinstance(attempt, str) or not attempt.startswith("scribe-attempt.") \
                or len(attempt) != 47 or any(c not in "0123456789abcdef" for c in attempt[15:])):
            return False
        if not all(integer(row[key]) and 1 <= row[key] <= 128 for key in ("attemptNumber", "providerRequestNumber")):
            return False
        code, origin, status, codec = (row[key] for key in ("failureCode", "failureOrigin", "httpStatusCode", "codecDisposition"))
        if code is not None and code not in FAILURES or origin is not None and origin not in ORIGINS \
                or codec is not None and codec not in CODECS:
            return False
        if code is None and (origin is not None or status is not None):
            return False
        if (origin == "model.failure-origin.http-status") != (status is not None) \
                or status is not None and (not integer(status) or not 100 <= status <= 599):
            return False
    return True


def integer(value, nullable=False):
    return (nullable and value is None) or (type(value) is int and 0 <= value <= 2**63 - 1)


def text(value):
    return value is None or (isinstance(value, str) and len(value) <= 512
                             and not any(ord(c) < 32 or ord(c) == 127 for c in value))


def object_fields(value, fields, check):
    return value is None or (isinstance(value, dict) and set(fields) <= set(value)
                             and all(check(value[key]) for key in fields))


def charge(value):
    return isinstance(value, dict) and set(("observed", "conservativeUnobserved", "totalCharged")) <= set(value) \
        and integer(value["observed"], True) and integer(value["conservativeUnobserved"]) \
        and integer(value["totalCharged"])


def valid(value):
    if not isinstance(value, dict) or not set(FIELDS) <= set(value):
        return False
    if type(value["invocationUsageVersion"]) is not int or value["invocationUsageVersion"] != 1:
        return False
    if value["completeness"] not in ("complete", "partial", "unknown"):
        return False
    if not all(text(value[key]) for key in ("invocationId", "campaignLineage", "snapshotId", "batchId", "publicationOperationId", "generationId", "pullRequestUrl")):
        return False
    if not integer(value["checkpointRevision"], True):
        return False
    if not object_fields(value["work"], WORK, integer) \
            or not object_fields(value["calls"], CALLS, lambda n: integer(n, True)):
        return False
    if not provider_diagnostics(value["providerDiagnostics"], value["calls"]):
        return False
    def token(item):
        return isinstance(item, dict) and {"observed", "missingDispatches"} <= set(item) \
            and integer(item["observed"], True) and integer(item["missingDispatches"])
    if not object_fields(value["tokens"], TOKENS, token):
        return False
    accounting = value["accounting"]
    if accounting is not None:
        if not isinstance(accounting, dict) or not set(TOKENS[:4] + ("unsettledDispatches", "activeExposure")) <= set(accounting):
            return False
        if not all(charge(accounting[key]) for key in TOKENS[:4]) or not integer(accounting["unsettledDispatches"]):
            return False
        if not object_fields(accounting["activeExposure"],
                             ("inputTokens", "cachedInputTokens", "uncachedInputTokens", "outputTokens", "elapsedMilliseconds"), integer):
            return False
    if not object_fields(value["history"], ("providerRequests",) + TOKENS, charge):
        return False
    if not object_fields(value["timing"], ("cliElapsedMilliseconds", "allowanceElapsedMilliseconds"),
                         lambda n: integer(n, True)):
        return False
    terminal = value["termination"]
    if terminal is not None and (not isinstance(terminal, dict)
            or not {"stopReason", "source", "allowanceStopReason", "scribeFinishReason"} <= set(terminal)
            or terminal["stopReason"] not in STOPS
            or not all(text(terminal[key]) for key in ("source", "allowanceStopReason", "scribeFinishReason"))):
        return False
    return True


def summary_rows(value):
    """Render only checked numbers and closed vocabulary, never identities/raw strings."""
    if not valid(value):
        return ["| usage | unavailable |"]
    def number(n):
        return "unknown" if n is None else str(n)
    rows = [f"| usage | {value['completeness']} |"]
    calls = value["calls"]
    if calls is not None:
        rows += [f"| provider sends / logical calls / retries | {number(calls['physicalProviderDispatches'])} / "
                 f"{number(calls['logicalProviderCalls'])} / {number(calls['retryDispatches'])} |",
                 f"| tool calls | {number(calls['toolCalls'])} |"]
    work = value["work"]
    if work is not None:
        rows += [f"| selected / new starts / accepted / remaining | {work['selectedTargets']} / "
                 f"{work['startedTargets']} / {work['acceptedTargets']} / {work['remainingTargets']} |",
                 f"| resumed / reconstructed | {work['resumedTargets']} / {work['reconstructedTargets']} |"]
    if value["tokens"] is not None:
        for key in TOKENS:
            item = value["tokens"][key]
            rows.append(f"| {key} tokens (reported subtotal; missing sends) | "
                        f"{number(item['observed'])}; {item['missingDispatches']} |")
    if value["termination"] is not None:
        rows.append(f"| stop | {value['termination']['stopReason']} |")
    diagnostics = value["providerDiagnostics"]
    if diagnostics is not None:
        rows.append(f"| provider diagnostics missing sends | {diagnostics['missingDispatches']} |")
        for item in diagnostics["observations"]:
            rows.append(f"| provider send {item['physicalDispatchOrdinal']} classification | "
                        f"{item['failureCode'] or 'unknown'} / {item['failureOrigin'] or 'unknown'} / "
                        f"{number(item['httpStatusCode'])} / {item['codecDisposition'] or 'unknown'} |")
    if value["timing"] is not None:
        rows.append(f"| CLI / allowance elapsed ms | {number(value['timing']['cliElapsedMilliseconds'])} / "
                    f"{number(value['timing']['allowanceElapsedMilliseconds'])} |")
    return rows
