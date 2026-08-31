# Campaign configuration v1

## Format

The campaign configuration is a credential-free UTF-8 JSON document no larger than 262,144 bytes. UTF-8 BOM, comments, trailing commas, duplicate properties, unknown properties, reordered properties, non-integer numbers, and unpaired surrogates are invalid. Every object has a closed property order and no defaults or aliases.

The top-level order is:

```text
campaignConfigurationVersion, planning, budgets, retry, scribeRequest, provider, costPolicy
```

`campaignConfigurationVersion` is `1`. A canonical executable example is checked in at `tests/fixtures/campaign/cli/configuration-valid.json` (its product-revision SHA is a fixture placeholder and a real invocation must pin the current CLI-derived revision).

## Closed object shapes

`planning` is ordered as `campaignLineage`, `targetProfile`, `proposalContractId`, `contextSelectionPolicyId`, `m2ProjectionPolicyId`, `m2ProjectionVersion`, `maximumPatchElapsedMilliseconds`, `productContractRevisionId`, `productContractRevisionSha256`. The profile is `profile.external-api` or `profile.assembly-visible`; M2 projection version is `1`; patch elapsed is positive and no greater than the campaign elapsed ceiling; the product SHA is exactly 64 lowercase hexadecimal characters.

`budgets` contains `campaign` then `scribe`. Campaign order is `maximumBlocks`, `maximumChangedFiles`, `maximumPatchBytes`, `maximumProviderRequests`, `maximumAttemptsPerTarget`, `maximumInputTokens`, `maximumUncachedInputTokens`, `maximumOutputTokens`, `maximumCostMicrounits`, `maximumElapsedMilliseconds`, `maximumCandidatesPerBlock`. Values use the current C1 planning bounds and its positive active ceilings; uncached input cannot exceed total input.

Scribe order is `maximumContextReferences`, `maximumContextUtf8Bytes`, `maximumEvidenceReferences`, `maximumEvidenceUtf8Bytes`, `maximumProviderRequests`, `maximumToolRounds`, `maximumToolCalls`, `maximumAttempts`, `maximumInputTokens`, `maximumUncachedInputTokens`, `maximumOutputTokens`, `maximumCostMicrounits`, `maximumElapsedMilliseconds`. Reference counts are 0..512, reference byte ceilings are 0..4,194,304, provider requests are 1..128, tool rounds are 0..128, tool calls are 0..1,024, and attempts and token/cost/time values use the current Documentation Scribe v1 bounds.

`retry` is `retryPolicyId`, `retryPolicyVersion`, with version `1`.

`scribeRequest` is `scribeRequestVersion`, `agentProtocolId`, `toolPolicyAndRegistryId`, `toolPolicyId`, `styleProfileTemplate`, with version `1`. The template order is `styleProfileId`, `outputLanguageId`, `summary`, `remarks`, `exceptions`, `componentPolicy`, `inheritDocDisposition`, `allowedLiterals`, `forbiddenLiterals`, `claimPolicies`, `maximumContentUnits`, `maximumEvidenceRefsPerUnit`. Text policies are `disposition`, `maximumScalars`. Each claim row is `claimCategoryId`, `completeEvidenceRequired`, `allowedAuthorities`. Literal, claim, and authority arrays are distinct and ordinally ordered. The runner expands the single component template into the exact current applicable-component order; repository, audit, target, source, context, and evidence facts never come from this file.

`provider` is `providerConfigurationId`, `modelConfigurationId`, `scribeProtocolId`, `endpoint`, `model`, `requestProfile`. Request-profile order is `thinkingMode`, `reasoningEffort`, `toolChoice`, `continuationPolicy`, `outputTokenField`, using only the selected existing OpenAI-compatible transport vocabulary. The endpoint and model are non-secret. No property may contain a credential.

`costPolicy` is `null` when cost enforcement is disabled. Otherwise its order is `currencyId`, `ratePolicyId`, `cachedInputMicrounitsPerMillion`, `uncachedInputMicrounitsPerMillion`, `outputMicrounitsPerMillion`, `reasoningMicrounitsPerMillion`, with non-negative bounded integers.

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
- product: the caller-pinned ID and SHA, independently matched to the current CLI product/contract revision.

These authorities, the complete validated limits, M1-derived snapshot facts, and opaque snapshot binding feed the C1 execution and C2 checkpoint commitments. A fresh process must derive the same values before a checkpoint can become execution authority. There is no compatibility or migration reader for another configuration shape in this pre-release contract.
