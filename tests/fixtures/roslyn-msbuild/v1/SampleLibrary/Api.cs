namespace SampleLibrary.Api;

public class RootType
{
    public int Value { get; set; }

    public class NestedType
    {
    }

    public void Process(int value)
    {
        Value = value;
    }

    public void Process(string value)
    {
        Value = value.Length;
    }

    protected void ProtectedMember()
    {
    }

    internal void InternalMember()
    {
    }
}

internal class InternalType
{
    public void HiddenMember()
    {
    }
}
