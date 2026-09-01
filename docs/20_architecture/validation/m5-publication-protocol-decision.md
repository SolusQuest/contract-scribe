# M5 publication protocol decision

> **Decision:** Select an adapter-owned coordination ref with exact GraphQL
> `updateRefs` compare-and-swap. Reject a managed Issue/comment ledger and an
> externally serialized caller as the initial M5 authority.

## Executable evidence

The transient serialized decision harness is reviewable at commit
`5009ff42b53584d6b49c2c2ef71b5f1fdfea9a4d` on the Issue #158 branch. The final
accepted tree intentionally removes its executable rejected alternatives. It was
executed from that committed source with:

```text
dotnet test tests/ContractScribe.Tests/ContractScribe.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~GitHubPublicationProtocolDecisionHarnessTests" --logger "trx;LogFileName=issue-158-harness-committed.trx"
```

Result: one harness test passed. It executed 12 scenarios against each of three
alternatives, producing 36 bounded observations and serialized request
transcripts. The selected path satisfied all twelve. The decision record keeps
the complete outcome matrix and exact transcript shapes; the final fixture tree
keeps only reusable selected-path vectors.

## Complete outcome matrix

`pass` means the alternative met the required one-winner, exact-predecessor,
response-loss, or bounded-residual invariant for that vector. A failing rejected
alternative remains evidence about that alternative; it is not a product test.

| Vector | coordination ref | managed Issue ledger | external serializer |
|---|---:|---:|---:|
| Initial-create response loss | pass: exact ref readback | fail: append may duplicate | fail: no durable repository proof |
| Stale predecessor | pass: exact CAS rejects | fail: body patch lacks predecessor CAS | fail: caller lock does not validate repository predecessor |
| Coordination ancestor rewind | pass: exact CAS rejects | fail: issue update is independent | fail: external lock is independent |
| Proposal ancestor rewind | pass: proposal ref has its own exact CAS | pass only as observation, not admission authority | pass only with external prerequisite |
| Target move before claim | pass: authenticated reread, zero write | pass: zero ledger write | pass: zero caller dispatch |
| Target move after claim, before resource | pass: step reread, zero resource write | pass only with extra reconciliation | pass only with external prerequisite |
| Target move during PR create | pass: one marker-owned stale draft | pass only with ledger plus PR discovery | pass only with external prerequisite |
| Two first-publication invocations | pass: one all-zero CAS winner | fail: two issue mutations admitted | fail: no repository-bound winner proof |
| Two append invocations | pass: one predecessor CAS winner | pass only with non-atomic application logic | fail: two independent callers can proceed |
| Ambiguous commit response | pass: expected content OID discovery | pass only with separate content recovery | pass only with external prerequisite |
| Ambiguous proposal-ref response | pass: exact head readback | pass only with separate ref recovery | pass only with external prerequisite |
| Ambiguous PR response | pass: exhaustive immutable-marker discovery | pass only with separate PR discovery | pass only with external prerequisite |

The harness serialized the coordination mutation as this exact boundary:

```json
{
  "query": "mutation($refUpdates:[RefUpdate!]!){updateRefs(input:{repositoryId:\"repository\",refUpdates:$refUpdates}){clientMutationId}}",
  "variables": {
    "refUpdates": [
      {
        "name": "refs/heads/contract-scribe/coordination/campaign",
        "beforeOid": "<exact predecessor or forty zeroes>",
        "afterOid": "<exact nonzero successor>",
        "force": false
      }
    ]
  }
}
```

The managed-Issue transcript serialized
`PATCH /issues` with `operationId` and `expectedPredecessor`; the request has no
atomic comparison against the previously read body. The external-serializer
transcript recorded caller admission but no durable repository predecessor
comparison. These are the exercised differences, not assumptions about the
alternatives.

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
reconciled mutable ledger without supplying a one-request exact-body CAS. The
external serializer adds an operational dependency whose lock is not durable
repository state and still needs repository predecessor validation. Neither is
the minimum initial protocol.

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
