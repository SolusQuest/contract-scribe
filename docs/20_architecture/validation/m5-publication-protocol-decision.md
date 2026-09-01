# M5 publication protocol decision

> **Decision:** Select an adapter-owned coordination ref with exact GraphQL
> `updateRefs` compare-and-swap. Reject a managed Issue/comment ledger and an
> externally serialized caller as the initial M5 authority.

## Executable evidence

The corrected transient serialized decision harness is reviewable at commit
`f6bbf08c35ba0f6a45b21e22f7d0741f3153870a` on the Issue #158 branch. It
supersedes the earlier comparison at `5009ff42b53584d6b49c2c2ef71b5f1fdfea9a4d`,
whose rejected alternatives did not traverse an equivalent executable boundary.
The final
accepted tree intentionally removes its executable rejected alternatives. It was
executed from that committed source with:

```text
dotnet test tests/ContractScribe.Tests/ContractScribe.Tests.csproj --no-build --filter "FullyQualifiedName~GitHubPublicationProtocolDecisionHarnessTests" --logger "console;verbosity=normal"
```

Result: one harness test passed. It executed 12 scenarios against each of three
viable implementations behind the same JSON-serialized request/response and
fault-injection server, producing 36 bounded observations and serialized request
transcripts. Verdicts were derived from observed admission, mutation, recovery,
and residual state rather than the alternative name. The selected path satisfied
all twelve. The decision record keeps
the complete outcome matrix and exact transcript shapes; the final fixture tree
keeps only reusable selected-path vectors.

## Complete outcome matrix

`pass` means the alternative met the required one-winner, exact-predecessor,
response-loss, or bounded-residual invariant for that vector. A failing rejected
alternative remains evidence about that alternative; it is not a product test.

| Vector | coordination ref | managed Issue ledger | external serializer |
|---|---:|---:|---:|
| Initial-create response loss | pass: exact ref readback | pass: exact comment readback; election is not final | pass: exact lease readback; external prerequisite |
| Stale predecessor | pass: exact CAS rejects | pass: repository gate rejects before comment | pass: repository gate rejects before lease |
| Coordination ancestor rewind | pass: exact CAS rejects | pass: repository gate rejects before comment | pass: repository gate rejects before lease |
| Proposal ancestor rewind | pass: proposal ref has its own exact CAS | pass: proposal CAS rejects after ledger admission | pass: proposal CAS rejects after external admission |
| Target move before claim | pass: authenticated reread, zero write | pass: zero ledger write | pass: zero caller dispatch |
| Target move after claim, before resource | pass: step reread, zero resource write | pass: step reread, zero resource write | pass: step reread; external prerequisite |
| Target move during PR create | pass: one marker-owned stale draft | pass: one discovered marker-owned stale draft | pass: one discovered marker-owned stale draft |
| Two first-publication invocations | pass: one all-zero CAS winner | fail: both append durable claims before a non-final election | pass: one external lease winner |
| Two append invocations | pass: one predecessor CAS winner | fail: both append durable claims before a non-final election | pass: one external lease winner |
| Ambiguous commit response | pass: expected content OID discovery | pass: expected content OID discovery | pass: expected content OID discovery |
| Ambiguous proposal-ref response | pass: exact head readback | pass: exact head readback | pass: exact head readback |
| Ambiguous PR response | pass: exhaustive immutable-marker discovery | pass: exhaustive immutable-marker discovery | pass: exhaustive immutable-marker discovery |

Every alternative crossed the same serialized boundary. For example, a
coordination mutation was represented as:

```json
{
  "Kind": "graphql-update-ref",
  "OperationId": "operation-a",
  "Resource": "coordination",
  "BeforeOid": "<exact predecessor or forty zeroes>",
  "AfterOid": "<exact nonzero successor>",
  "LoseResponse": false
}
```

The managed-Issue implementation used append-comment, full readback, and a
deterministic election. Both racing claims become durable before election, so
the loser cannot be made un-admitted and the authority is not final. The
external implementation used acquire/readback of an actual serialized lease and
passed the exercised vectors, but its winner is neither repository-bound nor
available without an external service. These are observed tradeoffs, not
alternatives made to fail by construction.

## Platform facts and minimum choice

GitHub GraphQL documents `RefUpdate.beforeOid` as the expected current object ID,
with forty zeroes representing expected absence; `afterOid` selects the new
object and forty zeroes would delete, so deletion is rejected by this contract.
`force=false` does not replace the exact predecessor check. The REST Git refs
create/update requests carry the destination SHA and optional force behavior but
not an exact previously observed SHA, so REST cannot enforce the tested
ancestor-rewind invariant.

References:

- [GitHub GraphQL Git objects and `updateRefs`](https://docs.github.com/en/graphql/reference/git)
- [GitHub REST Git refs](https://docs.github.com/en/rest/git/refs)

One coordination ref is the smallest current repository-bound admission
authority because it uses the existing Git database and one exact atomic
mutation. The proposal ref remains independent mutable authority and therefore
uses its own exact CAS for create and advance; safety is not inherited from the
coordination claim.

## Permissions and prerequisites

The selected adapter will require repository metadata/content read, pull-request
read/write, and Git-ref/object mutation permissions sufficient for GraphQL
`updateRefs`, Git data, and draft PR creation. It requires the deterministic
coordination/proposal ref namespaces and marker convention, but no Issues write,
managed Issue, external locking service, Action wrapper, database, or migration
family. R1 itself reads no credential and performs no network mutation.

The managed-Issue alternative adds Issues write permission and a separately
reconciled append-only ledger whose post-write election cannot provide final
single-winner admission. The external serializer is viable under the tested
faults, but adds an operational dependency whose lease is not durable repository
state and still needs repository predecessor validation. Neither is the minimum
initial protocol.

## Exact sequencing and remaining race

After local credential-free admission, the future adapter performs a complete
authenticated read, claims the coordination ref once, reads it back, then gates
each later content/ref/PR request with a fresh exact claim/predecessor/base read.
It performs at most one bounded mutation and directly reads that resource back
before the next create. Drift visible before a request means zero further write.

GitHub PR creation names a base branch rather than accepting an expected base
commit CAS. Therefore the final target read and server-side PR processing retain
one race window. If the base moves during create processing, exhaustive marker
discovery may find one exact owned draft bound to the moved base. The result is
`stale-base-after-create`; it records no completed transition and forbids
append/update/adoption/automatic close/second create while that draft remains.
This bounded residual is explicit rather than claimed away.

## Final-tree cleanup and deferrals

The accepted head retains:

- this bounded decision record;
- [GitHub Publication v1](../contracts/github-publication-v1.md);
- canonical commitment and selected coordination/base-byte fixtures;
- Core contracts, validation, commitments, results, and one port.

It contains no executable managed-Issue/external-serializer alternative, no
`protocol-decision` fixture/test product, no REST ref mutation authority, and no
ordinary-CI dependency on the experiment.

Production GitHub transport, authenticated tree/blob/mode reads, deterministic
Git object creation, ref execution, PR reconciliation, orchestration, CLI
grammar/composition, Action packaging, authorized live proof, rebase, ready,
merge, close, and ref deletion remain later M5/M6 work. They cannot silently
change this accepted semantic contract.
