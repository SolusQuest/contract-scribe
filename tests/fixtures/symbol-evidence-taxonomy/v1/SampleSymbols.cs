namespace TaxonomyFixtures;

public interface IContract
{
    void Execute();
}

public interface IStaticContract
{
    static abstract int StaticMember();
}

public partial record SampleRecord(string Name) : IContract, IStaticContract
{
    public required string Required { get; init; }
    public int this[int index] => index;
    public static int StaticMember() => 0;
    public void Execute() { }
    void IContract.Execute() { }
}

public class Base
{
    protected virtual void ProtectedMember() { }
}

public sealed class SealedBase
{
    protected void NotReachable() { }
}

public delegate int SampleDelegate(int value);
