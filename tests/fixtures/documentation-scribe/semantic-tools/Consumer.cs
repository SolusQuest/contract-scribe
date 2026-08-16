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
        _ = runner.Execute(42);
        _ = runner.Execute("consumer");
        return runner.Expand("extension");
    }
}
