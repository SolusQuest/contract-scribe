using ContractScribe.Roslyn;

const string usage = "Usage: ContractScribe.Roslyn.Experiment <solution-path> [output-directory]";

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine(usage);
    return 2;
}

var outputDirectory = string.Empty;
var resultPath = string.Empty;
var payloadPath = string.Empty;
try
{
    var solutionPath = Path.GetFullPath(args[0]);
    outputDirectory = args.Length == 2
        ? Path.GetFullPath(args[1])
        : Path.Combine(Environment.CurrentDirectory, "m0.4-output");
    resultPath = Path.Combine(outputDirectory, "result.json");
    payloadPath = Path.Combine(outputDirectory, "semantic-payload.json");

    Directory.CreateDirectory(outputDirectory);
    DeleteKnownArtifact(resultPath);
    DeleteKnownArtifact(payloadPath);

    var execution = await new FrameworkDependentExperiment().RunAsync(solutionPath);
    var envelope = ExperimentResultSerializer.Serialize(execution.Result);
    await File.WriteAllBytesAsync(resultPath, envelope);

    if (execution.SemanticPayloadBytes is not null)
    {
        await File.WriteAllBytesAsync(payloadPath, execution.SemanticPayloadBytes);
    }

    Console.Out.WriteLine(SemanticPayloadSerializer.DecodeUtf8(envelope));
    return execution.Result.ExitCode;
}
catch (Exception)
{
    try
    {
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            DeleteKnownArtifact(Path.Combine(outputDirectory, "semantic-payload.json"));
            var result = ExperimentResultSerializer.Serialize(
                ExperimentResult.Failure(ExperimentStatus.InternalError, null, null));
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "result.json"), result);
            Console.Out.WriteLine(SemanticPayloadSerializer.DecodeUtf8(result));
        }
    }
    catch (Exception)
    {
        // Keep unexpected host failures bounded and free of exception details and paths.
    }

    Console.Error.WriteLine("The experiment host failed unexpectedly.");
    return 3;
}

static void DeleteKnownArtifact(string path)
{
    if (File.Exists(path))
    {
        File.Delete(path);
    }
}
