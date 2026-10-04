# Campaign configuration v1

## Role

This document defines the resolved campaign execution configuration: the complete, closed runtime-authority document that the campaign runner and checkpoint commitments consume. It is produced only by the layered consumer-configuration resolver (see `docs/20_architecture/consumer-configuration.md`), which injects invocation authority (`campaignLineage`) and product identity (`productContractRevisionSha256`) before this shape is validated; both `campaign` and `github-proposal` invocations reach the runner through that single producer path. It is not the public consumer configuration shape; consumer layers are a distinct draft contract and cannot express the injected authority fields.

## Format

The campaign configuration is a credential-free UTF-8 JSON document no larger than 262,144 bytes. UTF-8 BOM, comments, trailing commas, duplicate properties, unknown properties, reordered properties, non-integer numbers, and unpaired surrogates are invalid. Every object has a closed property order and no defaults or aliases.

The top-level order is:

```text
campaignConfigurationVersion, planning, budgets, retry, scribeRequest, provider, costPolicy
```

`campaignConfigurationVersion` is `1`. A canonical executable example is checked in at `tests/fixtures/campaign/cli/configuration-valid.json` (its product-revision SHA is a fixture placeholder and a real invocation must pin the current CLI-derived revision).

## Closed object shapes

`planning` is ordered as `campaignLineage`, `targetProfile`, `proposalContractId`, `contextSelectionPolicyId`, `m2ProjectionPolicyId`, `m2ProjectionVersion`, `maximumPatchElapsedMilliseconds`, `productContractRevisionId`, `productContractRevisionSha256`. The profile is `profile.external-api` or `profile.assembly-visible`; M2 projection version is `1`; patch elapsed is a finite positive per-invocation deadline independent of the optional campaign lifetime elapsed cap; the product SHA is exactly 64 lowercase hexadecimal characters.

`budgets` contains `campaign` then `scribe`. Campaign order is `maximumBlocks`, `maximumChangedFiles`, `maximumPatchBytes`, `maximumProviderRequests`, `maximumAttemptsPerTarget`, `maximumInputTokens`, `maximumUncachedInputTokens`, `maximumOutputTokens`, `maximumCostMicrounits`, `maximumElapsedMilliseconds`, `maximumCandidatesPerBlock`. The six lifetime fields `maximumProviderRequests`, `maximumInputTokens`, `maximumUncachedInputTokens`, `maximumOutputTokens`, `maximumCostMicrounits`, and `maximumElapsedMilliseconds` are nullable bounded non-negative integers: `null` means unlimited and `0` is a real cap. The remaining five fields retain finite positive bounds. Uncached input cannot exceed total input when both lifetime caps are finite; either may independently be unlimited. Scribe-run limits and Patch deadlines remain finite.

Scribe order is `maximumContextReferences`, `maximumContextUtf8Bytes`, `maximumEvidenceReferences`, `maximumEvidenceUtf8Bytes`, `maximumProviderRequests`, `maximumToolRounds`, `maximumToolCalls`, `maximumAttempts`, `maximumInputTokens`, `maximumUncachedInputTokens`, `maximumOutputTokens`, `maximumCostMicrounits`, `maximumElapsedMilliseconds`. Reference counts are 0..512, reference byte ceilings are 0..4,194,304, provider requests are 1..128, tool rounds are 0..128, tool calls are 0..1,024, and attempts and token/cost/time values use the current Documentation Scribe v1 bounds.

`retry` is `retryPolicyId`, `retryPolicyVersion`, with version `1`.

`scribeRequest` is `scribeRequestVersion`, `agentProtocolId`, `toolPolicyAndRegistryId`, `toolPolicyId`, `styleProfileTemplate`, with version `1`. The template order is `styleProfileId`, `outputLanguageId`, `summary`, `remarks`, `exceptions`, `componentPolicy`, `inheritDocDisposition`, `allowedLiterals`, `forbiddenLiterals`, `claimPolicies`, `maximumContentUnits`, `maximumEvidenceRefsPerUnit`. Text policies are `disposition`, `maximumScalars`. Each claim row is `claimCategoryId`, `completeEvidenceRequired`, `allowedAuthorities`. Literal, claim, and authority arrays are distinct and ordinally ordered. The runner expands the single component template into the exact current applicable-component order; repository, audit, target, source, context, and evidence facts never come from this file.

`provider` is `providerConfigurationId`, `modelConfigurationId`, `scribeProtocolId`, `endpoint`, `model`, `requestProfile`. Request-profile order is `thinkingMode`, `reasoningEffort`, `toolChoice`, `continuationPolicy`, `outputTokenField`, using only the selected existing OpenAI-compatible transport vocabulary. The endpoint and model are non-secret. No property may contain a credential.

`costPolicy` is `null` when no currency/rate authority is configured. A finite campaign cost cap then blocks provider admission before dispatch; unlimited cost retains an unpriced-history fact. A configured cost policy measures observed or conservatively estimated cost even when the campaign cost cap is unlimited. Otherwise its order is `currencyId`, `ratePolicyId`, `cachedInputMicrounitsPerMillion`, `uncachedInputMicrounitsPerMillion`, `outputMicrounitsPerMillion`, `reasoningMicrounitsPerMillion`, with non-negative bounded integers.

## Content authorities

The configuration derives immutable content authorities rather than trusting identity strings alone:

- proposal: `{scribeRequestVersion:1,scribeRunResultVersion:1}`;
- context selection: `{contextSelectionPolicyVersion:1,selectionOrder:"current-m1-stable-order"}`;
- M2: `{m2ProjectionVersion:1,maximumPatchElapsedMilliseconds}`;
- retry: `{retryPolicyVersion:1}`;
- Agent protocol: `{scribeProtocolId}`;
- tool policy/registry: `{toolPolicyId}`;
- provider/model request profile: the complete ordered non-secret provider object;
- style: the complete validated style template;
- cost rate: the complete non-null cost object;
- product: the product-owned ID and the SHA derived from the running payload build identity, independently matched to the current CLI product/contract revision.

These authorities, finite Scribe and structural campaign limits, M1-derived snapshot facts, and opaque snapshot binding feed the C1 execution and C2 checkpoint correctness commitments. The six lifetime caps are spending policy excluded from those correctness preimages; their current values remain explicit in the canonical checkpoint. Currency, numeric rates and their content authority remain correctness inputs. A fresh process must derive the same values before a checkpoint can become execution authority. There is no compatibility or migration reader for another configuration shape in this pre-release contract.
