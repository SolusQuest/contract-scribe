using ContractScribe.Roslyn;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: ContractScribe.Roslyn.Experiment <solution-path> [output-directory]");
    return 2;
}

var solutionPath = Path.GetFullPath(args[0]);
var outputDirectory = args.Length == 2
    ? Path.GetFullPath(args[1])
    : Path.Combine(Environment.CurrentDirectory, "m0.4-output");

var execution = await new FrameworkDependentExperiment().RunAsync(solutionPath);
byte[] envelope;
try
{
    envelope = ExperimentResultSerializer.Serialize(execution.Result);
}
catch
{
    Console.Error.WriteLine("The experiment result envelope could not be serialized.");
    return 3;
}

Directory.CreateDirectory(outputDirectory);
await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "result.json"), envelope);

if (execution.SemanticPayloadBytes is not null)
{
    await File.WriteAllBytesAsync(
        Path.Combine(outputDirectory, "semantic-payload.json"),
        execution.SemanticPayloadBytes);
}

Console.Out.WriteLine(SemanticPayloadSerializer.DecodeUtf8(envelope));
return execution.Result.ExitCode;
