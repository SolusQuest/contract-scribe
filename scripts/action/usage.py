"""Physical invocation-usage shape and bounded summary projection; no accounting."""

FIELDS = ("invocationUsageVersion", "invocationId", "campaignLineage", "snapshotId",
          "batchId", "checkpointRevision", "publicationOperationId", "generationId", "pullRequestUrl", "work", "calls", "tokens", "accounting", "history", "timing",
          "termination", "completeness")
WORK = ("selectedTargets", "startedTargets", "resumedTargets", "acceptedTargets",
        "skippedTargets", "failedTargets", "reconstructedTargets", "remainingTargets",
        "deferredTargets")
CALLS = ("physicalProviderDispatches", "logicalProviderCalls", "retryDispatches",
         "uncorrelatedDispatches", "toolCalls", "activeSent", "activeReserved")
TOKENS = ("input", "cachedInput", "uncachedInput", "output", "reasoning")
STOPS = ("completed", "no-op", "target-limit", "resource-limit", "context-limit",
         "awaiting-review", "closed-same-base", "changed-base", "cancelled", "timeout",
         "failure", "unknown")


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
    if value["timing"] is not None:
        rows.append(f"| CLI / allowance elapsed ms | {number(value['timing']['cliElapsedMilliseconds'])} / "
                    f"{number(value['timing']['allowanceElapsedMilliseconds'])} |")
    return rows
