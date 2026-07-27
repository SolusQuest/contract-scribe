using ContractScribe.Roslyn;

if (args.Length < 3)
{
    return 64;
}

var repositoryRoot = args[0];
var inputPath = args[1];
var mode = args[2];
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
