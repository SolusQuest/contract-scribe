# GitHub Publication v1

> **Status:** Accepted pre-release M5-R1 contract. This document freezes the
> Core-owned publication authority, validation, commitment, result, and adapter
> port boundary. It does not implement GitHub transport or mutation.

## Purpose and ownership

GitHub Publication v1 is the closed boundary between the M5-H1 CLI producer and
the future GitHub adapter. Core owns four things:

1. a credential-free, source-free caller authority;
2. a separate, defensive-copy byte payload correlated to that authority;
3. an authenticated prepared-operation commitment used by the adapter after its
   complete remote read;
4. a closed structured result and one adapter port.

Campaign State v1 remains platform neutral and unchanged. The CLI must project
every and only the accepted M2/M4 changed paths. In particular,
`DocumentationPatchAcceptedCandidate.Files` is the complete governed candidate
and may include unchanged files; publishing that enumerable wholesale is
invalid.

## Two-stage admission

### Credential-free caller authority

`ValidatedGitHubPublicationAuthority` is constructible without a credential,
network, filesystem, or ambient Git state. It binds:

- repository owner/name, target ref, and configured expected base commit;
- campaign lineage, snapshot, execution and work-plan commitments;
- checkpoint revision/digest, candidate, Patch Request, Patch Result and
  accepted-projection commitments;
- the actual M4 campaign ceilings accepted for that checkpoint, not merely M5
  policy maxima;
- stable operation ID, logical generation and predecessor identities;
- every and only changed repository path with exact original/candidate full-file
  SHA-256 and M4 cumulative observations;
- for append, the complete preceding published-path map and its exact candidate
  SHA-256 values;
- immutable publication policy and optional exact closed-successor authority.

It cannot contain an observed tree OID, Git entry type/mode, current ref head,
proposal commit/tree, pull-request observation, token, request, response, source
bytes, or exception.

### Authenticated prepared operation

After credential admission, the adapter performs a complete authenticated read
and prepares a separate operation. Its observations include canonical repository
identity, observed target commit/base tree, entry OIDs/types/modes, coordination
and proposal heads, proposal commit/parent/tree, active pull-request state, and
the exact observed predecessor selected by reconciliation. These facts are never
trusted from the caller authority.

The prepared-operation commitment combines those observations, the unchanged
caller authority and policy commitments, deterministic coordination/proposal Git
payloads, ownership markers, and the exact draft-PR request. Changing any fact
that can select a different remote mutation changes the commitment. An exact
retry recreates byte-identical payloads and object identities.

## Policy

The policy has positive maximum documentation blocks, distinct changed files,
and cumulative patch bytes per pull-request generation. The caller also supplies
the actual accepted M4 ceiling projection for this checkpoint. Validation binds
that projection into the authority commitment and proves every M5 maximum is no
greater than the corresponding actual M4 ceiling. Comparing only global product
maxima is invalid. Both projections are immutable for the lifetime of the
generation; a mismatch is incompatible, not migrated.

The patch-byte observation is exactly:

```text
checked sum of CandidateDocumentationByteCount
over the complete distinct changed-file observation set
```

It is not Git blob size, diff size, original-plus-candidate size, absolute byte
delta, or the latest append delta. Closed-unmerged is terminal and has no
automatic retry, adoption, close, or successor.

## Changed files and byte payload

Authority paths are canonical repository-relative paths ordered ordinally.
Missing, extra, duplicate, ordinal-ignore-case-colliding, absolute, traversing,
or backslash paths fail locally. Original and candidate SHA-256 values are exact
lowercase hexadecimal.

`ValidatedGitHubChangedFilePayload` is a distinct input. Admission requires the
exact authority path set, recomputes every candidate SHA-256, and accepts at most
16,777,216 bytes per file and 67,108,864 bytes in aggregate. Counts and checked
length sums are validated before allocating or copying; each admitted buffer is
copied exactly once. Its bytes are excluded from authority and operation serialization,
commitments, results, `ToString()`, diagnostics, persistence, and logs.

At authenticated preparation:

- the authenticated observation supplies the complete flattened base tree, up
  to 100,000 entries, and its locally recomputed Git tree OID must equal the
  observed base-tree OID;
- first/new-generation publication requires every governed changed path in that
  tree to be a regular blob whose full bytes match M4 original SHA-256;
- `100644` and `100755` are the only accepted modes and are preserved;
- missing governed entries, duplicate paths, symbolic links, submodules,
  unsupported changed-entry modes/types, and original-byte drift fail before
  mutation;
- append requires the exact owned proposal head/commit/parent/tree; previously
  published paths must match their recorded preceding cumulative-candidate
  hashes, while newly introduced paths still match M4 original hashes;
- append's preceding path map must equal the complete cumulative prior path set,
  so an omitted prior path cannot be silently downgraded to a new path;
- candidate blobs are built from the exact byte buffers. UTF-8, BOM, UTF-16, and
  arbitrary binary bytes are never transcoded.

## Transition authority

The closed transitions are:

- `initial`: no logical predecessor, preceding candidate, or successor authority;
- `same-snapshot-append`: exact logical generation, predecessor/preceding
  operation, and preceding cumulative-candidate commitment;
