using System.Text;
using ContractScribe.Cli;

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
    using var output = new StreamWriter(Console.OpenStandardOutput(), utf8)
    {
        AutoFlush = true,
        NewLine = "\n",
    };
    using var error = new StreamWriter(Console.OpenStandardError(), utf8)
    {
        AutoFlush = true,
        NewLine = "\n",
    };
    await error.WriteAsync(bufferedError.ToString());
    await output.WriteAsync(bufferedOutput.ToString());

    return exitCode;
}
finally
{
    retainedSignals?.Dispose();
}
