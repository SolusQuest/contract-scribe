using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.Roslyn;

if (args is ["emit-streams", var streamMarker])
{
    Console.Out.Write($"{streamMarker}-child-out");
    Console.Error.WriteLine($"{streamMarker}-child-error");
    return 0;
}

if (args is ["hold-child", var childMarker])
{
    Console.Out.WriteLine($"{childMarker}:child:{Environment.ProcessId}");
    Console.Error.WriteLine($"{childMarker}:child-error");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

if (args is ["hold-tree", var treeMarker])
{
    var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        ?? Environment.ProcessPath
        ?? "dotnet";
    using var child = Process.Start(new ProcessStartInfo
    {
        FileName = host,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        ArgumentList =
        {
            Assembly.GetExecutingAssembly().Location,
            "hold-child",
            treeMarker,
        },
    }) ?? throw new InvalidOperationException("The owned hold child did not start.");
    _ = Task.Run(async () =>
    {
        while (await child.StandardOutput.ReadLineAsync() is { } line)
        {
            Console.Out.WriteLine(line);
        }
    });
    _ = Task.Run(async () =>
    {
        while (await child.StandardError.ReadLineAsync() is { } line)
        {
            Console.Error.WriteLine(line);
        }
    });
    Console.Out.WriteLine($"{treeMarker}:root:{Environment.ProcessId}:child:{child.Id}");
    Console.Error.WriteLine($"{treeMarker}:root-error");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

if (args.Length < 3)
{
    return 64;
}

var repositoryRoot = args[0];
var inputPath = args[1];
var mode = args[2];
if (mode.StartsWith("lifecycle-", StringComparison.Ordinal))
{
    return await LoaderLifecycleDriver.RunAsync(args);
}

if (mode is "classification" or "policy-evidence")
{
    var expectedLength = mode == "classification" ? 6 : 5;
    if (args.Length != expectedLength)
    {
        return 64;
    }

    var cultureArgument = mode == "classification" ? 4 : 3;
    var timeZoneArgument = cultureArgument + 1;
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(args[cultureArgument]);
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(args[cultureArgument]);
    Environment.SetEnvironmentVariable("TZ", args[timeZoneArgument]);
    TimeZoneInfo.ClearCachedData();
}

using var cancellation = new CancellationTokenSource();
var loader = mode == "cancellation"
    ? new RepositoryLoader(stage =>
    {
        if (stage == LoaderStage.Compilation)
        {
            cancellation.Cancel();
        }
    })
    : new RepositoryLoader();
IReadOnlyList<ToolGeneratedSourceInput>? generated = mode == "failure"
    ?
    [
        new(
            "App/App.csproj",
            "ContractScribe",
            "FixtureTool",
            "Broken",
            "public class {"),
    ]
    : null;

var outcome = await loader.LoadAsync(
    new RepositoryLoadRequest(repositoryRoot, inputPath, generated),
    cancellation.Token);
if (mode == "legacy-success")
{
    var valid = outcome.Status == RepositoryLoadStatus.Success
        && outcome.Session?.Projects.Count == 2;
    if (outcome.Session is not null)
    {
        await outcome.Session.DisposeAsync();
    }
    Console.WriteLine(valid ? "legacy-success" : $"{outcome.Status}:{outcome.PrimaryFailure?.Code}");
    return valid ? 0 : 77;
}
if (mode == "target-framework-environment")
{
    var valid = outcome.Status == RepositoryLoadStatus.Failure
        && outcome.PrimaryFailure?.Code == "graph.target-framework-not-single";
    if (outcome.Session is not null)
    {
        await outcome.Session.DisposeAsync();
    }
    Console.WriteLine(valid ? "target-framework-rejected" : $"{outcome.Status}:{outcome.PrimaryFailure?.Code}");
    return valid ? 0 : 78;
}
if (mode == "classification")
{
    if (outcome.Status != RepositoryLoadStatus.Success || outcome.Session is null)
    {
        return 68;
    }

    var profile = args[3] switch
    {
        "external-api" => TargetProfile.ExternalApi,
        "assembly-visible" => TargetProfile.AssemblyVisible,
        _ => (TargetProfile)(-1),
    };
    if (!Enum.IsDefined(profile))
    {
        await outcome.Session.DisposeAsync();
        return 69;
    }

    var classification = new SymbolClassifier().Classify(
        outcome.Session,
        profile,
        cancellation.Token);
    await outcome.Session.DisposeAsync();
    if (classification.Status != ClassificationRunStatus.Success
        || classification.ClassificationSet is null)
    {
        return 70;
    }

    Console.Write(SerializeClassification(classification.ClassificationSet));
    return 0;
}

if (mode == "policy-evidence")
{
    if (outcome.Status != RepositoryLoadStatus.Success || outcome.Session is null)
    {
        return 71;
    }

    var classification = new SymbolClassifier().ClassifySession(
        outcome.Session,
        TargetProfile.ExternalApi,
        cancellation.Token);
    var observations = new DocumentationObserver().Observe(
        classification,
        cancellation.Token);
    var policy = PolicyConfigurationEvaluator.Parse(
        """
        {"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"required"}
        """u8.ToArray(),
        cancellation.Token);
    if (policy.Status != PolicyRunStatus.Success || policy.Document is null)
    {
        await outcome.Session.DisposeAsync();
        return 72;
    }

    var extracted = new PolicyEvidenceExtractor().Extract(
        classification,
        observations,
        policy.Document,
        cancellation.Token);
    await outcome.Session.DisposeAsync();
    if (extracted.Status != PolicyEvidenceExtractionStatus.Success)
    {
        return 73;
    }

    Console.Write(SerializePolicyEvidence(extracted));
    return 0;
}

if (outcome?.Session is not null)
{
    await outcome.Session.DisposeAsync();
}

if (mode == "stdout-after-success")
{
    if (!OperatingSystem.IsWindows()
        || outcome?.Status != RepositoryLoadStatus.Success)
    {
        return 74;
    }

    var nativeBytes = Encoding.UTF8.GetBytes("native-stdout-ok\n");
    if (WriteFile(
            GetStdHandle(-11),
            nativeBytes,
            checked((uint)nativeBytes.Length),
            out var nativeWritten,
            IntPtr.Zero) == 0
        || nativeWritten != nativeBytes.Length)
    {
        return 75;
    }

    using var child = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
        Arguments = "--version",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    }) ?? throw new InvalidOperationException("The post-resolution child did not start.");
    var childOutput = await child.StandardOutput.ReadToEndAsync();
    var childError = await child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync();
    if (child.ExitCode != 0 || childError.Length != 0)
    {
        return 76;
    }

    Console.Write($"child-stdout-ok:{childOutput.Trim()}\nmanaged-stdout-ok\n");
    return 0;
}

