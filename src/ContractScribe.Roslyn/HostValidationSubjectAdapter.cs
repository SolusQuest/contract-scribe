using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContractScribe.Core;
using ContractScribe.Core.Hosting;

namespace ContractScribe.Roslyn;

internal static class HostValidationSubjectAdapter
{
    private const int MaximumRequestBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static bool IsEnabledFor(Assembly cliAssembly)
    {
        ArgumentNullException.ThrowIfNull(cliAssembly);
        var cliMetadata = HostBuildMetadata.Read(cliAssembly);
        var roslynMetadata = HostBuildMetadata.Read(typeof(HostValidationSubjectAdapter).Assembly);
        if (cliMetadata is null && roslynMetadata is null)
        {
            return false;
        }
        if (cliMetadata is null || roslynMetadata is null || cliMetadata != roslynMetadata)
        {
            throw new InvalidOperationException(
                "The CLI and Roslyn validation subject provenance metadata must match exactly.");
        }
        return true;
    }

    public static async Task<int> RunAsync(
        string requestPath,
        string responsePath,
        Assembly cliAssembly)
    {
        if (!IsEnabledFor(cliAssembly))
        {
            throw new InvalidOperationException(
                "The validation subject adapter is not enabled in this artifact.");
        }
        var metadata = HostBuildMetadata.Read(cliAssembly)!;
        return await RunCoreAsync(
            requestPath,
            responsePath,
            metadata.ToProvenance()).ConfigureAwait(false);
    }

    internal static Task<int> RunForTestsAsync(
        string requestPath,
        string responsePath,
        HostBuildProvenance provenance) =>
        RunCoreAsync(requestPath, responsePath, provenance);

