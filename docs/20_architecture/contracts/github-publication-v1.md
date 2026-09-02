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
  distinct preceding candidate commitment, preceding authority commitment,
  preceding operation ID, generation, snapshot commitment and policy commitment;
- for a closed-unmerged successor only, the exact explicit authorization.

Campaign lineage retains the M4 grammar
`[A-Za-z0-9][A-Za-z0-9._:-]*` and its existing scalar bound. New M5 identifiers
use the marker-safe grammar `[A-Za-z0-9][A-Za-z0-9._-]*` and the same bound.
Whitespace, leading punctuation, Unicode formatting characters, `=`, HTML
comment delimiters, controls, and all other characters fail before a commitment,
credential, filesystem access, or request. Raw lineage and M5 identifiers are
never interpolated into PR or ownership-marker metadata; those surfaces contain
only lowercase commitment keys. Repositories and refs retain their own stricter
grammars.

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
or backslash paths fail locally. An ASCII-letter drive prefix such as `C:` or
`c:` is absolute and fails before any remote access. Hashes are exact lowercase
SHA-256. Each member of the changed-file set is a true change, so its original
and candidate hashes must be unequal. This inequality does not apply to the
preceding path map, which carries only the previous candidate identity.
Candidate documentation byte and line observations are positive, matching the
accepted M4 candidate domain; original observations remain nonnegative so a new
nonempty documentation file may have an empty original.

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
- `same-snapshot-append`: exact preceding operation, authority, candidate,
  generation, snapshot, policy and nonempty complete preceding path map; the
  preceding generation/snapshot/policy must equal the current authority and no
  terminal predecessor/authorization is present;
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
commitment is independent of the path map and is committed separately. The
preceding generation/snapshot/policy claims are validated aliases of the equal
current generation/snapshot/policy fields already framed by the authority; they
are not framed a second time. Exact retry is stable.

Ref identities contain only lowercase commitment keys:

```text
refs/heads/contract-scribe/coordination/<campaign-sha256>
refs/heads/contract-scribe/proposals/<campaign-sha256>/<generation-sha256>
```

GitHub repository owner and name spelling is preserved in caller authority and
must later be authenticated against GitHub's canonical repository identity. For
ref derivation only, both ASCII repository parts are lowercased before hashing,
so GitHub case aliases cannot create parallel coordination or proposal
namespaces. Target ref and campaign/generation facts remain ordinal.

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
the complete cumulative block count, cumulative candidate-byte count and
changed-path/candidate-hash map. The distinct changed-file count is exactly the
map length; it is not a separately caller-controlled field.

The coordination ref points to a commit whose tree contains exactly one entry:
the tree `.contract-scribe` with mode `40000`. That tree contains exactly
`coordination-state-v1.json` with mode `100644`; no proposal or ownership file is
present. The state object has every key below in this exact order. Optional and
stage-forbidden values are encoded as JSON `null`, never omitted. Changed files
are ordered by ordinal path and each object has exactly `path` then
`candidateSha256`.

JSON numbers are base-ten integers without exponent, leading zero or fractional
form. Strings use the exact escaping produced by a non-indented
`Utf8JsonWriter` with `JavaScriptEncoder.Default`, validation enabled: JSON
syntax, control, HTML-sensitive and non-ASCII scalars are escaped by that
encoder. Quote, tab, BMP and surrogate-pair non-BMP cases are frozen as exact
base64 state bytes. Replacement characters are never admitted for invalid
UTF-16. The known-answer fixture, rather than a platform's future encoder
defaults, is normative for byte replay.

```text
version, stage, repositoryId, targetRef, targetCommitOid,
snapshotCommitmentSha256,
authorityCommitmentSha256, policyCommitmentSha256, operationId,
operationCommitmentSha256, currentCandidateCommitmentSha256,
precedingOperationId, precedingAuthorityCommitmentSha256,
precedingCandidateCommitmentSha256, generationId, transition,
coordinationPredecessorOid, contentCommitOid, proposalRefOid,
proposalCommitOid, proposalTreeOid,
pullRequestCreationOperationCommitmentSha256, pullRequestNumber,
expectedBaseOid, observedBaseOid, ownershipMarkerSha256,
cumulativeDocumentationBlocks, cumulativePatchBytes, cumulativeChangedFiles
```

The Git commit has exactly one nonzero parent and these exact, LF-only preimage
lines (the final message line is LF terminated):

```text
tree <root-tree-oid>
parent <coordination-parent-oid>
author ContractScribe <contract-scribe@users.noreply.github.com> 946684800 +0000
committer ContractScribe <contract-scribe@users.noreply.github.com> 946684800 +0000

ContractScribe coordination v1
operation=<operation-commitment-sha256>
stage=<lexical-stage-id>
```

For expected-absence initial creation, `coordinationPredecessorOid` and the
coordination-ref `beforeOid` are forty zeroes, while the commit parent is the
exact authenticated `targetCommitOid`. The zero value is ref-absence evidence,
not a Git object identity. For every later advance, one identity is authoritative
across state, object and mutation: the commit parent and
`coordinationPredecessorOid` both equal the exact coordination-ref head read for
`beforeOid`; `afterOid` equals the newly computed nonzero commit OID. Exact replay
reproduces the initial state and commit bytes/OID and performs no ref mutation.

