namespace ContractScribe.HostValidation;

public sealed class ProtocolException : Exception
{
    public ProtocolException(string code) : base(code)
    {
        Code = code;
    }

    public ProtocolException(string code, Exception innerException) : base(code, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
