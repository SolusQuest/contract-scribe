#nullable enable
namespace SemanticFixture;

public partial class Runner
{
    /// <summary>Records a value.</summary>
    public partial void Trace<T>(ref T value, in string? label)
        where T : class, new();

    public partial void Trace<T>(ref T value, in string? label)
        where T : class, new()
    {
    }

    public void Deep(string[][][][][][][][][][][][][][][][][][] value)
    {
    }
}

public static class RunnerExtensions
{
    public static string Expand(this Runner runner, string value) => runner.Execute(value);
}
