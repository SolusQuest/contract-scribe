# GitHub Publication v1

> **Status:** Current pre-release Core/adapter contract, revised by M7-G4 for one immutable publication result set per fixed batch. Core owns source-free local admission; the adapter owns authenticated remote facts and mutations.

## Boundary and ownership

GitHub Publication v1 is the closed boundary between the M5-H1 CLI producer and
the GitHub adapter. Core owns:

1. credential-free, source-free caller authority and local validation;
2. a separate defensive-copy byte payload correlated to that authority;
3. domain-separated result-set, authority, operation and ref-identity commitments;
4. a closed structured result and one adapter port.

Campaign State v1 remains platform-neutral. Core does not expose authenticated
remote observations or a prepared remote operation. It does not serialize Git
blobs, trees or commits, overlay proposal trees, construct PR text, choose an
authenticated transition, or reconcile remote state. Those are the independent
R3-R6 authorities described below; adding them to Core would create a second
production implementation and is forbidden.

The [GitHub proposal CLI](../github-proposal-cli.md) validates a fresh accepted checkpoint and live M2 reconstruction against the retained accepted-candidate origin. It binds the original accepted operation identity to the exact fixed manifest and validated accepted subset. Fresh reconstruction still settles charges and displays the current checkpoint revision.

## Credential-free authority

`ValidatedGitHubPublicationAuthority` binds:

- repository owner/name, target ref and exact expected base commit;
- campaign lineage, snapshot, execution and work-plan commitments;
- original accepted checkpoint revision/digest, candidate, Patch Request, Patch Result and accepted-projection commitments;
- fixed batch identity, original creation quota, ordered selected target/work associations and the sorted accepted work-item subset;
- stable operation ID, generation, transition and optional structured terminal predecessor;
- every and only changed path, exact original/candidate full-file SHA-256 and cumulative M2 observations.

The result-set commitment includes the manifest, creation quota and accepted subset. Selected target keys and their work-item keys must each be unique, matching the fixed batch's singleton selected work items. Admission requires a nonempty unique accepted subset of the selected manifest; the changed documentation block count equals that subset's size. Caller collections are bounded and copied. A partial valid subset may publish once while the remaining selected members remain unresolved. Increasing an invocation quota never adds members or replaces the frozen subset.

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

## Fixed result set and payload

There are no campaign or PR business quotas for documentation blocks, distinct changed files or cumulative patch bytes. The cumulative byte value is an observation: the checked sum of candidate documentation bytes over all changed files. Necessary existing M2 block/file, artifact/parser and transport/payload bounds remain. Integer representation is validated before arithmetic; it is not an additional spending policy.

Authority paths are canonical repository-relative paths ordered ordinally.
Missing, extra, duplicate, ordinal-ignore-case-colliding, absolute, traversing,
or backslash paths fail locally. An ASCII-letter drive prefix such as `C:` or
`c:` is absolute and fails before any remote access. Hashes are exact lowercase
SHA-256. Each member of the changed-file set is a true change, so its original
and candidate hashes must be unequal. Candidate documentation byte and line observations are positive, matching the
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

The closed caller transitions are `initial`, `successor-after-merge` and `successor-after-closed-unmerged`. Initial publication has no terminal predecessor. A successor requires the exact structured predecessor: logical identity, PR number, generation, head OID and terminal disposition. The old append transition, predecessor map and separate closed-unmerged unlock are rejected without aliases.

These are caller claims. R6 freshly authenticates the exact predecessor, complete campaign PR collection, ownership, head, base and terminal disposition before admitting a successor. No active campaign PR may remain. The successor has a different generation and fresh snapshot/Audit. Closed-unmerged additionally requires current HEAD to differ from the retained original-operation base; returning HEAD to any retained closed original base pauses again. Configuration changes do not unlock it.

## Commitments and ref identities

Product commitments use SHA-256 with domain tags and big-endian length-framed strict UTF-8 fields. Every mutation-selecting fact changes the authority or operation commitment. The result-set domain is `contract-scribe/github-publication-result-set/v1`: batch identity, creation quota, ordered selected target/work associations and sorted accepted work keys. Authority frames batch identity and result-set commitment; operation frames authority, operation ID, generation and transition. Exact original retry is stable.

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
authority/batch/result-set/current-candidate commitments, generation/transition, exact coordination and
proposal predecessor/result OIDs, exact PR residual identity when present, and
the complete cumulative block count, cumulative candidate-byte count and
changed-path/candidate-hash map. The distinct changed-file count is exactly the
map length; it is not a separately caller-controlled field.

