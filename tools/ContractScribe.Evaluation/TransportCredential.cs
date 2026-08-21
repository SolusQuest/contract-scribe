namespace ContractScribe.Evaluation;

internal sealed class TransportCredential
{
    private string? value;

    private TransportCredential(string value) => this.value = value;

    internal static bool TryCapture(string environmentVariable, out TransportCredential? credential)
    {
        credential = null;
        var captured = Environment.GetEnvironmentVariable(environmentVariable);
        Environment.SetEnvironmentVariable(environmentVariable, null);
        if (string.IsNullOrEmpty(captured))
        {
            return false;
        }

        credential = new TransportCredential(captured);
        return true;
    }

    internal string Take()
    {
        var captured = Interlocked.Exchange(ref value, null);
        return captured ?? throw new InvalidOperationException("evaluation.credential.consumed");
    }

    internal SensitiveMarker CreateMarker()
    {
        var captured = value ?? throw new InvalidOperationException("evaluation.credential.consumed");
        return SensitiveMarker.Create(captured);
    }

    public override string ToString() => nameof(TransportCredential);
}
