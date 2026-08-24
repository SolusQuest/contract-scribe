using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ContractScribe.Core;

internal sealed class CampaignPlanningCommitmentWriter : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool completed;

    public CampaignPlanningCommitmentWriter(string domain)
    {
        Add("domain", domain);
    }

    public void Add(string label, string value)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(value);
        Append(StrictUtf8.GetBytes(label));
        Append(StrictUtf8.GetBytes(value));
    }

    public void AddOptional(string label, string? value)
    {
        Add(label + ".present", value is null ? "0" : "1");
        if (value is not null)
        {
            Add(label, value);
        }
    }

    public void Add(string label, bool value) => Add(label, value ? "1" : "0");

    public void Add(string label, int value) => AddInt64(label, value);

    public void Add(string label, long value) => AddInt64(label, value);

    public string Complete()
    {
        ObjectDisposedException.ThrowIf(completed, this);
        completed = true;
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public void Dispose()
    {
        hash.Dispose();
        completed = true;
    }

    private void AddInt64(string label, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        Append(StrictUtf8.GetBytes(label));
        Append(bytes);
    }

    private void Append(ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