Console.WriteLine($"{outcome?.Status}:{outcome?.PrimaryFailure?.Code}");
var expected = mode switch
{
    "success" => RepositoryLoadStatus.Success,
    "failure" => RepositoryLoadStatus.Failure,
    "cancellation" => RepositoryLoadStatus.Cancelled,
    _ => (RepositoryLoadStatus)(-1),
};
return outcome?.Status == expected ? 0 : 66;

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr GetStdHandle(int standardHandle);

[DllImport("kernel32.dll", SetLastError = true)]
static extern int WriteFile(
    IntPtr file,
    byte[] buffer,
    uint bytesToWrite,
    out uint bytesWritten,
    IntPtr overlapped);

static string SerializeClassification(ClassificationSet set)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(
        stream,
        new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        }))
    {
        writer.WriteStartObject();
        writer.WriteString(
            "targetProfile",
            ClassificationVocabulary.GetId(set.TargetProfile));
        writer.WriteStartArray("targets");
        foreach (var target in set.Targets)
        {
            writer.WriteStartObject();
            writer.WriteString("recordType", "TargetClassification");
            writer.WritePropertyName("symbolRef");
            WriteSymbolRef(writer, target.SymbolRef);
            writer.WriteString(
                "primaryKind",
                ClassificationVocabulary.GetId(target.PrimaryKind));
            writer.WriteStartArray("traits");
            foreach (var trait in target.Traits)
            {
                writer.WriteStringValue(ClassificationVocabulary.GetId(trait));
            }

            writer.WriteEndArray();
            writer.WriteString(
                "origin",
                ClassificationVocabulary.GetId(target.Origin));
            writer.WriteString(
                "supportStatus",
                ClassificationVocabulary.GetId(target.SupportStatus));
            WriteSkip(writer, target.SkipReason);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("components");
        foreach (var component in set.Components)
        {
            writer.WriteStartObject();
            writer.WriteString("recordType", "ComponentClassification");
            writer.WritePropertyName("parentSymbolRef");
            WriteSymbolRef(writer, component.ParentSymbolRef);
            writer.WriteString(
                "componentKind",
                ClassificationVocabulary.GetId(component.ComponentKind));
            writer.WriteString("identity", component.Identity);
            writer.WriteString(
                "origin",
                ClassificationVocabulary.GetId(component.Origin));
            writer.WriteString(
                "supportStatus",
                ClassificationVocabulary.GetId(component.SupportStatus));
            WriteSkip(writer, component.SkipReason);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("relations");
        foreach (var relation in set.Relations)
        {
            writer.WriteStartObject();
            writer.WriteString("recordType", "RelationObservation");
            writer.WriteString(
                "relationKind",
                ClassificationVocabulary.GetId(relation.RelationKind));
            writer.WritePropertyName("sourceSymbolRef");
            WriteSymbolRef(writer, relation.SourceSymbolRef);
            writer.WritePropertyName("targetSymbolRef");
            WriteSymbolRef(writer, relation.TargetSymbolRef);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("unresolved");
        foreach (var unresolved in set.Unresolved)
        {
            writer.WriteStartObject();
            writer.WriteString("recordType", "UnresolvedClassification");
            writer.WriteString(
                "compilationContextRef",
                unresolved.CompilationContextRef);
            writer.WriteString(
                "origin",
                ClassificationVocabulary.GetId(unresolved.Origin));
            writer.WriteString(
                "supportStatus",
                ClassificationVocabulary.GetId(unresolved.SupportStatus));
            writer.WriteString(
                "skipReason",
                ClassificationVocabulary.GetId(unresolved.SkipReason));
            writer.WritePropertyName("candidateLocator");
            WriteLocator(writer, unresolved.CandidateLocator);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(stream.ToArray());
}

static string SerializePolicyEvidence(PolicyEvidenceExtractionOutcome outcome)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(
        stream,
        new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        }))
    {
        writer.WriteStartArray();
        foreach (var binding in outcome.Bindings)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("subject");
            WriteEvidenceSubject(writer, binding.Evidence.Bundle.ObservationSubject!.Subject);
            writer.WriteStartArray("policyContributions");
            foreach (var contribution in binding.PolicyContributions.Contributions)
            {
                writer.WriteStartObject();
                writer.WriteString("projectPath", contribution.ProjectPath);
                switch (contribution)
                {
                    case RepositoryPolicyContribution repository:
                        writer.WriteString("sourcePath", repository.SourcePath);
                        break;
                    case GeneratedPolicyContribution generated:
                        writer.WriteStartObject("generatedOutput");
                        writer.WriteString(
                            "producerKind",
                            PolicyConfigurationVocabulary.GetId(
                                generated.GeneratedOutput.ProducerKind));
                        writer.WriteString(
                            "producerId",
                            generated.GeneratedOutput.ProducerId);
                        writer.WriteString(
                            "outputId",
                            generated.GeneratedOutput.OutputId);
                        writer.WriteEndObject();
                        break;
                    default:
                        throw new InvalidOperationException("Unknown policy contribution.");
                }

                writer.WriteString(
                    "expectation",
                    PolicyConfigurationVocabulary.GetId(contribution.Expectation));
                if (contribution.MatchedRuleId is not null)
                {
                    writer.WriteString("matchedRuleId", contribution.MatchedRuleId);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString(
                "observationValue",
                DocumentationObservationVocabulary.GetId(
                    binding.Evidence.ObservationValue));
            writer.WriteBoolean(
                "supportsOrdinaryResult",
                binding.Evidence.SupportsOrdinaryResult);
            writer.WriteStartArray("evidenceIds");
            foreach (var evidenceId in binding.Evidence.EvidenceIds)
            {
                writer.WriteStringValue(evidenceId);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("bundle");
            WriteBundle(writer, binding.Evidence.Bundle);
            if (binding.Evidence.Authority is { } authority)
            {
                writer.WriteStartObject("authority");
                writer.WriteString("declarationSetId", authority.DeclarationSetId);
                writer.WriteString(
                    "completeness",
                    authority.Completeness == EvidenceAuthorityCompleteness.Complete
                        ? "complete"
                        : "positive-only");
                writer.WriteStartArray("declarations");
                foreach (var declaration in authority.Declarations)
                {
                    writer.WriteStartObject();
                    writer.WriteString("declarationId", declaration.DeclarationId);
                    writer.WriteString("evidenceId", declaration.EvidenceId);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    return Encoding.UTF8.GetString(stream.ToArray());
}

static void WriteBundle(Utf8JsonWriter writer, EvidenceBundle bundle)
{
    writer.WriteStartObject();
    writer.WriteNumber("evidenceBundleVersion", bundle.EvidenceBundleVersion);
    writer.WriteString(
        "availabilityStatus",
        EvidenceVocabulary.GetId(bundle.AvailabilityStatus));
    if (bundle.OmissionReason is { } omissionReason)
    {
        writer.WriteString(
            "omissionReason",
            EvidenceVocabulary.GetId(omissionReason));
    }

    writer.WriteStartArray("items");
    foreach (var item in bundle.Items)
    {
        writer.WriteStartObject();
        writer.WriteString("evidenceId", item.EvidenceId);
        writer.WritePropertyName("subject");
        WriteEvidenceSubject(writer, item.Subject);
        writer.WriteString("kind", EvidenceVocabulary.GetId(item.Kind));
        writer.WriteString("relation", EvidenceVocabulary.GetId(item.Relation));
        writer.WriteString("excerpt", item.Excerpt);
        writer.WriteString("sha256", item.Sha256);
        writer.WriteNumber("originalUtf8ByteCount", item.OriginalUtf8ByteCount);
        writer.WriteNumber("includedUtf8ByteCount", item.IncludedUtf8ByteCount);
        writer.WriteNumber("omittedUtf8ByteCount", item.OmittedUtf8ByteCount);
        writer.WriteBoolean("isTruncated", item.IsTruncated);
        writer.WritePropertyName("locator");
        WriteEvidenceLocator(writer, item.Locator);
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    if (bundle.ObservationSubject is { } observation)
    {
        writer.WriteStartObject("observationSubject");
        writer.WriteString("observationSubjectRef", observation.ObservationSubjectRef);
        writer.WriteString("compilationContextRef", observation.CompilationContextRef);
        writer.WritePropertyName("subject");
        WriteEvidenceSubject(writer, observation.Subject);
        writer.WriteString(
            "authoritativeDeclarationSetDigest",
            observation.AuthoritativeDeclarationSetDigest);
        writer.WriteNumber(
            "authoritativeDeclarationCount",
            observation.AuthoritativeDeclarationCount);
        writer.WriteEndObject();
    }

    writer.WriteEndObject();
}

static void WriteEvidenceSubject(Utf8JsonWriter writer, EvidenceSubject subject)
{
    writer.WriteStartObject();
    if (subject is ComponentEvidenceSubject component)
    {
        writer.WritePropertyName("parentSymbolRef");
        WriteSymbolRef(writer, component.ParentSymbolRef);
        writer.WriteString(
            "componentKind",
            ClassificationVocabulary.GetId(component.ComponentKind));
        writer.WriteString("identity", component.Identity);
    }
    else
    {
        writer.WriteString(
            "compilationContextRef",
            subject.ParentSymbolRef.CompilationContextRef);
        writer.WriteString(
            "documentationCommentId",
            subject.ParentSymbolRef.DocumentationCommentId);
    }

    writer.WriteEndObject();
}

static void WriteEvidenceLocator(Utf8JsonWriter writer, EvidenceLocator locator)
{
    writer.WriteStartObject();
    switch (locator)
    {
        case RepositoryEvidenceLocator repository:
            writer.WriteStartObject("repository");
            writer.WriteString("path", repository.Path);
            WriteSpan(writer, repository.Span);
            writer.WriteEndObject();
            break;
        case MetadataEvidenceLocator metadata:
            writer.WriteStartObject("metadata");
            writer.WriteString("assemblyIdentity", metadata.AssemblyIdentity);
            writer.WriteString(
                "documentationCommentId",
                metadata.DocumentationCommentId);
            writer.WriteEndObject();
            break;
        case GeneratedOutputEvidenceLocator generated:
            writer.WriteStartObject("generatedOutput");
            writer.WriteString(
                "producerKind",
                PolicyConfigurationVocabulary.GetId(generated.ProducerKind));
            writer.WriteString("producerId", generated.ProducerId);
            writer.WriteString("outputId", generated.OutputId);
            writer.WriteString("sourceSha256", generated.SourceSha256);
            WriteSpan(writer, generated.Span);
            writer.WriteEndObject();
            break;
        case SyntheticEvidenceLocator synthetic:
            writer.WriteStartObject("synthetic");
            writer.WriteString("fixtureId", synthetic.FixtureId);
            writer.WriteEndObject();
            break;
        default:
            throw new InvalidOperationException("Unknown evidence locator.");
    }

    writer.WriteEndObject();
}

static void WriteSymbolRef(Utf8JsonWriter writer, SymbolRef symbolRef)
{
    writer.WriteStartObject();
    writer.WriteString(
        "compilationContextRef",
        symbolRef.CompilationContextRef);
    writer.WriteString(
        "documentationCommentId",
        symbolRef.DocumentationCommentId);
    writer.WriteEndObject();
}

static void WriteSkip(Utf8JsonWriter writer, SkipReason? skipReason)
{
    if (skipReason is { } value)
    {
        writer.WriteString(
            "skipReason",
            ClassificationVocabulary.GetId(value));
    }
}

static void WriteLocator(Utf8JsonWriter writer, CandidateLocator locator)
{
    writer.WriteStartObject();
    switch (locator)
    {
        case RepositoryCandidateLocator repository:
            writer.WriteStartObject("repository");
            writer.WriteString("path", repository.Path);
            WriteSpan(writer, repository.Span);
            writer.WriteEndObject();
            break;
        case GeneratedSourceCandidateLocator generated:
            writer.WriteStartObject("generatedSource");
            writer.WriteString("generatorId", generated.GeneratorId);
            writer.WriteString("hintNameId", generated.HintNameId);
            WriteSpan(writer, generated.Span);
            writer.WriteEndObject();
            break;
        case ToolGeneratedCandidateLocator generated:
            writer.WriteStartObject("toolGenerated");
            writer.WriteString("producerId", generated.ProducerId);
            writer.WriteString("outputId", generated.OutputId);
            WriteSpan(writer, generated.Span);
            writer.WriteEndObject();
            break;
        case SyntheticCandidateLocator synthetic:
            writer.WriteStartObject("synthetic");
            writer.WriteString("fixtureId", synthetic.FixtureId);
            writer.WriteEndObject();
            break;
        default:
            throw new InvalidOperationException("Unknown candidate locator.");
    }

    writer.WriteEndObject();
}

static void WriteSpan(Utf8JsonWriter writer, Utf16Span? span)
{
    if (span is not { } value)
    {
        return;
    }

    writer.WriteStartObject("span");
    writer.WriteNumber("start", value.Start);
    writer.WriteNumber("end", value.End);
    writer.WriteEndObject();
}
