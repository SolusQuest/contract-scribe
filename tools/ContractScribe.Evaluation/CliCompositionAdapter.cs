using System.Reflection;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Roslyn;
using Microsoft.CodeAnalysis;

namespace ContractScribe.Evaluation;

internal sealed record EvaluationCompositionOutcome(
    string Status,
    string Code,
    DocumentationScribeRunResult? RunResult,
    DocumentationPatchExecutionOutcome? PatchOutcome,
    DocumentationPatchAcceptedCandidate? AcceptedCandidate)
{
    public override string ToString() => nameof(EvaluationCompositionOutcome);
}

internal sealed class ProductionCompositionAdapter
{
    private static readonly BindingFlags StaticInternal = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly BindingFlags InstanceInternal = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Type authorityType;
    private readonly MethodInfo createAuthority;
    private readonly MethodInfo selectAudit;
    private readonly MethodInfo execute;
    private readonly MethodInfo assembleAuditInputs;
    private readonly PropertyInfo loadedProjectCompilation;

    internal ProductionCompositionAdapter()
    {
        var assembly = Assembly.Load("ContractScribe.Cli");
        authorityType = RequireType(assembly, "ContractScribe.Cli.DocumentationScribeAuditAuthority");
        var selectionType = RequireType(assembly, "ContractScribe.Cli.DocumentationScribeSelectedAudit");
        var compositionType = RequireType(assembly, "ContractScribe.Cli.DocumentationScribeComposition");
        createAuthority = RequireMethod(authorityType, "Create", StaticInternal,
        [
            typeof(ClassifiedRepositorySession),
            typeof(ObservedRepositorySession),
            typeof(PolicyDocumentV1),
            typeof(IEnumerable<AuditRecordInput>),
            typeof(AuditDocument),
        ]);
        if (createAuthority.ReturnType != authorityType)
        {
            throw new MissingMethodException("evaluation.cli.signature-mismatch");
        }

        selectAudit = RequireMethod(authorityType, "Select", InstanceInternal, [typeof(TargetClassification)]);
        if (selectAudit.ReturnType != selectionType)
        {
            throw new MissingMethodException("evaluation.cli.signature-mismatch");
        }

        execute = RequireMethod(compositionType, "ExecuteAsync", StaticInternal,
        [
            selectionType,
            typeof(ReadOnlyMemory<byte>),
            typeof(DocumentationScribeAttemptId),
            typeof(string),
            typeof(DocumentationScribeRuntimeOptions),
            typeof(IDocumentationScribeModelExchange),
            typeof(CancellationToken),
        ]);
        if (!execute.ReturnType.IsGenericType
            || execute.ReturnType.GetGenericTypeDefinition() != typeof(Task<>))
        {
            throw new MissingMethodException("evaluation.cli.signature-mismatch");
        }

        var assemblerType = RequireType(
            typeof(RepositoryLoader).Assembly,
            "ContractScribe.Roslyn.AuditInputAssembler");
        assembleAuditInputs = RequireMethod(assemblerType, "Assemble", BindingFlags.Static | BindingFlags.Public,
        [
            typeof(ClassificationSet),
            typeof(PolicyDocumentV1),
            typeof(PolicyEvidenceExtractionOutcome),
        ]);
        loadedProjectCompilation = typeof(LoadedProject).GetProperty(
            "Compilation",
            InstanceInternal) ?? throw new MissingMemberException(
                typeof(LoadedProject).FullName,
                "Compilation");
    }

    internal object CreateAuthority(
        ClassifiedRepositorySession session,
        ObservedRepositorySession observations,
        PolicyDocumentV1 policy,
        IEnumerable<AuditRecordInput> inputs,
        AuditDocument audit) =>
        createAuthority.Invoke(null, [session, observations, policy, inputs, audit])
        ?? throw new InvalidOperationException("evaluation.cli.authority-null");

    internal object Select(object authority, TargetClassification target)
    {
        if (authority.GetType() != authorityType)
        {
            throw new ArgumentException("evaluation.cli.authority-type", nameof(authority));
        }

        return selectAudit.Invoke(authority, [target])
            ?? throw new InvalidOperationException("evaluation.cli.selection-null");
    }

    internal IReadOnlyList<AuditRecordInput> AssembleAuditInputs(
        ClassificationSet classifications,
        PolicyDocumentV1 policy,
        PolicyEvidenceExtractionOutcome extraction) =>
        assembleAuditInputs.Invoke(null, [classifications, policy, extraction])
            as IReadOnlyList<AuditRecordInput>
        ?? throw new InvalidOperationException("evaluation.roslyn.audit-inputs-null");

    internal Compilation GetCompilation(LoadedProject project) =>
        loadedProjectCompilation.GetValue(project) as Compilation
        ?? throw new InvalidOperationException("evaluation.roslyn.compilation-null");

    internal async Task<EvaluationCompositionOutcome> ExecuteAsync(
        object selection,
        ReadOnlyMemory<byte> requestBytes,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribeRuntimeOptions runtimeOptions,
        IDocumentationScribeModelExchange exchange,
        CancellationToken cancellationToken)
    {
        var invoked = execute.Invoke(null,
        [
            selection,
            requestBytes,
            attemptId,
            null,
            runtimeOptions,
            exchange,
            cancellationToken,
        ]) ?? throw new InvalidOperationException("evaluation.cli.execution-null");
        var task = (Task)invoked;
        await task.ConfigureAwait(false);
        var result = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?
            .GetValue(task) ?? throw new InvalidOperationException("evaluation.cli.result-null");
        return new EvaluationCompositionOutcome(
            RequireProperty(result, "Status").ToString()
                ?? throw new InvalidOperationException("evaluation.cli.status-null"),
            (string)RequireProperty(result, "Code"),
            Property(result, "RunResult") as DocumentationScribeRunResult,
            Property(result, "PatchOutcome") as DocumentationPatchExecutionOutcome,
            Property(result, "AcceptedCandidate") as DocumentationPatchAcceptedCandidate);
    }

    private static object RequireProperty(object instance, string name) =>
        Property(instance, name) ?? throw new MissingMemberException(instance.GetType().FullName, name);

    private static object? Property(object instance, string name) =>
        instance.GetType().GetProperty(name, InstanceInternal)?.GetValue(instance);

    private static Type RequireType(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: true, ignoreCase: false)!;

    private static MethodInfo RequireMethod(
        Type type,
        string name,
        BindingFlags flags,
        Type[] parameterTypes)
    {
        var method = type.GetMethod(name, flags, binder: null, parameterTypes, modifiers: null);
        return method ?? throw new MissingMethodException(type.FullName, name);
    }
}
