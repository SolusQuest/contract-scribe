using System.Text;
using System.Text.Json;
using System.Xml;

namespace ContractScribe.HostValidation;

public sealed record NetworkEvidenceDisposition(
    string ObservationCode,
    string Verdict,
    IReadOnlyList<string> DiagnosticCodes);

public sealed record NetworkScanFailureDisposition(
    string ObservationCode,
    string CauseClass);

public static class NetworkEvidenceEvaluator
{
    public static NetworkEvidenceObservation Evaluate(
        BundleContext context,
        SubjectSourceConfiguration source,
        CellMaterialization materialization,
        string? recorderState,
        bool processObservationComplete,
        IReadOnlyList<ObservedProcess> observedProcesses,
        RepositoryDelta repositoryDelta)
    {
        var definitions = context.NetworkEvidenceProfile.Methods;
        var inputIdentities = ExpectedInputIdentities(source, materialization);
        var results = new[]
        {
            EvaluateDeclaredInventory(
                definitions[0],
                context.Root,
                source,
                inputIdentities[0]),
            EvaluateBoundedScan(
                definitions[1],
                context.Root,
                source,
                materialization,
                inputIdentities[1]),
            EvaluateRecorder(definitions[2], recorderState, inputIdentities[2]),
            EvaluateRestoreObserver(
                definitions[3],
                inputIdentities[3],
                processObservationComplete,
                observedProcesses,
                repositoryDelta)
        };
        return new(
            context.NetworkEvidenceProfile.ProfileId,
            NetworkClaimSetRegistry.ClaimSetId,
            results);
    }

    public static NetworkEvidenceDisposition Classify(
        NetworkEvidenceProfileManifest profile,
        NetworkEvidenceObservation? observation,
        IReadOnlyList<string>? expectedInputIdentities = null)
    {
        if (observation is null)
        {
            return new(
                "network.evidence-profile-missing",
                "protocol-invalid-observation",
                ["HV247_NETWORK_EVIDENCE_PROTOCOL_FAILURE"]);
        }
        if (observation.ProfileId != profile.ProfileId
            || observation.ClaimSetId != NetworkClaimSetRegistry.ClaimSetId
            || observation.Methods.Count != profile.Methods.Count)
        {
            throw new ProtocolException("HV246_NETWORK_PROTECTED_INPUT_INVALIDATED");
        }

        for (var index = 0; index < profile.Methods.Count; index++)
        {
            var expected = profile.Methods[index];
            var actual = observation.Methods[index];
            if (actual.MethodId != expected.MethodId
                || actual.MethodVersion != expected.MethodVersion
                || actual.CoverageLimitationId != expected.CoverageLimitationId
                || expectedInputIdentities is not null
                    && (expectedInputIdentities.Count != profile.Methods.Count
                        || actual.InputIdentity != expectedInputIdentities[index]))
            {
                throw new ProtocolException("HV246_NETWORK_PROTECTED_INPUT_INVALIDATED");
            }
            if (actual.Status is not ("complete" or "finding" or "incomplete")
                || actual.Status == "complete" && actual.CauseClass is not null
                || actual.Status == "finding"
                    && actual.CauseClass != "subject-nonconformance"
                || actual.Status == "incomplete"
                    && actual.CauseClass is not (
                        "protected-input-invalidated"
                        or "protocol-failure"
                        or "subject-nonconformance"
                        or "environment-or-infrastructure-incomplete"))
            {
                return new(
                    "network.evidence-profile-invalid",
                    "protocol-invalid-observation",
                    ["HV247_NETWORK_EVIDENCE_PROTOCOL_FAILURE"]);
            }
        }

        var methods = observation.Methods;
        if (methods.Any(method =>
                method.Status == "incomplete"
                && method.CauseClass == "protected-input-invalidated"))
        {
            throw new ProtocolException("HV246_NETWORK_PROTECTED_INPUT_INVALIDATED");
        }
        if (methods.Any(method =>
                method.Status == "incomplete"
                && method.CauseClass == "protocol-failure"))
        {
            return new(
                methods.First(method => method.CauseClass == "protocol-failure").ObservationCode,
                "protocol-invalid-observation",
                ["HV247_NETWORK_EVIDENCE_PROTOCOL_FAILURE"]);
        }
        if (methods.Any(method =>
                method.Status == "finding"
                || method.CauseClass == "subject-nonconformance"))
        {
            return new(
                methods.First(method =>
                    method.Status == "finding"
                    || method.CauseClass == "subject-nonconformance").ObservationCode,
                "subject-nonconformance",
                []);
        }
        if (methods.Any(method =>
                method.Status == "incomplete"
                && method.CauseClass == "environment-or-infrastructure-incomplete"))
        {
            return new(
                methods.First(method =>
                    method.CauseClass == "environment-or-infrastructure-incomplete").ObservationCode,
                "vector-infrastructure-incomplete",
                ["HV248_NETWORK_EVIDENCE_OBSERVER_INCOMPLETE"]);
        }
        if (methods.Any(method => method.Status != "complete"))
        {
            return new(
                "network.evidence-cause-unmapped",
                "protocol-invalid-observation",
                ["HV247_NETWORK_EVIDENCE_PROTOCOL_FAILURE"]);
        }
        return new("network.no-contractscribe-initiated-operation", "matched", []);
    }