The coordination ref points to one Git commit whose root contains only `.contract-scribe` (mode `40000`). Its sorted file entries (mode `100644`) are `coordination-state-v1.json` and the present `checkpoint.json`, `plan.json`, `archive-predecessor.json`, and `publication-event.json` pointers. Every referenced blob is a real tree entry. The current coordination document has fixed-order keys `format`, `repositoryId`, `targetRef`, `targetCommitOid`, `campaignLineage`, `predecessorOid`, `checkpoint`, `plan`, `archive`, `publication`, `claim`; absent optional pointers are explicit null. Its format is `campaign-coordination-v1`, bounded to 32 KiB, canonical compact JSON without a final LF.

The production data path uses native Git for the existing canonical coordination commit format and ordinary single-ref checkpoint/publication updates. Each application attempt pushes a distinct successor with the full exact expected predecessor lease to one allowlisted coordination ref. Only an actual updated acknowledgement followed by independently fetched complete successor bytes and the original owner/lease/parent/history validation may advance the writer. An up-to-date result, stale rejection or unknown acknowledgement remains an observer and is never resent to recover authority. The independent observer object store cannot see locally prepared writer objects. Git content hashes establish actual bytes, not a GitHub principal, API Ref node identity, trusted time or a PR-send entitlement. Real platform identity/permission/Date facts remain at their required gates; the strict paired coordination/proposal mutation remains on its existing HTTP path.

Native acquisition does not download unrelated source history. The first source parent, and any required source commit metadata, use depth-one treeless fetches; an ignored or unsupported filter is a failure even if Git exits successfully. Newly acquired objects are restricted to the requested commit and Git's canonical empty tree. Coordination observations fetch one commit with its complete coordination tree and blobs; required historical coordination commits are acquired explicitly by exact OID, with no full-history fallback. All remote commands use the fixed origin and credential and are counted. Local object reads cannot initiate lazy network fetches. Raw commit parents, object hashes, canonical state and the original history/principal checks remain authoritative; a shallow traversal's missing parent is not absence evidence. Both independent stores retain the shared128MiB bound, cancellation and original deadlines.

Already-persisted API-signed commits and the HTTP operations that require that format retain their actual metadata/signature reader. The signed commit's actual Git OID is reconstructed from its observed raw payload and signature; its message binds repository/ref identities, exact canonical document and a single-use nonce. Expected principal composition is independent of inspected metadata. Null/wrong principals, invalid signatures or substituted bytes fail closed; raw Git never fabricates those fields. Canonical fixed-actor commits retain their existing exact preimage/tree/parent checks. Git actor time is not lease time. Initial absence uses exact native absent-ref creation; expired foreign takeover keeps its existing separate confirmation path. Every publication event retains its actual observed physical OID. Neither format grants a replayed writer or PR-create right, and publication events do not gain checkpoint-only polling exceptions.

A campaign observation may combine compatible repository/ref and full PR facts in one bounded GraphQL response. Its opening retains an actual REST repository permission vector; nullable GraphQL viewerPermission is never converted into those permission bits. Complete pagination and actual numeric/Node identities, lifecycle/body/marker/head/base/actor fields are mandatory. A separately dispatched closing query compares the complete PR collection and current repository/ref facts after content/ref verification. Sharing a response is not a transactional snapshot or independent readback. No lifecycle/absence result or authority is cached across gates.

