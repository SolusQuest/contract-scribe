# Consumer configuration

## M7 planning and current configuration

[M7 plan](../90_roadmap/m7-plan.md) owns the adopted resource and identity semantics. C1 does not change `config/defaults.json`, configuration/schema grammar or runtime readers. The inventory and defaults below describe the current implementation until their owning code/schema issues update them.

M7 selects a fixed semantic batch under a configurable initial 100-target invocation ceiling and shares Action resources across all targets/retries. Campaign aggregate caps become optional/default unlimited while retaining complete accounting history; zero is not an unlimited sentinel. The complete initial numeric values live in [M7 resource defaults](../90_roadmap/m7-plan.md#shared-resources-and-initial-defaults), not this current-defaults inventory. Remove PR block/file/patch-size business quotas while preserving necessary parser/transport/context/patch safety bounds and aligning hidden legacy execution caps.

Separate correctness-bearing inputs from execution budgets. With no active-PR or closed-same-base gate, changed base/policy/profile/style or other correctness inputs require a fresh snapshot/Audit without proposal migration. Execution-budget changes alone preserve compatible original membership: raising quota cannot add targets and lowering quota limits newly started distinct targets. Configuration or quota changes never unlock a same-base closed-unmerged PR or bypass an open-PR pause/wait. Exact schema layouts and the explicit unlimited representation belong to implementation; no compatibility framework is added.

## Decision

`campaign` invocations resolve their effective configuration from three layers in the production C# CLI:

```text
invocation override (--configuration-override) > downstream file (--configuration) > payload defaults
```

The payload defaults ship inside the selected payload at `config/defaults.json` relative to the CLI payload root. They are read from that payload, never from `main`, the consumer repository, or the caller's current working directory. Both downstream options take a file path, not inline JSON; each is optional, but an explicitly supplied path that is missing, unreadable, or not a regular file fails closed. There is no environment-variable precedence, no remote include, no hot reload, and no second consumer contract version in this draft.

Configuration field names are a configuration contract. A future GitHub Action wrapper is expected to expose a small number of invocation inputs (for example a path to a downstream configuration file), not one Action input per field.

The resolved output is the complete `campaign-configuration-v1` runtime-authority document. The existing strict parser remains the sole semantic validator; layering adds no second validator and no second resolved shape.

## Invocation and product authority

Some fields of the resolved document are never read from a consumer layer:

- `planning.campaignLineage` is caller-attested invocation authority: the `campaign` command supplies it through the required `--campaign-lineage <id>` option and `github-proposal` through the request document's `campaignLineage` field. It is a durable identity consumed by campaign state and GitHub publication coordination, and uses the existing M4 opaque-identifier grammar `[A-Za-z0-9][A-Za-z0-9._:-]*` at 1–512 UTF-16 code units. A layer that carries it is rejected as an unknown field.
- `planning.productContractRevisionSha256` is derived from the running payload build identity (`CliBuildIdentity`) and injected by the resolver on both production paths; it is never caller-supplied and consumers cannot express it.
- `planning.productContractRevisionId` and every protocol, contract, version, policy, registry, and source-marker identifier are product-owned authority values carried only by the payload defaults.

## Field disposition

| Resolved field | Disposition |
|---|---|
| `campaignConfigurationVersion` | product-owned (`1`) |
| `planning.campaignLineage` | invocation authority (`--campaign-lineage` for `campaign`, request `campaignLineage` for `github-proposal`) |
| `planning.targetProfile` | public |
| `planning.proposalContractId` | product-owned |
| `planning.contextSelectionPolicyId` | product-owned |
| `planning.m2ProjectionPolicyId` | product-owned |
| `planning.m2ProjectionVersion` | product-owned (`1`) |
| `planning.maximumPatchElapsedMilliseconds` | public |
| `planning.productContractRevisionId` | product-owned |
| `planning.productContractRevisionSha256` | derived from the running payload |
| `budgets.campaign.*` (11 limits) | public |
| `budgets.scribe.*` (13 limits) | public |
| `retry.*` | product-owned |
| `scribeRequest.scribeRequestVersion` | product-owned (`1`) |
| `scribeRequest.agentProtocolId` | product-owned |
| `scribeRequest.toolPolicyAndRegistryId` | product-owned |
| `scribeRequest.toolPolicyId` | product-owned |
| `scribeRequest.styleProfileTemplate.*` (style id, output language, text/component policies, literal and claim policies, unit/reference ceilings) | public — the value is the style choice; the content authority commits identifier and projection together |
| `provider.providerConfigurationId` | product-owned source marker (`provider.configured.v1`) |
| `provider.modelConfigurationId` | product-owned source marker (`model.configured.v1`) |
| `provider.scribeProtocolId` | product-owned (`scribe-protocol.v1`) |
| `provider.endpoint` | public |
| `provider.model` | public |
| `provider.requestProfile.*` | public (`reasoningEffort` is the only nullable scalar) |
| `costPolicy` and its `currencyId`, `ratePolicyId`, and four `*MicrounitsPerMillion` rates | public nullable object |

Anything else in a consumer layer — GitHub inputs, credentials, snapshot or state bindings, or any name outside this inventory — is an unknown field and is rejected. No public field carries a repository-relative path today, so no declared path confinement applies to layer values; if a path field is ever added it resolves against the single explicit repository root under the existing confinement rules.

## Layer grammar and merge

Each consumer layer is a credential-free UTF-8 JSON object no larger than 262,144 bytes and no deeper than 64 JSON levels, with no BOM, comments, trailing commas, duplicate properties, unknown properties, or undeclared `null`. `consumerConfigurationVersion` is required and is `1`; it is layer metadata, not merged into the resolved document.

Merge walks the declared field tree:

- Objects merge recursively by declared field; absent fields inherit from lower layers.
- Scalars and arrays are terminal and replace wholesale; arrays never concatenate or deep-merge.
- Explicit `0`, `false`, and `[]` are real overrides, not absence.
- `null` is legal only for declared nullable fields: `costPolicy` (object ↔ `null`), `provider.requestProfile.reasoningEffort` (string ↔ `null`), and the six campaign lifetime caps for provider requests, input/uncached/output tokens, cost and elapsed time (integer ↔ `null`). A missing lifetime field inherits, an explicit upper-layer `null` clears a finite lower-layer cap, and zero remains a finite cap. `null` → object replaces wholesale with no inherited children; object → `null` clears explicitly; a non-null type or kind change is rejected everywhere else.
- Layer property order is free; the resolved document is emitted in the canonical field order before strict validation.

The consumer layer schema is published at `schemas/consumer-configuration/v1.schema.json` and is driven by the same declared-field tree the resolver uses.

## Current defaults

`config/defaults.json` carries the complete resolved shape minus the two injected fields. Values:

- Provider: the repository's frozen M3 `deepseek-primary` validation profile (`m3-provider-evaluation-protocol.md`): `https://api.deepseek.com/chat/completions`, `deepseek-v4-flash`, thinking enabled, `reasoningEffort: high`, `toolChoice: omitted`, `continuationPolicy: required-for-tool-calls`, `outputTokenField: max_tokens`. This selects the provider profile the repository's own validation exercised; it is not a provider-support claim and performs no network or credential access by itself.
- Campaign lifetime provider-request, input/uncached/output-token, cost and elapsed caps default to `null` (unlimited). The five structural/attempt/candidate limits retain their finite defaults, as do all Scribe-run transport, parser, request, token and timeout limits (8 provider requests per run and attempt ceilings of 2) and the Patch deadline.
- `costPolicy`: `null` — no invented pricing; a finite lifetime cost cap requires configured rate/currency authority; unlimited runs without it retain unpriced history.
- Authority identifiers: the frozen product-owned `*.v1` vocabulary (`proposal.documentation.v1`, `context.m1.v1`, `m2.projection.v1`, `retry.policy.v1`, `documentation-scribe.agent.v1`, `tools.registry.read-only.v1`, `tool-policy.read-only.v1`) plus the M3-accepted scribe protocol identity `scribe-protocol.v1`; `provider.configured.v1` / `model.configured.v1` source markers remain truthful under any effective endpoint/model because the complete provider object is committed as provider content authority.

## Errors and admission

Every source is admitted through the same bounded regular-file, no-follow, size, UTF-8, and content checks. Resolution failures surface as `campaign.invalid-configuration` before any repository audit, checkpoint read, credential read, or provider dispatch. Each admitted source is recorded with its path, length, timestamp, and SHA-256; the existing admission, in-session, dispatch-guard, and reconciler revalidation predicate now covers every source and preserves its existing stage-specific outcomes (`campaign.invalid-configuration`, reservation conflicts, `campaign.patch.configuration-changed`). A changed effective configuration cannot silently resume or reset consumptive totals.

## github-proposal and validation disposition

`github-proposal` consumes the same layered resolution: its optional `--configuration`/`--configuration-override` options carry the consumer layers while campaign lineage, snapshot binding, state location, and GitHub publication claims move into the separately validated `--request` invocation document (see [github-proposal-cli.md](github-proposal-cli.md)). The direct resolved-document input and its low-level runtime-authority reader are retired; `campaign-configuration-v1` keeps exactly one active draft role — the resolved authority representation produced by this resolver — and does not become a competing public consumer contract.

## Non-goals

No environment-variable configuration precedence, hot reload, remote includes, a YAML merge engine, compatibility readers, aliases, migrations, deprecation periods, or a second public consumer shape. No new model/provider capability, M4 state-semantics change, or GitHub-publication behavior is introduced here.
