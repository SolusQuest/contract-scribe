using System.Reflection;

[assembly: AssemblyVersion("2.0.0.0")]

namespace SemanticFixture;

public sealed class Runner
{
    public string Execute(string value) => value;
}

public sealed class DecoyConsumer
{
    public string Invoke() => new Runner().Execute("same-simple-name-decoy");
}
