using ContractScribe.Core;

namespace ContractScribe.Roslyn;

internal static class AuditInputAssembler
{
    public static IReadOnlyList<AuditRecordInput> Assemble(
        ClassificationSet classifications,
        PolicyDocumentV1 policy,
        PolicyEvidenceExtractionOutcome extraction)
    {
        ArgumentNullException.ThrowIfNull(classifications);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(extraction);
        if (extraction.Status != PolicyEvidenceExtractionStatus.Success
            || classifications.TargetProfile != policy.TargetProfile)
        {
            throw new InvalidOperationException("Accepted component outcomes are not mutually bound.");
        }

        var empty = PolicyConfigurationEvaluator.Evaluate(
            policy,
            Array.Empty<PolicyContributionInput>());
        if (empty.Status != PolicyRunStatus.Success || empty.ContributionSet is null)
        {
            throw new InvalidOperationException("The accepted Policy evaluator did not produce the empty set.");
        }

        var bindings = new Dictionary<string, PolicyEvidenceSubjectBinding>(StringComparer.Ordinal);
        foreach (var binding in extraction.Bindings)
        {
            if (!bindings.TryAdd(SubjectKey(binding.Subject), binding))
            {
                throw new InvalidOperationException("The extractor returned a duplicate subject binding.");
            }
        }

        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var inputs = new List<AuditRecordInput>(
            classifications.Targets.Length
            + classifications.Components.Length
            + classifications.Unresolved.Length);
        foreach (var target in classifications.Targets)
        {
            var key = SubjectKey(target.SymbolRef, null, null);
            if (target.SupportStatus == SupportStatus.Supported)
            {
                var binding = RequireBinding(bindings, consumed, key);
                inputs.Add(AuditInput.Target(
                    target,
                    binding.PolicyContributions,
                    binding.Evidence));
            }
            else
            {
                RejectBinding(bindings, key);
                inputs.Add(AuditInput.Target(target, empty.ContributionSet, null));
            }
        }

        foreach (var component in classifications.Components)
        {
            var key = SubjectKey(
                component.ParentSymbolRef,
                component.ComponentKind,
                component.Identity);
            if (DocumentationObserver.IsObservableComponent(component))
            {
                var binding = RequireBinding(bindings, consumed, key);
                inputs.Add(AuditInput.Component(
                    component,
                    binding.PolicyContributions,
                    binding.Evidence));
            }
            else
            {
                RejectBinding(bindings, key);
                inputs.Add(AuditInput.Component(component, empty.ContributionSet, null));
            }
        }

        foreach (var unresolved in classifications.Unresolved)
        {
            inputs.Add(AuditInput.Unresolved(unresolved, empty.ContributionSet));
        }

        if (consumed.Count != bindings.Count)
        {
            throw new InvalidOperationException("The extractor returned an extra or unmatched binding.");
        }
        return inputs;
    }

    private static PolicyEvidenceSubjectBinding RequireBinding(
        IReadOnlyDictionary<string, PolicyEvidenceSubjectBinding> bindings,
        ISet<string> consumed,
        string key)
    {
        if (!bindings.TryGetValue(key, out var binding) || !consumed.Add(key))
        {
            throw new InvalidOperationException("A required extractor binding is missing or stale.");
        }
        return binding;
    }

    private static void RejectBinding(
        IReadOnlyDictionary<string, PolicyEvidenceSubjectBinding> bindings,
        string key)
    {
        if (bindings.ContainsKey(key))
        {
            throw new InvalidOperationException("A skipped subject cannot consume fabricated evidence.");
        }
    }

    private static string SubjectKey(DocumentationObservationSubject subject) =>
        SubjectKey(
            subject.ParentSymbolRef,
            subject.ComponentKind,
            subject.ComponentIdentity);

    private static string SubjectKey(
        SymbolRef parent,
        ComponentKind? kind,
        string? identity) => string.Join(
            "\0",
            parent.CompilationContextRef,
            parent.DocumentationCommentId,
            kind is null ? string.Empty : ClassificationVocabulary.GetId(kind.Value),
            identity ?? string.Empty);
}
