#nullable enable
using RunnerAlias = SemanticFixture.Runner;

namespace SemanticConsumer;

public sealed class Consumer
{
    public string Invoke()
    {
        var runner = new RunnerAlias();
        SemanticFixture.IRunner contract = runner;
        _ = contract.Execute("explicit-interface");
        var traced = new RunnerAlias();
        string? label = "trace";
        runner.Trace(ref traced, in label);
        var name = nameof(RunnerAlias.Execute);
        _ = name;
        var inheritedName = nameof(RunnerAlias.Inherited);
        _ = inheritedName;
        _ = runner.Execute(42);
        _ = runner.Execute("consumer");
        _ = runner.Inherited("inherited-interface");
        return runner.Expand("extension");
    }
}

public sealed class UserDefinedNameofConsumer
{
    private static string @nameof(string value) => value;

    public string Invoke()
    {
        var runner = new RunnerAlias();
        _ = @nameof(runner.Execute("user-defined-nameof"));
        return @nameof(runner.Inherited("user-defined-nameof-inherited"));
    }
}
