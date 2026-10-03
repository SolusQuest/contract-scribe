# M6 closeout: Action engineering and internal qualification

## Accepted closeout scope

On 2026-10-03, the maintainer approved closing M6 on its completed engineering delivery and exact unpublished internal-candidate qualification. The original M6 plan included first public publication; that outcome is explicitly deferred until after M7 rather than counted as completed. License/contribution disposition remains owned by #29 under Release Gate — Governance. First public publication remains owned by open #189 in a separate deferred publication track.

M7's accepted direction is caller-owned scheduling and automatic bounded continuation. Its detailed scope, product decisions, execution graph, and acceptance criteria will be refined in a separate session. This closeout records the handoff only; it does not create M7 executable issues, implement continuation, or select a public alpha channel.

## Completed delivery

| Track | Completed outcomes |
| --- | --- |
| Consumer configuration | #183 resolves payload defaults, an explicit downstream file, and invocation overrides in C#; #184 supplies GitHub proposals with resolved settings and separate runtime authority. |
| Payload and Action | Adopted #18 delivers the verified D2 DLL archive; #185 delivers the thin composite Action; #186 delivers manual/scheduled caller examples with authenticated state handoff. #196 corrects the caller product-token boundary. |
| Release controls and internal qualification | #187 implements manual preparation and guarded promotion; #198 attests payload provenance; #201 adds draft-only internal versions; #204 fixes the observed staging/publication/handoff failures; #188 qualifies the exact merged-main internal candidate. |

The C# implementation owns configuration, provider execution, campaign state, deterministic patching, and GitHub proposal mutations. The Action acquires/verifies the payload and invokes the CLI. M6 adds no new provider, platform, automatic merge, public release, or released compatibility promise.

## Exact internal evidence

The accepted `v0.1.0-internal.6` candidate used source/wrapper [`ecaf2841fa5346cf2e3f979c505ac3804eab6e53`](https://github.com/SolusQuest/contract-scribe/commit/ecaf2841fa5346cf2e3f979c505ac3804eab6e53), payload source [`607643f10d9422289e378b243ce39660d2d9a881`](https://github.com/SolusQuest/contract-scribe/commit/607643f10d9422289e378b243ce39660d2d9a881), and candidate digest `7501e3f852f3eacf751661f57be5b3feabc676ee13d093d40f86544d2ed3a680`. The candidate and its historical evidence retain these identities after documentation or M7 changes.

- [Main CI 36973968901](https://github.com/SolusQuest/contract-scribe/actions/runs/36973968901) and [prepare/attest 36974043708](https://github.com/SolusQuest/contract-scribe/actions/runs/36974043708) passed.
- [Protected draft creation 36979719887](https://github.com/SolusQuest/contract-scribe/actions/runs/36979719887) and [independent recovery 36979879899](https://github.com/SolusQuest/contract-scribe/actions/runs/36979879899) converged on the same unpublished Release `401621181` and asset `605086680`.
- [Manual Action run 36980058284](https://github.com/SolusQuest/contract-scribe-sandbox/actions/runs/36980058284) created one bot-owned draft PR.
- [Native scheduled run 37018034591](https://github.com/SolusQuest/contract-scribe-sandbox/actions/runs/37018034591) restored the authenticated checkpoint on a distinct runner, advanced checkpoint revision 4 to 6, and replayed the same publication without additional provider/token/cost charges or a duplicate PR.
- [Native inactive run 37055713165](https://github.com/SolusQuest/contract-scribe-sandbox/actions/runs/37055713165) skipped both jobs with zero steps/artifacts after activation and lineage removal.

The [#188 completion record](https://github.com/SolusQuest/contract-scribe/issues/188#issuecomment-5960313486) and [#204 completion record](https://github.com/SolusQuest/contract-scribe/issues/204#issuecomment-5960311243) retain the full bounded acceptance and final independent review. No public product release/tag was published, no sandbox proposal was merged, and historical drafts/refs/PRs and the user-enabled diagnostic were retained. Administrative M6 closeout does not mutate these resources or repeat the live qualification.

## Evidence limitations and M7 handoff

M6 proves payload acquisition, actual Action execution, authenticated cross-runner checkpoint recovery, idempotent PR replay, and inactive entry behavior for the exact recorded candidate. It does not prove punctual or reliable delivery of GitHub native cron, automatic consumption of all remaining work, downstream proposal-PR CI coverage, or a public supported distribution.

The current caller example seals an explicitly activated next hop to a native `schedule` run. Its request helper defaults to `transition: initial`, so resume reconstructs/replays an existing accepted publication. The production CLI supports explicit `same-snapshot-append`, but the example does not automatically select that transition. Snapshot and state selection are caller-supplied rather than an automatic commit-indexed lookup. Provider/token/cost charges persist across invocations; there is no independently configurable per-Action work quota that automatically renews on the next trigger. Completing these user-facing continuation decisions belongs to M7 refinement, not a reinterpretation or reopening of the completed M6 acceptance.

The maintainer's M7 direction is to make trigger sources replaceable, restore compatible progress, process bounded work per invocation, continue remaining work, and stop model work when resolved. Native schedule remains an optional caller integration with no timing promise. Exact snapshot/configuration correlation, budget authority, ownership, safe source changes, and publication uniqueness remain constraints for the future design. State storage, request construction, quota semantics, PR lifecycle, and validation details are intentionally undecided here.

## Deferred publication and governance

#189 remains open and is moved out of M6 and the M6-R parent into a separate first-publication track. #29 remains open in Release Gate — Governance, with work planned after M7. Neither issue is cancelled, waived, or marked completed. Once M7 is refined, the publication owner must gain the actual M7 completion dependency; until then, the documented after-M7 prerequisite remains explicit rather than represented by a fabricated completed issue.

After M7, the release owner prepares the then-current candidate, runs affected qualification, satisfies exact-candidate license/notice/inventory obligations, and obtains fresh maintainer publication approval. The retained internal candidate cannot be renamed or promoted into a public version. A public preview/alpha channel remains a separate policy/implementation decision; full public-release scope and version selection are not decided by this closeout. See [Release policy](../10_workflow/release-policy.md).

## Tracker synchronization

Merge this governing scope amendment before applying its structural tracker migration. Then synchronize the M6 milestone title/description, remove #189 from M6-R and move it to the deferred first-publication milestone, update governance timing without changing its gate, and close #180/#181/#182 followed by #179 and M6 under the revised scope. Verify current completed leaves, the changed fields/relationships, retained #189 blockers (#188 and #29), and the complete changed graph. Preserve native issue types and completed leaf histories. The separate Payload Distribution and Process Topology tracks are not moved or closed by this task.

Pre-release process exception: the post-merge tracker migration is necessary because the current M6 graph still requires first publication and would otherwise falsely imply publication completed. One documentation PR, one human merge, one coherent review, and one bounded structural synchronization are sufficient; no new implementation issue, certification bundle, live run, or second PR is required. The synchronization manifest is an execution aid for this single migration rather than a new release-authorization protocol.
