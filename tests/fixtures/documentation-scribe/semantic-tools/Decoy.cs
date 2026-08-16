namespace SemanticFixture;

public sealed class Runner
{
    public string Execute(string value) => value;
}

public sealed class DecoyConsumer
{
    public string Invoke() => new Runner().Execute("same-simple-name-decoy");
}
