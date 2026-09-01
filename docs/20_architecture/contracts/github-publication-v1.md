# GitHub Publication v1

> **Status:** Accepted pre-release M5-R1 contract. This document freezes the
> Core-owned source-free authority, policy, commitments, closed results, one
> port, and the normative requirements handed to R2-R6. It does not implement
> Git objects, GitHub observations, reconciliation, PR metadata, transport, or
> mutation in Core.

## Boundary and ownership

GitHub Publication v1 is the closed boundary between the M5-H1 CLI producer and
the future GitHub adapter. Core owns:

1. credential-free, source-free caller authority and local validation;
2. a separate defensive-copy byte payload correlated to that authority;
3. domain-separated policy, authority, operation, ref-identity commitments;
4. a closed structured result and one adapter port.

Campaign State v1 remains platform-neutral. Core does not expose authenticated
remote observations or a prepared remote operation. It does not serialize Git
blobs, trees or commits, overlay proposal trees, construct PR text, choose an
authenticated transition, or reconcile remote state. Those are the independent
R3-R6 authorities described below; adding them to Core would create a second
production implementation and is forbidden.

## Credential-free authority

`ValidatedGitHubPublicationAuthority` binds:

- repository owner/name, target ref, configured expected base commit;
- campaign lineage, snapshot, execution and work-plan commitments;
- checkpoint revision/digest, candidate, Patch Request, Patch Result and
  accepted-projection commitments;
- actual accepted M4 ceilings and the subordinate immutable M5 policy;
- stable operation ID, generation, transition and predecessor identities;
- every and only changed path with exact original/candidate full-file SHA-256
  and cumulative M4 observations;
- for append, the complete preceding published-path/candidate-hash map, the
  distinct preceding candidate commitment, preceding authority commitment and
  preceding operation ID;
- for a closed-unmerged successor only, the exact explicit authorization.

Authority identifiers use the marker-safe grammar `[A-Za-z0-9._-]+` and the
existing scalar bound. Whitespace, Unicode formatting characters, `=`, HTML
comment delimiters, controls, and all other characters fail before a commitment,
credential, filesystem access, or request. Raw identifiers therefore cannot
escape line or HTML metadata later. Repositories and refs retain their own
stricter grammars.

The authority contains no tree/blob/mode observation, current remote head,
proposal object, PR observation, token, request, response, source bytes, or
exception.

## Policy and payload

M5 maxima for documentation blocks, distinct changed files and cumulative patch
bytes are positive and no greater than the actual accepted M4 ceilings supplied
for the checkpoint. Both projections enter the policy commitment and are
immutable for the generation. The byte observation is the checked sum of
`CandidateDocumentationByteCount` over the complete distinct changed-file set;
it is not a Git blob/diff measure or the newest append delta.

Authority paths are canonical repository-relative paths ordered ordinally.
Missing, extra, duplicate, ordinal-ignore-case-colliding, absolute, traversing,
or backslash paths fail locally. Hashes are exact lowercase SHA-256.

`ValidatedGitHubChangedFilePayload` is a separate nonpersistent input. Admission
requires the exact authority path set, recomputes each candidate SHA-256, and
accepts at most 16,777,216 bytes per file and 67,108,864 bytes in aggregate.
Counts and checked length sums are validated before allocation; each admitted
buffer is copied once. Payload bytes are absent from authority/result text,
commitments, diagnostics, logs and persistence. Core validates caller-supplied
bytes; it does not read source files or create Git objects.

## Local transition authority

The closed caller transitions are:

- `initial`: no preceding operation/authority/candidate, path map, terminal
  predecessor or closed authorization;
- `same-snapshot-append`: exact preceding operation, authority, candidate and
  nonempty complete preceding path map; no terminal predecessor/authorization;
- `successor-after-merge`: structured merged predecessor, fresh M4 authority,
  new generation, and no closed authorization;
- `successor-after-closed-unmerged`: structured closed predecessor, fresh M4
  authority, new generation, and exact explicit authorization.

Closed-unmerged authorization binds stable authorization ID, exact closed PR,
generation/head, fresh snapshot/work-plan/candidate commitments, new generation
and stable operation. It permits exact replay of that operation only.

These are caller claims, not authenticated GitHub facts. R6 may select a
successor transition only after R5 supplies an exact terminal capability binding
repository, predecessor PR number, generation, head ref/OID, base, immutable
marker, bot ownership, and the exact `merged` or `closed-unmerged` disposition,
and after proving no active predecessor remains. A caller-supplied disposition
alone never authorizes a successor.

## Commitments and ref identities

Product commitments use SHA-256 with domain tags and big-endian length-framed
strict UTF-8 fields. Every fact that can select a different future mutation
changes the authority or operation commitment. Append's preceding candidate
commitment is independent of the path map and is committed separately. Exact
retry is stable.

Ref identities contain only lowercase commitment keys:

```text
refs/heads/contract-scribe/coordination/<campaign-sha256>
refs/heads/contract-scribe/proposals/<campaign-sha256>/<generation-sha256>
```

The forty-zero Git OID means expected ref absence only; it is never an
`afterOid`, object parent, or product commitment.

## Normative R3 coordination representation