The checkpoint pointer binds blob OID, canonical SHA-256 and Core revision. The plan pointer binds blob OID, SHA-256, execution commitment and batch identity. The claim binds a random owner, increasing epoch and server-time expiration. Claims are renewed under exact HEAD CAS; server Date samples and monotonic elapsed time gate ownership. Missing time, expiry, takeover or uncertain CAS stops execution. A separately armed monotonic expiry timer cancels execution at the confirmed claim's safety boundary even if renewal is waiting on the checkpoint gate or transport. Only an actual confirmed same-owner/epoch claim rearms it; cancellation is permanent. Publication-entry renewal has the same permanent-stop rule as background and active-publication renewal: an unconfirmed transaction or definitive current-authority loss freezes the original failure before cancelling the owner and cannot perform a later cleanup CAS. Ordinary caller cancellation before dispatch, or after a fully confirmed renewal, retains independently revalidated original-source release; it never enters a cancelled publication callback. Runtime provider and Host callbacks observe the actual execution token again after checkpoint admission and before debit/dispatch, and active read cancellation is linked directly to it. Mandatory completion retains its independent settlement token. Checkpoint authority cannot mutate a proposal ref or create a PR.

One conditional checkpoint-write transaction retains only its own authenticated immutable predecessor/object proof through exact CAS and actual successor readback. The successor commit/root/leaf/state, checkpoint blob, plan binding and archive proof must match; the target and final coordination ref are freshly checked and the owned claim remains live. Its typed write result carries that actual observation to Core for independent byte/hash/revision/artifact/archive validation, avoiding another complete remote history traversal at the caller. Later reads, renewal, recovery and publication start independent authority proofs. The bounded payload exceptions below permit authenticated immutable R3 and already published proposal-content object data across gates within one confirmed checkpoint-owner lifetime or one publication callback. They never cache current authority or replace actual successor readback; a confirmed same-owner publication entry may retain the positive raw-payload pool at the phase boundary while beginning a fresh callback authority proof. Campaign SemanticTool timing follows the authorization, query and observation-only closure envelope in [Campaign State](campaign-state-v1.md#budget-and-transition-semantics); sibling operations and Provider retain their existing deadlines, exposure and settlement rules.

Checkpoint persistence distinguishes admission from completion of admitted work. Initial/new-batch or changed-snapshot admission and each new physical provider-dispatch reservation require a fresh exhaustive campaign observation, exact target and coordination head, and absence of an active or same-base closed-unmerged proposal. Local host/tool progress, settlement, member acknowledgement and deterministic patch progress do not repeat this admission scan. Every save still verifies exact canonical predecessor, retained history, allowed transition, owner/epoch, projected live lease, exact bound plan, CAS and independent actual successor readback. Publication performs its own fresh gates. The retained publication event already authenticated by that predecessor proof may be reused within the same save transaction; an unchanged plan object authenticated at that predecessor is retained without another blob creation. Neither reuse supplies current mutable authority or a send entitlement.

Checkpoint Write/Renew confirmation has one bounded pending exception: with an independently confirmed live prior owner/epoch/lease, nonzero exact predecessor and strictly matched successful acknowledgement bound to the same repository/ref/before/after/operation, a valid exact-predecessor final GET permits at most two more direct observations after250/750ms. The three observations share one3s monotonic polling subwindow inside the original30s-or-shorter recovery, caller/semantic/Host and owner-lifetime limits. The independent old confirmed expiry stays armed; neither acknowledgement nor a requested future expiry rearms it. Only valid predecessor observations repeat. A failed logical read, foreign value, missing Date, authority loss or cancellation stops immediately. First exact expected exits polling irrevocably. First acquisition/zero predecessor, live foreign claims, ambiguous/lost acknowledgement, cleanup/release, publication-record operations, other R3 and paired R4 transitions retain their existing rules.

After delayed expected confirmation, retain the canonical successor proof and require physical fresh repository identity, pinned target and exact final coordination-ref reads, actual canonical owner/epoch, monotonically projected response Date and positive prior lease before rearming the confirmed claim. This fence has no retry on regression. Write still performs its original target/currentref/checkpoint readback before returning actual Written to unchanged Core validation; Renew itself grants no new Core Writer. The extra bound is two polling plus three barrier logical GETs, at most ten additional application sends, with all complete proof work inside the original scopes. Pending has no progress/debit/effect authority and is not persisted. Exhaustion freezes the actual predecessor comparison and permanently stops; other failures freeze their own first closed cause before explicit stop, never a mixture of observations. Historical failures remain failures. Finite sampling does not exclude unseen same-OID ABA or promise a platform visibility deadline.

Expired foreign-claim takeover has a separate unconfirmed pending path. Only Acquire with an authenticated nonzero predecessor whose foreign claim is already expired, one fixed new owner/next epoch/requested expiry, a strictly matched same-transaction successful CAS acknowledgement and a valid exact-predecessor first GET may enter it. This state grants no lease, timer arm, Core Writer, accepted progress, debit or model work. At most two additional single-attempt direct ref GETs after250/750ms share one3s monotonic polling window inside the original30s-or-shorter final-ref recovery, original caller/permanent stop and active parent deadlines. The original requested expiry must retain positive projected server-Date safety residual before and after waits/reads; neither the expired owner nor acknowledgement supplies authority. Failed logical reads are never retried, only valid exact predecessors repeat, and the first exact expected exits polling irrevocably. Authenticate the actual canonical successor and immediate parent, retained checkpoint/plan/archive/publication bindings, then physically reread repository identity, pinned target and the exact expected final coordination ref. The final ref fence is also single-attempt and cannot retry regression. Revalidate the actual new owner/epoch and original requested-expiry residual before the FIRST lease confirmation and independent timer arm. All complete authentication and fresh fences remain within the original recovery and cancellation parents; no later expiry or CAS is generated. Foreign/failed/missing-time/cancelled/expired/exhausted observations permanently stop with their actual first cause and no cleanup CAS or later resurrection. A later exact conditional checkpoint acceptance is still required for Core Writer; acquisition alone never grants it. Existing owned Write/Renew and publication payload reuse retain their separate scopes. This intentionally extends availability for future invocations without relabeling earlier failed observations or promising GitHub visibility latency. Persisted formats remain unreleased version1; no cross-revision migration or support promise is introduced.

R4 content preparation distinguishes immutable orphan-object uploads from making a proposal visible. Only the owned prepared-content operation, after its initial full gate and an actual missing candidate-commit observation, may upload its exact blobs, trees and commit without repeating R5 PR enumeration for each object. Every upload still requires a fresh repository/current owner/epoch/live lease observation, the unchanged proposal-ref predecessor, the original caller tokens and an unconsumed current-mutation guard; each object retains its exact OID/bytes and independent physical readback. An unconfirmed object stops subsequent mutations. Initial and final content verification retain their complete gates. Ref creation/paired CAS and PR creation retain full fresh target/repository/owner/coordination/predecessor/PR-absence checks and the original single-use winning-ACK send fence. Orphan objects supply neither published state nor a Writer/send entitlement; rejection at a visibility boundary may leave those objects unreferenced. This exception is private to that existing content stage, not a caller-selectable validation mode or cross-operation permission.

The same bounded positive raw-object pool also serves complete owned R4 proposal-content verification and R5's immutable object checks. R4 rehashes its exact deterministic expected commit, every traversed tree and each changed candidate blob before promoting the entire content proof; a failed or cancelled prefix promotes nothing. New mutations retain direct independent physical object readback, and the complete proof may consume that actual observed commit instead of fetching it again. R5's fresh proposal/target refs, PR lifecycle/ownership and current coordination Guard remain physical; only objects already admitted by a complete content proof may avoid another download. Cold single-object R5 reads do not populate the pool. Current Actor.user associations, permissions, owner/epoch/lease, absence, Writer and send rights are not cached. Checkpoint/current-head physical observation rules remain unchanged.

Publication state is carried by an immutable `publication-event-v1` envelope with `format`, `state`, `previous`. `previous` names the preceding event's blob and containing commit, skipping checkpoint-only commits. The containing document uses `anchor: "current"`; successors retain the now-known commit OID. No object hashes its own future commit identity. Readers authenticate actual parent/tree linkage and original generation/PR creation provenance. The 1024-event traversal bound applies to publication events, not checkpoint commits. Current-head acceptance also checks the immediate actual predecessor: claim-only commits retain checkpoint, plan, archive and publication pointers; publication events retain checkpoint facts; checkpoint changes preserve revision, request history, and each known/unknown charge component (including explicit zero). Within an unchanged execution plan they retain fixed membership, completed proposals and member receipts, closed outcomes, known completions, and the original candidate provenance; correlated patch rejection, retries, and newly accepted candidates keep their existing reducer rules. Reopening a closed outcome requires a genuinely retryable provider failure, its retained prior attempt identity, and a fresh correlated reservation with the proper anchor/member attempt counts. Changes require exact compaction proof for a new archive and correlated successor planning for a new plan. A dropped checkpoint or substituted historical event is invalid, never first-run absence. Checkpoint acquisition authenticates the retained publication-event graph before granting execution. Each publication gate rereads its current ref and current claim/state body, derives exact object OIDs and recomputes canonical authentication, immediate-parent retention, event edges, bounds and original provenance. The bounded payload exception may supply current/parent commit/tree and historical state/event data; it never carries parsed acceptance or a prior valid-edge result. A checkpoint-only document has no candidate or invented generation.

The embedded publication state has every key below in the exact order. Optional and stage-forbidden values are JSON null. Changed files use ordinal path order and exactly `path` then `candidateSha256`.

JSON numbers are base-ten integers without exponent, leading zero or fractional
form. Strings use the exact escaping produced by a non-indented
`Utf8JsonWriter` with `JavaScriptEncoder.Default`, validation enabled: JSON
syntax, control, HTML-sensitive and non-ASCII scalars are escaped by that
encoder. Quote, tab, BMP and surrogate-pair non-BMP cases are frozen as exact
base64 state bytes. Replacement characters are never admitted for invalid
UTF-16. The known-answer fixture, rather than a platform's future encoder
defaults, is normative for byte replay.

```text
version, campaignLineage, stage, repositoryId, targetRef, targetCommitOid,
snapshotCommitmentSha256,
authorityCommitmentSha256, batchIdentity, resultSetCommitmentSha256, operationId,
operationCommitmentSha256, currentCandidateCommitmentSha256,
generationId, transition,
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

ContractScribe campaign coordination v1
```

For the fixed coordination and proposal messages in this contract, response verification accepts the exact raw message or the observed REST representation that omits its single terminal LF. The independently prepared raw commit OID and all tree, parent, actor, date, state, and content checks must still match. This does not change the canonical raw preimage, request message, or known-answer OID, and does not allow general whitespace trimming, CRLF conversion, or other message changes. The response parser retains the observed text; this narrow equivalence is applied only by the owning commit verifier.

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
pull-request-create-started
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
authority. The remaining required/forbidden matrix is:

| stage | content | proposal ref/commit/tree | PR create operation/number | expected/observed base | ownership marker |
| --- | --- | --- | --- | --- | --- |
| `claimed` | forbidden | forbidden | forbidden | forbidden | forbidden |
| `content-created` | required | forbidden | forbidden | forbidden | forbidden |
| `proposal-ref-advanced` | required | required | forbidden | forbidden | forbidden |
| `pull-request-create-started` | required | required | creation commitment required, number forbidden | required and equal | required |
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

`tests/fixtures/github/publication-contract/coordination-representation-v1.json` freezes initial claim, exact replay, content/ref partials, durable PR send fence, pre-PR stale residuals, PR residual, completed publication and canonical escaping. The stage fixture freezes immediate PR-created, awaiting-review and terminal edges. State bytes, complete tree, commit, actor, timestamp, actual parent and Git OID authenticate before a capability escapes.

An acknowledged durable Claimed event freezes batch identity, accepted result-set commitment and original candidate identity before any publication success. Its containing document retains the original checkpoint and plan pointers. Readers reauthenticate those anchors and the complete immutable publication-event graph. Same-batch checkpoint updates may reconstruct only that frozen accepted projection; they cannot reserve provider work, expand membership, replace its candidate or erase uncertain intent. Successful authenticated PR recording seals the exact proposal tree/head and published subset.

Initial admission accepts expected absence or exact same-operation replay. Another operation cannot replace a sealed generation. A later generation requires freshly verified terminal/no-active facts and fresh Audit. Unknown, damaged, human-edited or foreign state yields no authority.

## Normative R4 proposal representation

R4 owns candidate blob creation, complete base/proposal tree reads, proposal
overlay, Git tree ordering/serialization, commit construction, proposal-ref CAS,
and exact replay. Its proposal tree is the authenticated predecessor tree with
every and only M2-authorized changed paths replaced. It adds no ownership file,
coordination file or other repository path. Ownership lives in deterministic
commit metadata and immutable PR metadata.

Unchanged entries are identified by authenticated Git object OID/type/mode; an
arbitrary SHA-256 is forbidden for bytes that were not retrieved. Governed
source paths require retrieved bytes matching the exact M4 original hash. Regular modes `100644`/`100755` are
preserved; governed links/submodules or unsupported modes fail.

Git trees use raw UTF-8 name bytes and Git's comparator: bytewise `memcmp`, with
a directory compared as if its name were followed by `/`. UTF-16 string ordering
is forbidden. R4 fixtures include independent Git known answers for ASCII and
Unicode names (including U+10000 versus U+E000), nested directories, modes,
binary blobs, initial and merged-successor trees. Commits have exactly
one nonzero parent and deterministic message/actor/time frozen by R4 fixtures.

`GitHubProposalStore` implements separate preparation, content verification and
proposal-ref publication steps. R6 owns the intervening R3 stage mutations.
`tests/fixtures/github/git-data/known-answers.json` freezes synthetic Git-generated
blob/tree/commit identities. Proposal commits use author and committer
`ContractScribe <contract-scribe@users.noreply.github.com>` at Unix second
`946684800`, offset `+0000`, and this exact LF-terminated message:

```text
ContractScribe proposal v1
operation=<operation-commitment-sha256>
generation=<generation-key-from-proposal-ref>
```

Every original/fresh-generation proposal overlays the exact authenticated target-base tree and has that immutable target commit as its sole parent. No previous proposal is a new candidate's base, and no cumulative append path exists. Each mutation still rereads the current claim, target and proposal facts.

Recorded-resource verification uses a separate read-only current-state check
that remains available after publication, terminal transitions, or target
movement. It authenticates the recorded content graph and returns the observed
target and proposal-ref OIDs for R6 classification, including drift. It returns
no preparation, content-write capability, guard, or ref entitlement, and does
not broaden the stages permitted to create resources. R4 also authenticates
its own client's numeric repository identity against the R3 state before Git
object access. Failures retain the closed R3 domain cause for R6 classification.
After dispatch, the complete immediate and final verification phases preserve
the mutation's delivery, context, and permissions. Independently verified
content may accompany a later gate failure; expiration of the independent
recovery budget is reported as timeout rather than caller cancellation. IO-performing
renewal, absence and mutation guards retain their typed first failure, including
transport/status, delivery and readback, rather than manufacturing a domain conflict.
A remote-failure result may retain one independently verified content or ref residual;
that observation does not turn the failure into a successful partial recovery or issue
an entitlement. A genuine earlier domain conflict remains authoritative after later
cancellation. Host/caller cancellation disposition is selected at its source and is
not recomputed from teardown signals during CLI presentation.

A fresh process may recover the next missing step of the exact frozen original operation. Fresh Core candidate/source proof, the live claim, exact deterministic Git objects and exhaustive PR absence issue a one-use proposal-ref right tied to the issuing store, operation, current content-stage head, content commit and absent original proposal ref. The paired CAS advances coordination and proposal ref atomically with both exact predecessors and `force=false`. A final absence/ownership/target check occurs before the mutation. A read-only resource proof alone grants no write.

PR creation uses a separate durable `pull-request-create-started` fence. It binds the exact original ref/head/tree, expected base, immutable creation commitment and marker, with no PR number. Only the unambiguously acknowledged winning fence CAS, followed by exact readback, issues a private one-use POST right. Replay, restart, lost acknowledgement or losing CAS never recreate it. The right is consumed before POST. A fresh process at the fence performs exhaustive exact-marker discovery only; an absent or ambiguous result retains intent and stops. A crash between fence and POST is safe but is not universally live.

For an atomic coordination-only fence, that right requires this call's matched positive acknowledgement and independently authenticated exact successor nonce. Later discovery of the nonce or matching canonical state after an unknown acknowledgement proves observation only. It cannot recreate the PR send right. The fresh target/owner/ref and exhaustive-absence fences remain mandatory. A fresh same-gate guard may be consumed once by the immediate absence check instead of rereading the identical opening proof; the closing physical recheck remains. The guard is bound to its issuing store/client, authority, ref identity, original cancellation token and live lease, and becomes unusable after any attempted semantic mutation, including unknown delivery.

Every acknowledged or uncertain mutation is read back under bounded recovery. Missing recorded objects, a foreign ref, active/changed PR, target drift or ambiguous effects stop without regenerating work, expanding scope or blindly resending. The first authenticated PR result may record base drift after the fence; later results retain that exact base observation. Checkpoint updates preserve publication anchors.

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

An otherwise exact marker-owned draft with a mismatched base OID during creation recovery is `stale-base-after-create` when no retained authenticated expected-base success exists. This is an observation-based residual and does not establish the hidden time of base movement. Retained expected-base success followed by a mismatch is later drift. Both record no completed transition and block append/update/adoption/automatic close/second create while active.

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
PR behavior, and R6 orchestration. CLI composition uses this port. Automatic ready/merge/close/delete is outside this contract.

### Bounded creation provenance

A bounded read-only GraphQL query may carry the live numeric repository identity, target and coordination ref identities, actual commit metadata and raw signature, two canonical tree levels, and the five fixed coordination file paths in one physical response. A historical read names one already-referenced commit OID and only its fixed coordination-state path; it does not select arbitrary paths or enumerate arbitrary contents. Both forms reject partial errors, missing required fields, truncated or binary blobs, incomplete parents and out-of-bound responses. Each document still passes the original Git hash, layout, pointer, actor and immediate-parent provenance checks. A combined response does not establish snapshot consistency: current-owner and Date checks and the independent final ref fence remain. Physical readbacks may use this bounded query rather than a separate GET for every object; a read-only GraphQL POST is a read, not a mutation.

Each generation retains its own original Claimed/fence/PR creation chain. Terminal successors retain prior generations and authenticate the original PR creation metadata. The complete read is bounded to 1,024 publication events and 90 seconds, with at most seven same-operation transitions. Checkpoint-only commits do not consume that event budget. Missing, substituted, cyclic, over-bound or incomplete provenance yields no partial authority. Every gate starts a fresh authority proof and rereads mutable facts. Within one confirmed checkpoint-owner lifetime, or separately within one `WithPublication` callback, a positive bounded pool may reuse intrinsically authenticated R3 commit/tree/state/event payloads and verified original-base/published-proposal commit/tree/blob payloads from an earlier complete successful proof at the exact Git kind/OID and client/store/origin/numeric-repository/read-authority scope. Repeated checkpoint and semantic-tool acceptances therefore do not refetch the complete retained publication history. Checkpoint-owner promotion follows the complete canonical content, pointer, owner and final-ref proof; newly created objects enter it only after exact physical readback and successful current-state confirmation. This deliberately accepts earlier verified data even when another object GET would now return 403, 404 or inconsistent data; immutable identity is not present accessibility. Current claim/state bodies, current checkpoint/plan/archive bodies, actual mutated-successor commit/root/leaf/state/event readbacks and final refs remain physical. Historical checkpoint blobs used only as retained immutable publication or immediate-parent evidence may share the same bounded positive payload pool after complete hash, canonical artifact, pointer/revision and successor-history validation. Their provenance and transition checks are repeated on reuse; a later use as the current checkpoint still fetches its bytes physically. Older ancestors inside a logical post-CAS proof may reuse payloads, with all semantic checks repeated. Checkpoint-owned observation also repeats the full proposal-content tree traversal, mode checks and blob hashes/candidate digests using positive payloads only after an earlier complete successful content proof. A content proof never caches a verification result, PR ownership/lifecycle, mutable ref, collection completeness or execution entitlement; every observer physically refreshes those facts and its final fences. Failed, incomplete or cancelled content verification discards its staged prefix. Read-only observers without a confirmed checkpoint owner remain cold. No headers, Date, status, ACK, intended bytes, negative result, capability or one-use CAS/POST entitlement is cached. Promotion follows whole-proof success, including exact successor comparison; failed, partial, cancelled or absent-publication proofs admit nothing. Each pool starts cold after initial claim confirmation, retains at most 1,024 promoted/staged entries and 64 MiB of encoded payload plus accounted metadata, and falls back to physical reads when full. Only one scope retains a pool at a time. Checkpoint-owner stop, expiry and disposal clear its pool before cleanup. After a complete same-owner publication-entry renewal and physical ref confirmation under the checkpoint gate, the existing positive raw-payload pool may pass into that callback for the exact same store, client, origin, credential lifetime and numeric repository. No parsed document acceptance, incomplete staged prefix, current-field observation, lifecycle/absence result or right passes with it. Cancellation or a mismatched owner/ref/repository rejects the handoff. Current fields and independent mutation readbacks remain physical. Publication execution cancellation, ownership/context loss and callback exit clear the callback pool. Callback exit clears the pool; later owner invocations, stores or credentials start cold. A later callback cannot inherit an earlier callback pool; only a newly complete checkpoint-phase positive proof may supply the handoff. This one-way checkpoint-to-publication handoff never extends scope across an invocation or identity boundary. No hit or renewal extends the original 300-second Host lifetime or other deadlines.

### Implemented R5 observations

The default host publisher is Actions using its built-in `GITHUB_TOKEN` (`github-actions[bot]`). R5 receives an explicit trusted expected principal independently of the inspected PR and compares its immutable IDs, kind and login. Neither a Bot type nor a matching suffix grants ownership. R5 does not acquire credentials or require `/user`; authenticated principal composition and live credential/permission/trigger proof remain R6/H2/H3 responsibilities.

The immutable title is `ContractScribe proposal <proposal-generation-key>`. The exact LF body consists of the existing ownership marker followed, in order, by `campaign`, `generation`, `snapshot`, `batch`, `headRef`, `baseRef`, and `creationOperationId` lines, each using `=sha256:<lowercase-key>`. Campaign uses the R1 coordination-ref identity key, generation uses the proposal-generation key, snapshot uses its commitment and batch uses its exact identity, ref keys hash the exact UTF-8 full refs, and creationOperationId uses the original operation commitment. No current-head/cumulative prose or optional summary is emitted. The literal fixture is `tests/fixtures/github/pull-requests/creation-metadata.json`.

Fresh creation is explicit and performs at most one draft POST. Recovery is a separate read-only operation; uncertain absence never grants a blind resend. Every authenticated result requires exhaustive campaign discovery, exact owned metadata and generic proposal observations, direct PR readback, and unchanged coordination/proposal authority. Exact duplicate list observations deduplicate; contradictory duplicates, competing active generations and removed ownership on known refs fail closed. Terminal PR recognition does not by itself authorize a successor.

During unresolved creation recovery, an otherwise exact owned draft with a mismatched base OID and no retained expected-base success is `stale-base-after-create`. This describes the observed residual, not the unobserved time of base movement. Retained authenticated expected-base success distinguishes later drift. Both prohibit append, completion and replacement. Base-ref edits or additional ownership mismatches are not this residual.

Retained base evidence includes the authenticated original creation source during incomplete publication, the current PR-bearing R3 state, and a supplied store-owned prior R5 observation. A recorded stale generation does not regain active eligibility when base equality returns. Terminal recognition validates the exact retained observed base, so human closure or merge of that same stale PR can be observed without allowing an unexplained base substitution or authorizing a successor. An R5-owned recovery deadline expires as `timeout`, preserving possible POST delivery and no success capability; it is not caller cancellation. Cancellation after possible dispatch still permits the existing bounded independent readback.

Lifecycle guarantees use recorded authenticated observations only. A retained actual `Draft=false` observation can prove a later ready-to-draft contradiction; R3 `awaiting-review` is only a publication hold and does not prove ready promotion. An unchanged draft with a budget hold remains a held draft. Authenticated terminal-to-open contradictions fail closed; wholly unobserved lifecycle cycles are not detected. Ready/terminal state encountered during recovery is returned as observed without mutating human review state or inventing a draft success.

## Independent host finalization

The CLI starts one finite five-minute host finalization window before accepted-only M2 reconstruction or candidate-free publication recovery. It is separate from the shared model allowance, never resets between publication steps, and stays alive through checkpoint cleanup. Execution links actual caller cancellation and owner loss. Independent readback may continue after caller cancellation, but every request, uncertain-effect readback and cleanup remains bounded by the same host deadline. Caller cancellation and host deadline retain distinct outcomes.