    private static async Task<int> RunCoreAsync(
        string requestPath,
        string responsePath,
        HostBuildProvenance provenance)
    {
        var request = ReadCanonical<ValidationSubjectRequest>(
            requestPath,
            MaximumRequestBytes);
        ValidateRequest(request, responsePath);
        var fixture = ReadCanonical<ValidationFixtureEnvelope>(
            Path.Join(request.RepositoryRoot, ".contractscribe-fixture.json"),
            MaximumRequestBytes);

        if (request.NetworkOperationLogPath is not null)
        {
            File.WriteAllText(
                request.NetworkOperationLogPath,
                "{\"formatVersion\":\"contractscribe-network-operation-recorder-v1\",\"state\":\"active\"}\n",
                Utf8NoBom);
        }

        using var cancellation = new CancellationTokenSource();
        var control = new SubjectControlAdapter(request, cancellation);
        var resultPath = Path.GetFullPath(Path.Join(
            request.RepositoryRoot,
            "TestResults/audit-result.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        var publicationTarget = ResolvedPublicationTarget.ForValidationFixture(
            request.RepositoryRoot);
        var fault = ResolveFault(request.PublicationFault);
        var lateAttemptKind = ResolveLateAttempt(request);
        var transitions = new SubjectTransitionRecorder(
            request.TransitionLogPath,
            ResolveTransitionPlan(request, resultPath, fault, lateAttemptKind));
        var controls = new ProductionAuditHostControls(
            fault,
            control.ReachAsync,
            transitions.Record,
            lateAttemptKind is not null
                ? _ => Task.CompletedTask
                : null,
            lateAttemptKind ?? ProductionLateAttemptKind.LateCompletion);
        var productionRequest = new ProductionAuditRequest(
            request.RepositoryRoot,
            ResolveInputPath(fixture.Fixture),
            ResolvePolicyBytes(fixture.Fixture),
            publicationTarget,
            provenance,
            request.AuditTemporaryRoot,
            request.TemporaryDiskGate?.OutputStagingRoot,
            ResolveGeneratedSources(fixture.Fixture));
        var outcome = await new ProductionAuditHost(provenance).RunAsync(
            productionRequest,
            controls,
            cancellation.Token).ConfigureAwait(false);
        var response = CreateResponse(request, fixture.Fixture, productionRequest, outcome);
        WriteCanonicalAtomic(responsePath, response);
        return 0;
    }

    private static void ValidateRequest(
        ValidationSubjectRequest request,
        string responsePath)
    {
        if (request.FormatVersion != "contractscribe-m1-host-validation-subject-request-v1"
            || request.SubjectKind != "production-host"
            || !Path.IsPathRooted(request.RepositoryRoot)
            || !Directory.Exists(request.RepositoryRoot)
            || !Path.IsPathRooted(request.ResponsePath)
            || !string.Equals(
                Path.GetFullPath(request.ResponsePath),
                Path.GetFullPath(responsePath),
                PathComparison())
            || request.SynchronizationGates.Count > 1)
        {
            throw new InvalidOperationException("The validation subject request is invalid.");
        }
    }

    private static ProductionHostFault ResolveFault(
        PublicationFaultRequest? publicationFault)
    {
        if (publicationFault is not null)
        {
            if (publicationFault is
                {
                    Operation: "invalidate-existing",
                    Occurrence: 1,
                    Failure: "io-exception",
                    StagingRelativePath: null,
                })
            {
                return ProductionHostFault.PublicationInvalidation;
            }
            if (publicationFault is
                {
                    Operation: "atomic-replace",
                    Occurrence: 1,
                    Failure: "io-exception",
                    StagingRelativePath: "TestResults/.audit-result.json.contractscribe-stage",
                })
            {
                return ProductionHostFault.PublicationFinalization;
            }
            throw new InvalidOperationException("The publication fault request is invalid.");
        }
        return ProductionHostFault.None;
    }

    private static ProductionLateAttemptKind? ResolveLateAttempt(
        ValidationSubjectRequest request)
    {
        var postTerminalAttempt = request.PostTerminalAttempt;
        if (postTerminalAttempt is not null
            && postTerminalAttempt is not
            {
                ExecutionOutcome: "succeeded",
                Timing: "after-publication-failure-commit",
                Occurrence: 1,
            })
        {
            throw new InvalidOperationException("The post-terminal attempt is invalid.");
        }
        if (postTerminalAttempt is not null
            || request.ControlAction == "release-late-completion")
        {
            return ProductionLateAttemptKind.LateCompletion;
        }
        if (request.ControlAction == "cancel"
            && request.TransitionLogPath is not null)
        {
            return ProductionLateAttemptKind.CompetingTerminal;
        }
        return null;
    }

    private static SubjectTransitionPlan ResolveTransitionPlan(
        ValidationSubjectRequest request,
        string resultPath,
        ProductionHostFault fault,
        ProductionLateAttemptKind? lateAttemptKind)
    {
        if (request.TransitionLogPath is null)
        {
            return SubjectTransitionPlan.None;
        }
        if (fault == ProductionHostFault.PublicationInvalidation)
        {
            return SubjectTransitionPlan.PublicationInvalidationFailure;
        }
        if (fault == ProductionHostFault.PublicationFinalization)
        {
            return SubjectTransitionPlan.PublicationFinalizationFailure;
        }
        if (lateAttemptKind == ProductionLateAttemptKind.CompetingTerminal)
        {
            return SubjectTransitionPlan.TerminalPrecedence;
        }
        if (lateAttemptKind == ProductionLateAttemptKind.LateCompletion)
        {
            return SubjectTransitionPlan.LateCompletion;
        }
        return File.Exists(resultPath)
            ? SubjectTransitionPlan.StaleInvalidation
            : SubjectTransitionPlan.SameDirectoryAtomic;
    }

    private static string ResolveInputPath(string fixtureProfile) => fixtureProfile switch
    {
        "entry.sln" => "Fixture.sln",
        "entry.slnx" => "Fixture.slnx",
        "entry.slnf" => "Fixture.slnf",
        "entry.non-csharp" => "Fixture.fsproj",
        "path.lexical-parent" => "../outside.csproj",
        _ => "Fixture.csproj",
    };

    private static byte[] ResolvePolicyBytes(string fixtureProfile)
    {
        var json = fixtureProfile switch
        {
            "failure.invalid-input" => "{}",
            "profile.assembly-visible" =>
                "{\"defaultDecision\":\"optional\",\"schemaVersion\":1,\"targetProfile\":\"profile.assembly-visible\"}",
            "audit-outcome.violation" =>
                "{\"defaultDecision\":\"required\",\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\"}",
            "audit-outcome.skipped" =>
                "{\"defaultDecision\":\"optional\",\"rules\":[{\"decision\":\"required\",\"id\":\"conflict-a\",\"priority\":0},{\"decision\":\"forbidden\",\"id\":\"conflict-b\",\"priority\":0}],\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\"}",
            _ =>
                "{\"defaultDecision\":\"optional\",\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\"}",
        };
        return Encoding.UTF8.GetBytes(json + "\n");
    }

    private static IReadOnlyList<ToolGeneratedSourceInput>? ResolveGeneratedSources(
        string fixtureProfile) => fixtureProfile == "entry.source-generator"
            ?
            [
                new ToolGeneratedSourceInput(
                    "Fixture.csproj",
                    "ContractScribe",
                    "ValidationFixtureGenerator",
                    "GeneratedFixture",
                    "namespace ContractScribe.ValidationFixture; public sealed class GeneratedFixture { }"),
            ]
            : null;

    private static ValidationSubjectResponse CreateResponse(
        ValidationSubjectRequest request,
        string fixtureProfile,
        ProductionAuditRequest productionRequest,
        ProductionAuditOutcome outcome)
    {
        var terminal = outcome.Terminal;
        var committedResult = terminal.ExecutionOutcome == HostExecutionOutcome.Succeeded;
        var failure = terminal.Failure;
        var executionOutcome = HostVocabulary.GetId(terminal.ExecutionOutcome);
        var auditOutcome = terminal.AuditOutcome is null
            ? null
            : AuditOutcomeId(terminal.AuditOutcome.Value);
        var canonical = committedResult && outcome.CanonicalResult is not null
            ? new CanonicalResultFact(
                Sha256(outcome.CanonicalResult),
                outcome.CanonicalResult.LongLength,
                "canonical-json-utf8-no-bom-single-lf",
                true)
            : null;
        var diagnostics = terminal.Diagnostics
            .Select(diagnostic => new NormalizedDiagnosticResponse(
                diagnostic.Code,
                HostVocabulary.GetId(diagnostic.Stage)))
            .ToArray();
        var toolchain = terminal.Toolchain;
        var toolchainSelected = toolchain.SelectionState == HostToolchainSelectionState.Selected;
        var measuredBounds = CreateMeasuredBounds(fixtureProfile, diagnostics, terminal);
        var loaderFact = outcome.LoaderFact?.Code == "loader.unsupported.multi-targeting"
                ? new LoaderObservationResponse(
                    "loader.unsupported.multi-targeting",
                    "whole-input-rejected",
                    false,
                    false)
                : null;
        var outputStatus = committedResult ? "committed" : "not-committed";
        var hostFacts = new HostFactsResponse(
            terminal.Provenance.SourceConfigurationId,
            terminal.Provenance.SourceRevision,
            terminal.Provenance.ContractBaselineSha256,
            terminal.Provenance.FailureRegistrySha256,
            terminal.Provenance.CalibratedBoundsSha256,
            toolchainSelected ? toolchain.SdkVersion : null,
            toolchainSelected ? toolchain.RuntimeVersion : null,
            toolchainSelected ? toolchain.MsbuildVersion : null,
            diagnostics,
            new OutputCommitResponse(outputStatus, committedResult ? canonical?.Sha256 : null),
            measuredBounds,
            loaderFact,
            toolchainSelected ? "selected" : "not-selected");

        return new ValidationSubjectResponse(
            "contractscribe-m1-host-validation-subject-response-v1",
            request.VectorId,
            request.RunId,
            "started",
            "normal",
            auditOutcome,
            executionOutcome,
            failure is null ? null : terminal.Provenance.FailureRegistrySha256,
            failure?.Code,
            failure is null ? null : HostVocabulary.GetId(failure.Stage),
            "committed",
            HostArtifactStateId(terminal.OutputCommit.State),
            ProjectEnforcementClass(fixtureProfile, terminal, outcome.LoaderFact),
            ProjectObservation(fixtureProfile, productionRequest, outcome),
            canonical,
            hostFacts);
    }

    private static IReadOnlyList<MeasuredBoundResponse> CreateMeasuredBounds(
        string fixtureProfile,
        IReadOnlyList<NormalizedDiagnosticResponse> diagnostics,
        HostTerminalRecord terminal)
    {
        if (fixtureProfile == "bounds.temporary-disk")
        {
            return terminal.MeasuredBounds
                .Where(item => item.Name == "temporary-disk-bytes")
                .Select(item => new MeasuredBoundResponse(
                    item.Name,
                    item.Unit,
                    item.Measured,
                    item.Threshold,
                    HostVocabulary.GetId(item.EnforcementClass)))
                .ToArray();
        }
        if (fixtureProfile == "diagnostics.over-limit")
        {
            return
            [
                Bound("diagnostic-count", "count", diagnostics.Count),
                Bound(
                    "diagnostic-utf8-bytes",
                    "bytes",
                    SerializeCanonical(diagnostics).LongLength),
            ];
        }
        if (fixtureProfile == "process.toolchain-owned")
        {
            return terminal.MeasuredBounds
                .Where(item => item.Name == "toolchain-subprocess-count")
                .Select(item => new MeasuredBoundResponse(
                    item.Name,
                    item.Unit,
                    item.Measured,
                    item.Threshold,
                    HostVocabulary.GetId(item.EnforcementClass)))
                .ToArray();
        }
        return [];
    }

    private static MeasuredBoundResponse Bound(string name, string unit, long measured) =>
        new(
            name,
            unit,
            measured,
            HostContractResources.RequireBound(name),
            name == "toolchain-subprocess-count"
                ? "observable-only"
                : "internally-enforceable");

    private static string ProjectObservation(
        string fixtureProfile,
        ProductionAuditRequest productionRequest,
        ProductionAuditOutcome outcome)
    {
        var terminal = outcome.Terminal;
        if (fixtureProfile == "path.lexical-parent"
            && terminal.ExecutionOutcome == HostExecutionOutcome.InvalidInput
            && productionRequest.InputPath.Contains("..", StringComparison.Ordinal))
        {
            return "path.lexical-escape-invalid";
        }
        if (outcome.LoaderFact?.Code == "graph.restore-assets-missing")
        {
            return "toolchain.missing-assets-classified";
        }
        if (outcome.LoaderFact?.Code == "loader.unsupported.multi-targeting")
        {
            return "support.multi-targeting-rejected-no-partial-result";
        }
        if (terminal.Failure is not null)
        {
            return terminal.ExecutionOutcome switch
            {
                HostExecutionOutcome.InvalidInput => productionRequest.InputPath.EndsWith(
                    ".slnf",
                    StringComparison.OrdinalIgnoreCase)
                        ? "support.slnf-unsupported"
                        : productionRequest.InputPath.EndsWith(
                            ".fsproj",
                            StringComparison.OrdinalIgnoreCase)
                            ? "support.non-csharp-unsupported"
                            : "failure.invalid-input",
                HostExecutionOutcome.EnvironmentUnavailable => "failure.environment-unavailable",
                HostExecutionOutcome.LoadFailure => "failure.load",
                HostExecutionOutcome.AuditError => "failure.audit-error",
                HostExecutionOutcome.PublicationFailure when terminal.Failure.Code
                    == "host.publication.invalidation-failed" =>
                    "publication.invalidation-failure-committed",
                HostExecutionOutcome.PublicationFailure =>
                    "publication.finalization-failure-committed",
                HostExecutionOutcome.Cancelled when fixtureProfile
                    == "gate.before-commit" => "cancellation.cancelled-before-commit",
                HostExecutionOutcome.Cancelled => "terminal.late-completion-rejected",
                HostExecutionOutcome.Timeout => "failure.environment-unavailable",
                _ => throw new InvalidOperationException(
                    "The production terminal record has no response projection."),
            };
        }

        return fixtureProfile switch
        {
            "gate.after-commit" => "cancellation.committed-outcome-wins",
            "diagnostics.over-limit" => "diagnostics.bounded-sanitized",
            "repository-write.protected" => "repository.protected-files-unchanged",
            "repository-write.design-time" => "repository.design-time-output-bounded",
            "network.declared-operation-markers" =>
                "network.no-contractscribe-initiated-operation",
            "global-json-policy" => "toolchain.identities-recorded",
            "process.topology" => "process.one-runtime-zero-workers",
            "process.toolchain-owned" => "process.toolchain-subprocesses-bounded",
            "bounds.temporary-disk" => "bounds.temporary-disk-calibrated",
            "entry.sln" => "support.sln-accepted",
            "entry.slnx" => "support.slnx-accepted",
            "entry.csproj" => "support.csproj-accepted",
            "entry.analyzer" => "support.analyzer-trusted-observed",
            "entry.custom-target" => "support.custom-target-trusted-observed",
            _ => terminal.AuditOutcome switch
            {
                AuditOutcome.Compliant => "audit.outcome.compliant",
                AuditOutcome.Violation => "audit.outcome.violation-success",
                AuditOutcome.Skipped => "audit.outcome.skipped",
                _ => throw new InvalidOperationException(
                    "A successful production result has no audit outcome."),
            },
        };
    }

    private static string ProjectEnforcementClass(
        string fixtureProfile,
        HostTerminalRecord terminal,
        LoaderFact? loaderFact)
    {
        if (terminal.Failure is not null
            || loaderFact?.Code == "graph.restore-assets-missing")
        {
            return "internally-enforceable";
        }
        return fixtureProfile is
            "repository-write.protected" or
            "repository-write.design-time" or
            "network.declared-operation-markers" or
            "process.topology" or
            "process.toolchain-owned" or
            "entry.analyzer" or
            "entry.source-generator" or
            "entry.custom-target"
                ? "observable-only"
                : "internally-enforceable";
    }

    private static string AuditOutcomeId(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Compliant => "compliant",
        AuditOutcome.Violation => "violation",
        AuditOutcome.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static string HostArtifactStateId(HostArtifactState state) => state switch
    {
        HostArtifactState.Absent => "absent",
        HostArtifactState.Invalidated => "invalidated",
        HostArtifactState.Staged => "staged",
        HostArtifactState.Published => "published",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static T ReadCanonical<T>(string path, int maximumBytes)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0
            || bytes.Length > maximumBytes
            || bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            throw new InvalidOperationException("The validation artifact bytes are invalid.");
        }
        _ = Utf8NoBom.GetString(bytes);
        RejectDuplicateProperties(bytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        if (!bytes.AsSpan().SequenceEqual(SerializeCanonical(document.RootElement)))
        {
            throw new InvalidOperationException("The validation artifact is not canonical JSON.");
        }
        return document.RootElement.Deserialize<T>(JsonOptions)
            ?? throw new InvalidOperationException("The validation artifact is null.");
    }

    private static void WriteCanonicalAtomic<T>(string path, T value)
    {
        var bytes = SerializeCanonical(value);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The response path has no parent.");
        Directory.CreateDirectory(directory);
        var staging = Path.Join(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.stage");
        try
        {
            using (var stream = new FileStream(
                staging,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    private static byte[] SerializeCanonical<T>(T value) =>
        SerializeCanonical(JsonSerializer.SerializeToElement(value, JsonOptions));

    private static byte[] SerializeCanonical(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            WriteSorted(writer, element);
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WriteSorted(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject()
                         .OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteSorted(writer, property.Value);
            }
            writer.WriteEndObject();
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
            {
                WriteSorted(writer, item);
            }
            writer.WriteEndArray();
            return;
        }
        element.WriteTo(writer);
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                objects.Push(new HashSet<string>(StringComparer.Ordinal));
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                objects.Pop();
            }
            else if (reader.TokenType == JsonTokenType.PropertyName
                && (objects.Count == 0
                    || !objects.Peek().Add(reader.GetString() ?? string.Empty)))
            {
                throw new InvalidOperationException(
                    "The validation artifact contains a duplicate property.");
            }
        }
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed class SubjectControlAdapter(
        ValidationSubjectRequest request,
        CancellationTokenSource cancellation)
    {
        public async Task ReachAsync(
            ProductionHostControlPoint point,
            CancellationToken cancellationToken)
        {
            var gateName = SelectGate(point);
            if (gateName is null || request.ControlRoot is null)
            {
                return;
            }
            Directory.CreateDirectory(request.ControlRoot);
            if (request.ControlAction == "measure-temporary-disk")
            {
                WriteTemporaryDiskBoundary(freeze: true);
            }
            File.WriteAllText(
                Path.Join(request.ControlRoot, $"{gateName}.reached"),
                string.Empty,
                Utf8NoBom);
            if (request.ControlAction == "external-kill")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }
            var release = Path.Join(request.ControlRoot, $"{gateName}.release");
            while (!File.Exists(release))
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
            if (request.ControlAction == "measure-temporary-disk")
            {
                WriteTemporaryDiskBoundary(freeze: false);
            }
            if (request.ControlAction is "cancel" or "release-late-completion")
            {
                if (!File.Exists(Path.Join(request.ControlRoot, "cancel.requested")))
                {
                    throw new InvalidOperationException(
                        "The cancellation control marker is missing.");
                }
                cancellation.Cancel();
            }
        }

        private string? SelectGate(ProductionHostControlPoint point)
        {
            if (request.SynchronizationGates.Count == 0)
            {
                return null;
            }
            var requested = request.SynchronizationGates[0];
            return point switch
            {
                ProductionHostControlPoint.BeforeCommit
                    when requested is "before-commit" or "publication-before-commit" => requested,
                ProductionHostControlPoint.AfterCommit
                    when requested is "after-commit" or "publication-after-commit" => requested,
                ProductionHostControlPoint.LateCompletion
                    when requested == "late-completion" => requested,
                ProductionHostControlPoint.PublicationStagingReady
                    when requested == "publication-staging-ready" => requested,
                ProductionHostControlPoint.ProcessObservation
                    when requested is "process-observation" or "forced-termination" => requested,
                ProductionHostControlPoint.TemporaryDiskHighWater
                    when requested == "temporary-disk-high-water" => requested,
                _ => null,
            };
        }

        private void WriteTemporaryDiskBoundary(bool freeze)
        {
            var contract = request.TemporaryDiskGate
                ?? throw new InvalidOperationException(
                    "The temporary-disk gate contract is missing.");
            var sentinel = freeze
                ? contract.FreezeSentinelName
                : contract.ReleaseSentinelName;
            foreach (var root in new[]
                     {
                         contract.TemporaryWorkRoot,
                         contract.OutputStagingRoot,
                     })
            {
                Directory.CreateDirectory(root);
                using var stream = new FileStream(
                    Path.Join(root, sentinel),
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Flush(flushToDisk: true);
            }
        }
    }

    private sealed class SubjectTransitionRecorder
    {
        private readonly string? path;
        private readonly HashSet<string>? selectedEvents;
        private int sequence;

        public SubjectTransitionRecorder(string? path, SubjectTransitionPlan plan)
        {
            this.path = path;
            selectedEvents = ExpectedEvents(plan)?.ToHashSet(StringComparer.Ordinal);
        }

        public void Record(string transition)
        {
            if (path is null || selectedEvents?.Contains(transition) != true)
            {
                return;
            }
            var line = JsonSerializer.Serialize(
                new { sequence = ++sequence, @event = transition },
                JsonOptions);
            File.AppendAllText(path, line + "\n", Utf8NoBom);
        }

        private static IReadOnlyList<string>? ExpectedEvents(SubjectTransitionPlan plan) =>
            plan switch
            {
                SubjectTransitionPlan.LateCompletion =>
                    ["terminal-commit-cancelled", "late-terminal-attempt-rejected"],
                SubjectTransitionPlan.TerminalPrecedence =>
                    ["terminal-commit-cancelled", "competing-terminal-attempt-rejected"],
                SubjectTransitionPlan.StaleInvalidation =>
                    ["invalidation-completed", "failure-prone-stage-entered"],
                SubjectTransitionPlan.SameDirectoryAtomic =>
                    ["staging-created-in-destination", "atomic-rename-committed"],
                SubjectTransitionPlan.PublicationInvalidationFailure =>
                    [
                        "invalidation-attempt-failed",
                        "terminal-commit-publication-failure",
                        "late-terminal-attempt-rejected",
                    ],
                SubjectTransitionPlan.PublicationFinalizationFailure =>
                    [
                        "invalidation-completed",
                        "failure-prone-stage-entered",
                        "staging-created-in-destination",
                        "atomic-replace-attempt-failed",
                        "staging-cleanup-completed",
                        "terminal-commit-publication-failure",
                        "late-terminal-attempt-rejected",
                    ],
                _ => null,
            };
    }

    private enum SubjectTransitionPlan
    {
        None,
        LateCompletion,
        TerminalPrecedence,
        StaleInvalidation,
        SameDirectoryAtomic,
        PublicationInvalidationFailure,
        PublicationFinalizationFailure,
    }

    private sealed record ValidationFixtureEnvelope(string Fixture);

    private sealed record ValidationSubjectRequest(
        string FormatVersion,
        string SubjectKind,
        string VectorId,
        string RunId,
        string RepositoryRoot,
        string ResponsePath,
        string? ControlRoot,
        IReadOnlyList<string> SynchronizationGates,
        string ControlAction,
        string? NetworkOperationLogPath,
        string? TransitionLogPath,
        string? AuditTemporaryRoot,
        TemporaryDiskGateRequest? TemporaryDiskGate,
        PublicationFaultRequest? PublicationFault,
        PostTerminalAttemptRequest? PostTerminalAttempt);

    private sealed record TemporaryDiskGateRequest(
        string TemporaryWorkRoot,
        string OutputStagingRoot,
        string FreezeSentinelName,
        string ReleaseSentinelName);

    private sealed record PublicationFaultRequest(
        string Operation,
        int Occurrence,
        string Failure,
        string? StagingRelativePath);

    private sealed record PostTerminalAttemptRequest(
        string ExecutionOutcome,
        string Timing,
        int Occurrence);

    private sealed record ValidationSubjectResponse(
        string FormatVersion,
        string VectorId,
        string RunId,
        string ProcessStart,
        string ProcessTermination,
        string? AuditOutcome,
        string? ExecutionOutcome,
        string? FailureRegistryIdentity,
        string? FailureCode,
        string? FailureStage,
        string TerminalState,
        string ArtifactState,
        string EnforcementClass,
        string ObservationCode,
        CanonicalResultFact? CanonicalResult,
        HostFactsResponse HostFacts);

    private sealed record CanonicalResultFact(
        string Sha256,
        long ByteCount,
        string Encoding,
        bool Canonical);

    private sealed record HostFactsResponse(
        string SourceConfigurationId,
        string HostRevision,
        string ContractBaselineSha256,
        string FailureRegistrySha256,
        string CalibratedBoundsSha256,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SelectedSdk,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SelectedRuntime,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SelectedMsbuild,
        IReadOnlyList<NormalizedDiagnosticResponse> NormalizedDiagnosticFacts,
        OutputCommitResponse OutputCommit,
        IReadOnlyList<MeasuredBoundResponse> MeasuredBounds,
        LoaderObservationResponse? LoaderFact,
        string ToolchainSelectionState);

    private sealed record NormalizedDiagnosticResponse(string Code, string Stage);

    private sealed record OutputCommitResponse(string Status, string? Sha256);

    private sealed record MeasuredBoundResponse(
        string Name,
        string Unit,
        long Measured,
        long Threshold,
        string EnforcementClass);

    private sealed record LoaderObservationResponse(
        string Code,
        string Disposition,
        bool SelectedOrDefaultTargetFramework,
        bool PartialResultProduced);
}
