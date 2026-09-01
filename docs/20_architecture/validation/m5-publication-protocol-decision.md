# M5 publication protocol decision

> **Decision:** Select an adapter-owned coordination ref with exact GraphQL
> `updateRefs` compare-and-swap. Reject a managed Issue/comment ledger and an
> externally serialized caller as the initial M5 authority.

## Executable evidence

The final corrected transient serialized decision harness is reviewable at
commit `8f8be093651526a7ef7b8c25cfeaa6b20ba9d126` on the Issue #158 branch. It
supersedes the comparison at `01efbef97dfdd035db3963b63e3626a6945f7298`,
whose target-movement vectors moved the target before the relevant read rather
than inside the read/write window, as well as the earlier comparisons at
`5009ff42b53584d6b49c2c2ef71b5f1fdfea9a4d` and
`f6bbf08c35ba0f6a45b21e22f7d0741f3153870a`: the first did not give rejected
alternatives an equivalent executable boundary, while the second still read the
repository outside that boundary and partly accepted implementation-supplied
finality. The final accepted tree intentionally removes executable rejected
alternatives. The committed source was executed with:

```text
dotnet test tests/ContractScribe.Tests/ContractScribe.Tests.csproj --no-build --filter "FullyQualifiedName~GitHubPublicationProtocolDecisionHarnessTests" --logger "console;verbosity=normal"
```

Result: one harness test passed. It executed 14 scenarios against each of three
bounded implementations behind the same JSON-serialized request/response and
fault-injection server, producing 42 bounded observations and serialized request
transcripts. Every predecessor/target read traversed the server. Stale and rewind
faults were injected after the final successful read and before each
alternative's admission mutation. Target movement was separately injected after
a successful final read and before admission, after a successful step gate and
before immutable content creation, and after a successful step gate and before
proposal-ref CAS. Each path performed exact residual readback and then proved a
later gate rejected continuation. Finality came from a later durable observation,
including a late Issue claimant, rather than an alternative property. PR create
carried expected base and discovery returned observed base. The selected path
satisfied all twelve. The final fixture tree keeps only reusable selected-path
vectors.

## Complete outcome matrix

`pass` means the alternative met the required one-winner, exact-predecessor,
response-loss, or bounded-residual invariant for that vector. A failing rejected
alternative remains evidence about that alternative; it is not a product test.

| Vector | coordination ref | managed Issue ledger | external serializer |
|---|---:|---:|---:|
| Initial-create response loss | pass: exact ref readback remains final | fail: exact comment exists but late claimant changes election | pass: exact lease readback; external prerequisite |
| Stale predecessor after final read | pass: exact CAS rejects | fail: comment writes after repository drift | fail: lease writes after repository drift |
| Coordination ancestor rewind after final read | pass: exact CAS rejects | fail: comment writes after rewind | fail: lease writes after rewind |
| Proposal ancestor rewind | pass: proposal ref has its own exact CAS | pass: proposal CAS rejects after ledger admission | pass: proposal CAS rejects after external admission |
| Target move after final read, before claim | pass: one exact claim, post-claim gate, zero content/ref/PR mutation | pass: one ledger claim and zero resource mutation, but ledger finality still fails | pass: one lease claim and zero resource mutation; external prerequisite |
| Target move after step read, before content write | pass: one deterministic orphan content object, exact readback, zero later ref/PR mutation | pass: same bounded content residual after ledger admission | pass: same bounded content residual; external prerequisite |
| Target move after step read, before proposal-ref write | pass: one exact old-base generation ref, exact readback, zero PR mutation | pass: same bounded ref residual after ledger admission | pass: same bounded ref residual; external prerequisite |
| Target move during PR create | pass: one marker-owned stale draft | pass: one discovered marker-owned stale draft | pass: one discovered marker-owned stale draft |
| Two first-publication invocations | pass: one all-zero CAS winner | fail: both claims durable; later claimant changes election | pass: one external lease winner, external prerequisite |
| Two append invocations | pass: one predecessor CAS winner | fail: both claims durable; later claimant changes election | pass: one external lease winner, external prerequisite |
| Ambiguous commit response | pass: expected content OID discovery | pass: expected content OID discovery | pass: expected content OID discovery |
| Ambiguous proposal-ref response | pass: exact head readback | pass: exact head readback | pass: exact head readback |
| Ambiguous PR response | pass: exhaustive immutable-marker discovery | pass: exhaustive immutable-marker discovery | pass: exhaustive immutable-marker discovery |

Every alternative performed its final repository read and admission mutation
through the same serialized server. The server injected stale/rewind between
those two requests. For example, a coordination mutation was represented as:

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

The managed-Issue implementation used append-comment, full readback and a
deterministic election. The server then appended a later durable claimant and
re-ran election; the winner changed, proving non-finality from observed state.
The external implementation used acquire/readback of an actual serialized lease.
It produced one lease winner, but stale/rewind injected after its final repository
read still allowed the lease mutation because repository predecessor and lease
ownership are different atomic authorities. It therefore fails repository-bound
admission in addition to requiring an external service. These are executed
tradeoffs, not implementation-supplied verdicts.

A one-ref CAS cannot atomically compare the independently mutable target ref.
The experiment therefore does not rename a target move inside that window to a
zero-write rejection. Admission may leave one exact claim; content creation may
leave one deterministic unreachable object; proposal CAS may leave one exact
old-base generation ref. A fresh read immediately after each mutation discovers
that residual, and the next repository gate forbids every later content/ref/PR
mutation. The selected coordination state is then terminalized as `stale` by its
own exact predecessor CAS. Only a newly attested initial operation on the current
target, in a new generation, may replace that exact pre-PR stale state. It may
neither adopt nor advance the old content/ref residual. This is the bounded
continuation proved by the target-movement vectors.

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
single-winner admission. The external serializer adds an operational dependency;
its lease is not durable repository state and cannot atomically bind the final
repository predecessor read, so the executed read-to-admission stale/rewind
window remains open. Neither is the minimum initial protocol.

## Exact sequencing and remaining race

After local credential-free admission, the future adapter performs a complete
authenticated read, claims the coordination ref once, reads it back, then gates
each later content/ref/PR request with a fresh exact claim/predecessor/base read.
It performs at most one bounded mutation and directly reads that resource back
before the next create. Drift visible before a request means zero further write.
Drift first observed after a request permits only exact residual discovery and
one coordination-only stale terminalization CAS; it never permits another
content, proposal-ref or PR mutation for that operation.

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
