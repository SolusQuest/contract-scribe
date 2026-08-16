#nullable enable
namespace SemanticFixture;

public interface IRunner
{
    string Execute(string value);
}

public abstract class RunnerBase
{
    public virtual string Execute(string value) => value;
}

public partial class Runner : RunnerBase, IRunner
{
    /// <summary>Returns the supplied value.</summary>
    /// <param name="value">The value to return.</param>
    /// <returns>The supplied value.</returns>
    public override string Execute(string value) => value;

    public string Execute(int value) => value.ToString();

    string IRunner.Execute(string value) => Execute(value);
}