- `successor-after-merge`: exact terminal predecessor, fresh M4 authority, and a
  new generation;
- `successor-after-closed-unmerged`: the same fresh facts plus explicit
  non-secret maintainer authorization.

Closed-unmerged authorization binds a stable authorization ID, exact closed PR,
generation and head, fresh snapshot/work-plan/candidate commitments, new
generation, and stable operation ID. It permits only exact replay of that one
operation and must be absent from every other transition.

## Commitments and deterministic Git payloads

All product commitments use SHA-256 with a domain tag and big-endian
length-framed strict UTF-8 fields. Git object OIDs remain their separate
40-lowercase-hex domain on the current GitHub path. The forty-zero Git OID means
expected ref absence only and is never a product commitment or permitted
`afterOid`.

Ref names are derived only from bounded lowercase commitment keys:

```text
refs/heads/contract-scribe/coordination/<campaign-sha256>
refs/heads/contract-scribe/proposals/<campaign-sha256>/<generation-sha256>
```

Raw caller display text never enters a ref. Canonical coordination state uses
strict UTF-8 without BOM and LF, a fixed key order, lowercase hash/OID values,
and only version, identities, commitments, counts, state, exact predecessor and
result OIDs, the complete cumulative changed-path/candidate-hash map, and bounded
sanitized diagnostics. On append, the authenticated state map must exactly equal
the caller's preceding map; removing a path from both caller collections cannot
downgrade it to unpublished. The state contains no source or secret.

Coordination and proposal commits are fully materialized locally. Blob OIDs,
recursive tree OIDs, and commit OIDs are the real Git SHA-1 identities computed
from canonical Git object headers and bytes. Proposal construction overlays the
authorized candidate blobs and fixed marker on the complete observed base tree,
preserving every unchanged entry and mode. Coordination construction uses only
the canonical state and marker. Both commits have exactly one nonzero parent,
the fixed message and marker, actor `ContractScribe`, email
`contract-scribe@users.noreply.github.com`, timestamp `946684800 +0000`
(2000-01-01T00:00:00Z), and byte-exact
payloads exposed by the prepared operation. Forty-zero expected absence is a ref
CAS sentinel, never a parent. No server-selected time or server-selected object
representation is permitted.

The PR request binds owned head, configured base ref, exact title hash, immutable
body-marker hash, `draft=true`, and `maintainer_can_modify=false`. Immutable PR
prose carries ownership, campaign/generation/snapshot/policy/operation markers;
mutable proposal head, latest operation, cumulative counts, and current
validation facts remain authoritative in Git state rather than being rewritten
into immutable prose.

## Atomic admission and step gates

Every coordination-ref creation/advance and proposal-ref creation/advance uses
one GraphQL `updateRefs` request containing exactly one `RefUpdate`:

```text
beforeOid = exact observed predecessor, or forty zeroes for expected absence
afterOid  = exact nonzero expected successor
force     = false
```

REST ref create/update is not an admission or ref-write primitive. Stale
predecessors, ancestor rewinds, competing successors, and zero `afterOid` fail.
An ambiguous response is accepted only after exact direct readback proves the
expected successor and operation.

The operation sequence is normative:

```text
credential-free complete preparation
  -> authenticated complete read
  -> atomic coordination claim
  -> direct claim readback
  -> before each later create, exact claim/predecessor/base reread
  -> at most one bounded content/ref/PR mutation
  -> direct resource readback
  -> record exact completed step or residual
  -> only then permit the next create
```

Visible drift means zero further mutation. PR create is permitted only after
exact proposal-ref readback and a final exact claim/base read. Base movement
visible before the request writes nothing. Base movement during GitHub PR-create
processing may leave exactly one marker-owned draft; it is reported as
`stale-base-after-create`, records no completed transition, and blocks append,
update, adoption, automatic close, and a second create while active.

## Results and port

The closed result vocabulary is `local-invalid`, `replay-no-op`, `admitted`,
`recovered-content-partial`, `recovered-ref-partial`, `published`,
`awaiting-review`, `merged`, `closed-unmerged`, `stale-base-after-create`,
`stale`, `human-change`, `conflict`, `permission`, `rate-limit`, `cancelled`,
`timeout`, and `host-failure`.

Kinds carry only their exact structured detail. `local-invalid` identifies its
field with a closed enum rather than caller-controlled text. Content and ref partials identify
expected/observed OIDs and operation; admitted identifies the exact claim;
published states identify generation ref/PR; stale draft identifies exact PR,
marker, owned head, expected/observed base, generation and operation. Local and
remote failures expose bounded codes/classifications only. Raw exceptions,
requests, responses, credentials, and payloads are not representable.

The only port is:

```csharp
ValueTask<GitHubPublicationResult> PublishAsync(
    ValidatedGitHubPublicationAuthority authority,
    ValidatedGitHubChangedFilePayload payload,
    CancellationToken cancellationToken);
```

Cancellation is execution control and is not committed. Token acquisition,
timeouts/clocks, HTTP construction, pagination, Git object creation, GraphQL and
PR execution belong to later adapter leaves.
