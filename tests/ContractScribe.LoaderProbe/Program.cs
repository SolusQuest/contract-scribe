using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.Roslyn;

if (args.Length < 3)
{
    return 64;
}

var repositoryRoot = args[0];
var inputPath = args[1];
var mode = args[2];
if (mode == "classification")
{
    if (args.Length != 6)
    {
        return 64;
    }

    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(args[4]);
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(args[4]);
    Environment.SetEnvironmentVariable("TZ", args[5]);
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
if (mode == "churn")
{
    if (args.Length != 5 || outcome.Status != RepositoryLoadStatus.Success || outcome.Session is null)
    {
        return 65;
    }

    await outcome.Session.DisposeAsync();
    outcome = null!;
    await File.WriteAllTextAsync(args[3], "ready");
    while (!File.Exists(args[4]))
    {
        outcome = await loader.LoadAsync(
            new RepositoryLoadRequest(repositoryRoot, inputPath));
        if (outcome.Status != RepositoryLoadStatus.Success || outcome.Session is null)
        {
            return 67;
        }

        await outcome.Session.DisposeAsync();
    }
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

if (outcome?.Session is not null)
{
    await outcome.Session.DisposeAsync();
}

Console.WriteLine($"{outcome?.Status}:{outcome?.PrimaryFailure?.Code}");
var expected = mode switch
{
    "success" or "churn" => RepositoryLoadStatus.Success,
    "failure" => RepositoryLoadStatus.Failure,
    "cancellation" => RepositoryLoadStatus.Cancelled,
    _ => (RepositoryLoadStatus)(-1),
};
return mode == "churn" || outcome?.Status == expected ? 0 : 66;

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