    public static IReadOnlyList<string> ExpectedInputIdentities(
        SubjectSourceConfiguration source,
        CellMaterialization materialization)
    {
        var closureIdentity = $"closure.{CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(new
        {
            source.SourceConfigurationId,
            BuiltArtifacts = materialization.BuiltArtifacts
                .Where(artifact => artifact.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .OrderBy(artifact => artifact.Path, StringComparer.Ordinal)
                .ToArray(),
            SelectedRuntimeManifest =
                NetworkOperationSourceScanner.SelectedRuntimeManifestInputIdentity(
                    materialization.SelectedRuntime)
        }))}";
        var observerIdentity = $"observer.{CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(new
        {
            source.SourceConfigurationId,
            materialization.CellId,
            materialization.RunnerImage,
            materialization.SelectedSdk,
            materialization.SelectedRuntime,
            materialization.SelectedMsbuild
        }))}";
        return
        [
            $"inventory.{CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(new
            {
                source.SourceConfigurationId,
                source.DeclaredOperationInventoryId,
                DeclaredNetworkOperationInventoryEvaluator.EvaluatorId
            }))}",
            closureIdentity,
            $"recorder.{source.SourceConfigurationId[7..]}",
            observerIdentity
        ];
    }

    private static NetworkEvidenceMethodResult EvaluateDeclaredInventory(
        NetworkEvidenceMethodDefinition definition,
        string root,
        SubjectSourceConfiguration source,
        string inputIdentity)
    {
        try
        {
            var finding =
                DeclaredNetworkOperationInventoryEvaluator.HasDeclaredNetworkOperation(
                    root,
                    source);
            return Result(
                definition,
                inputIdentity,
                finding ? "finding" : "complete",
                finding
                    ? "network.declared-operation-observed"
                    : "network.declared-operation-inventory-clean",
                finding ? "subject-nonconformance" : null);
        }
        catch (ProtocolException exception) when (
            exception.Code == "HV246_NETWORK_PROTECTED_INPUT_INVALIDATED")
        {
            return Result(
                definition,
                inputIdentity,
                "incomplete",
                "network.declared-inventory-input-invalidated",
                "protected-input-invalidated");
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Result(
                definition,
                inputIdentity,
                "incomplete",
                "network.declared-inventory-input-invalidated",
                "protected-input-invalidated");
        }
        catch (Exception exception) when (
            exception is JsonException
                or XmlException
                or DecoderFallbackException)
        {
            return Result(
                definition,
                inputIdentity,
                "incomplete",
                "network.declared-inventory-invalid",
                "subject-nonconformance");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Result(
                definition,
                inputIdentity,
                "incomplete",
                "network.declared-inventory-observer-incomplete",
                "environment-or-infrastructure-incomplete");
        }
        catch (ProtocolException)
        {
            return Result(
                definition,
                inputIdentity,
                "incomplete",
                "network.declared-inventory-evaluator-failed",
                "protocol-failure");
        }
    }

    private static NetworkEvidenceMethodResult EvaluateBoundedScan(
        NetworkEvidenceMethodDefinition definition,
        string root,
        SubjectSourceConfiguration source,
        CellMaterialization materialization,
        string inputIdentity)
    {
        try
        {
            var finding = NetworkOperationSourceScanner.HasContractScribeInitiatedNetworkOperation(
                root,
                source,
                materialization);
            return Result(
                definition,
                inputIdentity,
                finding ? "finding" : "complete",
                finding
                    ? "network.bounded-source-or-metadata-finding"
                    : "network.bounded-source-and-metadata-clean",
                finding ? "subject-nonconformance" : null);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or BadImageFormatException
                or ProtocolException)
        {
            var protectedInputState = DetermineProtectedInputState(
                root,
                source,
                materialization);
            var disposition = ClassifyBoundedScanFailure(
                exception,
                protectedInputState);
            return Result(
                definition,
                inputIdentity,
                "incomplete",
                disposition.ObservationCode,
                disposition.CauseClass);
        }
    }

    public static NetworkScanFailureDisposition ClassifyBoundedScanFailure(
        Exception exception,
        string protectedInputState)
    {
        if (protectedInputState == "invalidated")
        {
            return new(
                "network.source-or-build-input-invalidated",
                "protected-input-invalidated");
        }
        if (protectedInputState == "inaccessible"
            || exception is IOException or UnauthorizedAccessException)
        {
            return new(
                "network.source-or-build-input-inaccessible",
                "environment-or-infrastructure-incomplete");
        }
        if (protectedInputState != "current")
        {
            return new(
                "network.checked-in-scanner-state-invalid",
                "protocol-failure");
        }
        if (exception is BadImageFormatException
            || exception is ProtocolException
            {
                Code: "HV244_PRODUCTION_DEPENDENCY_CLOSURE"
            })
        {
            return new(
                "network.production-managed-input-invalid",
                "subject-nonconformance");
        }
        if (exception is ProtocolException
            {
                Code: "HV249_SELECTED_RUNTIME_MANIFEST"
            })
        {
            return new(
                "network.selected-runtime-manifest-incomplete",
                "environment-or-infrastructure-incomplete");
        }
        return new(
            "network.checked-in-scanner-failed",
            "protocol-failure");
    }

    private static string DetermineProtectedInputState(
        string root,
        SubjectSourceConfiguration source,
        CellMaterialization materialization)
    {
        var identities = source.SourceAndBuildInputs
            .Concat(
            [
                source.FailureRegistry,
                source.CalibratedBounds,
                source.BuildRecipe,
                source.CommandContract,
                source.ContractBaseline,
                source.EnvironmentPolicy,
                source.Workflow
            ])
            .Concat(materialization.BuiltArtifacts)
            .GroupBy(identity => identity.Path, StringComparer.Ordinal)
            .ToArray();
        if (identities.Any(group =>
                group.Select(identity => identity.Sha256)
                    .Distinct(StringComparer.Ordinal).Count() != 1))
        {
            return "invalidated";
        }
        foreach (var identity in identities.Select(group => group.First()))
        {
            try
            {
                var path = RepositoryPaths.ResolveConfined(
                    root,
                    identity.Path,
                    mustExist: false);
                if (!File.Exists(path))
                {
                    return "invalidated";
                }
                var bytes = File.ReadAllBytes(path);
                if (CanonicalJson.Sha256(bytes) != identity.Sha256)
                {
                    return "invalidated";
                }
            }
            catch (UnauthorizedAccessException)
            {
                return "inaccessible";
            }
            catch (IOException)
            {
                return "inaccessible";
            }
            catch (ProtocolException)
            {
                return "invalidated";
            }
        }
        return "current";
    }

    private static NetworkEvidenceMethodResult EvaluateRecorder(
        NetworkEvidenceMethodDefinition definition,
        string? recorderState,
        string inputIdentity) =>
        recorderState switch
        {
            "empty" => Result(
                definition,
                inputIdentity,
                "complete",
                "network.operation-recorder-empty",
                null),
            "operation-observed" => Result(
                definition,
                inputIdentity,
                "finding",
                "network.operation-recorder-finding",
                "subject-nonconformance"),
            _ => Result(
                definition,
                inputIdentity,
                "incomplete",
                "network.required-subject-recorder-missing",
                "subject-nonconformance")
        };

    private static NetworkEvidenceMethodResult EvaluateRestoreObserver(
        NetworkEvidenceMethodDefinition definition,
        string inputIdentity,
        bool processObservationComplete,
        IReadOnlyList<ObservedProcess> observedProcesses,
        RepositoryDelta repositoryDelta)
    {
        if (!processObservationComplete)
        {
            return Result(
                definition,
                inputIdentity,
                "incomplete",
                "network.process-or-artifact-observer-incomplete",
                "environment-or-infrastructure-incomplete");
        }
        var finding = observedProcesses.Any(process =>
                process.Role is "restore-or-runtime-download" or "unknown-descendant")
            || repositoryDelta.AllowedDesignTimeCreated
                .Concat(repositoryDelta.AllowedDesignTimeChanged)
                .Any(path => path.EndsWith("project.assets.json", StringComparison.Ordinal));
        return Result(
            definition,
            inputIdentity,
            finding ? "finding" : "complete",
            finding
                ? "network.restore-or-runtime-download-observed"
                : "network.restore-runtime-download-observer-clean",
            finding ? "subject-nonconformance" : null);
    }

    private static NetworkEvidenceMethodResult Result(
        NetworkEvidenceMethodDefinition definition,
        string inputIdentity,
        string status,
        string observationCode,
        string? causeClass) =>
        new(
            definition.MethodId,
            definition.MethodVersion,
            inputIdentity,
            definition.CoverageLimitationId,
            status,
            observationCode,
            causeClass);
}
