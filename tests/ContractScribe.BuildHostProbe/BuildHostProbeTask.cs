using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using Microsoft.Build.Framework;

namespace ContractScribe.BuildHostProbe;

public sealed class BuildHostProbeTask : ITask
{
    private const byte ProtocolVersion = 1;
    private const byte Ready = 1;
    private const byte Release = 2;
    private const byte Completed = 3;

    [Required]
    public string PipeName { get; set; } = string.Empty;

    [Required]
    public string Token { get; set; } = string.Empty;

    public IBuildEngine BuildEngine { get; set; } = null!;

    public ITaskHost HostObject { get; set; } = null!;

    public bool Execute()
    {
        if (!Guid.TryParseExact(Token, "N", out var token)
            || string.IsNullOrWhiteSpace(PipeName))
        {
            return true;
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.None);
            pipe.Connect(30_000);
            using var writer = new BinaryWriter(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
            using var reader = new BinaryReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(ProtocolVersion);
            writer.Write(Ready);
            writer.Write(token.ToByteArray());
            writer.Write(process.Id);
            writer.Write(process.StartTime.ToUniversalTime().Ticks);
            writer.Flush();

            using var releaseDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var release = new byte[1];
            var read = pipe.ReadAsync(release, 0, release.Length, releaseDeadline.Token)
                .GetAwaiter()
                .GetResult();
            if (read != 1 || release[0] != Release)
            {
                return true;
            }

            writer.Write(ProtocolVersion);
            writer.Write(Completed);
            writer.Flush();
        }
        catch (Exception exception) when (exception is
            IOException
            or TimeoutException
            or OperationCanceledException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
        }

        return true;
    }
}