R3 owns canonical state bytes, coordination Git objects/ref, their validation,
and replay. The canonical state is strict UTF-8 without BOM, LF terminated,
fixed-key-order JSON with no unknown keys. It persists the raw stable operation
ID and its commitment as distinct fields, plus repository ID, target ref/base,
authority/policy/current-candidate commitments, nullable preceding
authority/candidate/operation, generation/transition, exact coordination and
proposal predecessor/result OIDs, exact PR residual identity when present, and
the complete cumulative changed-path/candidate-hash map.

Its closed stages are:

```text
claimed
content-created
proposal-ref-advanced
pr-created
published
awaiting-review
stale-draft
merged
closed-unmerged
```

Each stage has a closed required/forbidden field matrix. Later stages retain all
earlier exact identities; no caller-controlled diagnostic is correctness-bearing.
R3 fixtures must freeze bytes and object identities for initial claim, exact
replay, append claim, content partial, ref partial, PR-create residual,
published/awaiting-review, stale draft, merged and closed-unmerged. R3 must
authenticate state bytes, complete tree, commit message, actor, timestamp,
parent and OID before producing a validated capability.

Initial admission accepts either expected absence or byte-exact same-operation
replay. Append requires authenticated equality of preceding operation,
authority, candidate commitment and complete path map. Changing only the
preceding candidate commitment fails. Partial/restart states resume only the
next missing bounded step. Unknown, damaged, human-edited or foreign state fails
closed.

## Normative R4 proposal representation

R4 owns candidate blob creation, complete base/proposal tree reads, proposal
overlay, Git tree ordering/serialization, commit construction, proposal-ref CAS,
and exact replay. Its proposal tree is the authenticated predecessor tree with
every and only M2-authorized changed paths replaced. It adds no ownership file,
coordination file or other repository path. Ownership lives in deterministic
commit metadata and immutable PR metadata.

Unchanged entries are identified by authenticated Git object OID/type/mode; an
arbitrary SHA-256 is forbidden for bytes that were not retrieved. Governed
source paths require retrieved bytes matching the appropriate M4 original or
authenticated preceding candidate hash. Regular modes `100644`/`100755` are
preserved; governed links/submodules or unsupported modes fail.

Git trees use raw UTF-8 name bytes and Git's comparator: bytewise `memcmp`, with
a directory compared as if its name were followed by `/`. UTF-16 string ordering
is forbidden. R4 fixtures include independent Git known answers for ASCII and
Unicode names (including U+10000 versus U+E000), nested directories, modes,
binary blobs, initial, append and merged-successor trees. Commits have exactly
one nonzero parent and deterministic message/actor/time frozen by R4 fixtures.

## Normative R5 PR representation and terminal capability

R5 owns PR creation, exhaustive discovery, immutable metadata, human-change
detection, stale-draft recovery and authenticated terminal capabilities. The
creation request binds repository, owned head ref/OID, configured base ref,
expected base OID, stable operation commitment, generation, marker commitment,
exact title/body bytes, `draft=true`, and `maintainer_can_modify=false`.

Raw identifiers are not interpolated. Immutable title/body markers use fixed
labels and lowercase commitment keys. Discovery readback must bind the observed
base OID and the complete immutable creation metadata, author/bot ownership and
creation operation. Human edits fail closed.

Base movement after the final read but during create may leave one exact
marker-owned draft whose observed base differs from expected. Only that mismatch
produces `stale-base-after-create`; it records no completed transition and blocks
append/update/adoption/automatic close/second create while active.

## R6 authenticated state matrix and step gates

R6 consumes validated R3/R4/R5 capabilities rather than raw public observation
records. It covers expected absence, exact replay, admitted claim, content/ref
partials, PR-create recovery, completed publication, stale draft and terminal
predecessor. It never asks Core to manufacture or authenticate these facts.

Every coordination/proposal create or advance uses one GraphQL `updateRefs`
request with one `RefUpdate`:

```text
beforeOid = exact predecessor, or forty zeroes for expected absence
afterOid  = exact nonzero successor
force     = false
```

REST ref mutation is not an admission primitive. An ambiguous response advances
only after exact direct readback proves the expected owned successor. The
sequence is:

```text
credential-free Core admission
  -> authenticated complete read
  -> R3 coordination CAS and readback
  -> before each later create, exact claim/predecessor/base reread
  -> at most one bounded R4/R5 mutation
  -> direct resource readback and durable R3 stage advance
  -> only then permit the next create
```

Visible drift means zero further mutation.

## Results and port

The closed result vocabulary is `local-invalid`, `replay-no-op`, `admitted`,
`recovered-content-partial`, `recovered-ref-partial`, `published`,
`awaiting-review`, `merged`, `closed-unmerged`, `stale-base-after-create`,
`stale`, `human-change`, `conflict`, `permission`, `rate-limit`, `cancelled`,
`timeout`, and `host-failure`.

`local-invalid` uses a closed field enum. Content/ref partials identify exact
expected/observed OIDs and operation; admitted identifies the claim; published
states identify generation/ref/PR; stale draft identifies exact PR, marker,
owned head, expected/observed base, generation and operation. Raw exceptions,
requests, responses, credentials and payloads are not representable.

The only port is:

```csharp
ValueTask<GitHubPublicationResult> PublishAsync(
    ValidatedGitHubPublicationAuthority authority,
    ValidatedGitHubChangedFilePayload payload,
    CancellationToken cancellationToken);
```

R2 owns credential/HTTP boundaries, R3 coordination, R4 proposal Git data, R5
PR behavior, and R6 orchestration. CLI composition, live proof, automatic
ready/merge/close/delete and release compatibility remain later work.