Git blob, tree and commit identities use the ordinary SHA-1 Git object framing
`<type> <decimal-byte-count>\0<preimage>`. The pull-request creation operation
commitment is SHA-256 over the same four-byte big-endian length-framed field
encoding as Core commitments, with domain
`contract-scribe/github-pull-request-creation/v1` and these labels in exact
order: `version`, `repository-id`, `target-ref`, `target-commit-oid`,
`authority-commitment`, `operation-commitment`, `generation-id`, `proposal-ref`,
`proposal-commit-oid`, `proposal-tree-oid`. It binds only authenticated immutable
generation-creation facts. The lowercase marker ownership key is exactly this
`pullRequestCreationOperationCommitmentSha256`, not an independent caller value.
The ownership marker is strict UTF-8, LF terminated, and exactly
`<!-- contract-scribe-publication-v1 ownership=sha256:<marker-key> -->`.
`ownershipMarkerSha256` is SHA-256 over those complete marker bytes. Raw lineage,
operation and generation IDs never appear in the marker. The fixture freezes the
creation commitment, marker key, marker bytes, marker hash, exact replay and
one-field mutation behavior.

Its closed stages are:

```text
claimed
content-created
proposal-ref-advanced
pr-created
published
awaiting-review
stale-draft
stale
merged
closed-unmerged
```

Each stage has a closed required/forbidden field matrix. Later stages retain all
earlier exact identities; no caller-controlled diagnostic is correctness-bearing.
The common fields from `version` through `coordinationPredecessorOid`, plus all
three cumulative observations, are always required and must equal the accepted
authority. In a `claimed` initial/successor record the
three `preceding*` fields are forbidden; in a `claimed` append they are required.
The remaining required/forbidden matrix is:

| stage | content | proposal ref/commit/tree | PR create operation/number | expected/observed base | ownership marker |
| --- | --- | --- | --- | --- | --- |
| `claimed` | forbidden | forbidden | forbidden | forbidden | forbidden |
| `content-created` | required | forbidden | forbidden | forbidden | forbidden |
| `proposal-ref-advanced` | required | required | forbidden | forbidden | forbidden |
| `pr-created` | required | required | required | required and equal | required |
| `published` | required | required | required | required and equal | required |
| `awaiting-review` | required | required | required | required and equal | required |
| `stale-draft` | required | required | required | required and unequal | required |
| `stale` | optional closed residual | optional with content and equal when present | forbidden | required and unequal | forbidden |
| `merged` | required | required | required | required | required |
| `closed-unmerged` | required | required | required | required | required |

Whenever proposal fields are required, `contentCommitOid`, `proposalCommitOid`
and the authenticated `proposalRefOid` are one identity: the exact prepared
proposal commit. The proposal tree OID is distinct. A state that records
different values for any of the three commit/ref identities fails closed.

`stale` is the pre-PR target-drift terminal. Its residual has exactly one of
three closed shapes: claim-only has no content/proposal identity; content has
only `contentCommitOid`; proposal has content plus all proposal fields with the
three commit/ref identities equal. It always records the unequal expected and
observed target OIDs and has no PR creation operation, PR number or marker. After
exact residual discovery, the adapter may CAS only this terminal state from the
current coordination predecessor. No later content/ref/PR mutation is permitted.

A fresh `initial` authority for the currently authenticated target may replace
an exact `stale` state by coordination-ref CAS only after proving the complete
stale bytes/OID, the residual content/ref identities, and absence of any owned
PR. It uses a new generation and operation and retains no preceding published
M4 authority. The old immutable content object and old-generation proposal ref
are never adopted, advanced or used as the new proposal predecessor. This is the
only continuation from pre-PR target drift; `stale-draft` remains blocking and
cannot use it.

`tests/fixtures/github/publication-contract/coordination-representation-v1.json`
freezes canonical state and commit preimages plus blob, nested-tree, root-tree and
commit OIDs for initial claim, byte-exact replay, append claim, content partial,
ref partial, all three pre-PR stale residual shapes, PR-create residual,
completed publication and quote/BMP/non-BMP/control escaping. R3 must authenticate
those state bytes, the complete tree, commit message, actor, timestamp, parent
and OID before producing a validated capability. The known answers form a
predecessor-bound chain from the authenticated target commit through initial,
content and proposal-ref states. Stale and completed publication are alternative
successors of that proposal-ref state; append is a new operation whose parent and
predecessor are the completed-publication coordination commit.

Initial admission accepts either expected absence or byte-exact same-operation
replay. Append requires authenticated equality of preceding operation,
authority, candidate commitment, generation, snapshot commitment, policy
commitment and complete path map. R6 compares those caller predecessor claims
to the validated previous R3 state before admitting the Core equality checks;
neither an opaque preceding authority hash nor a caller claim alone proves
same-snapshot lineage. Changing any one predecessor fact fails. Partial/restart
states resume only the next missing bounded step. Unknown, damaged, human-edited
or foreign state fails closed.

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
