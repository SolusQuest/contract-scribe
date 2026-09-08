using System.Text;
using ContractScribe.Cli;

using var standardStreams = StandardStreamIsolation.Install();
using var bufferedOutput = new StringWriter();
using var bufferedError = new StringWriter();
IDisposable? retainedSignals = null;
var exitCode = await CommandLineApplication.ExecuteProcessAsync(
    args,
    bufferedOutput,
    bufferedError,
    registration => retainedSignals = registration);

try
{
    var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    using var output = new StreamWriter(standardStreams.OpenPresentationOutput(), utf8)
    {
        AutoFlush = true,
        NewLine = "\n",
    };
    using var error = new StreamWriter(standardStreams.OpenPresentationError(), utf8)
    {
        AutoFlush = true,
        NewLine = "\n",
    };
    await error.WriteAsync(bufferedError.ToString());
    await output.WriteAsync(bufferedOutput.ToString());

    return exitCode;
}
catch (Exception exception) when (args.FirstOrDefault() == "github-proposal"
    && exception is not (OutOfMemoryException or StackOverflowException))
{
    // Physical stream failure cannot retract emitted bytes. Do not append a second envelope
    // or expose an unhandled exception; preserve the already selected terminal exit.
    return exitCode;
}
finally
{
    if (exitCode == 6 || args.FirstOrDefault() == "github-proposal")
    {
        // A repeated control event may already be queued after the first handled
        // signal. Keep the handler rooted until process termination so that the
        // selected cancellation class cannot be escalated by that late delivery.
        GC.KeepAlive(retainedSignals);
    }
    else
    {
        retainedSignals?.Dispose();
    }
}
