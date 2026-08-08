using System.Text;
using ContractScribe.Cli;

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var output = new StreamWriter(StableStandardStream.OpenOutput(), utf8)
{
    AutoFlush = true,
    NewLine = "\n",
};
using var error = new StreamWriter(Console.OpenStandardError(), utf8)
{
    AutoFlush = true,
    NewLine = "\n",
};

return await CommandLineApplication.ExecuteProcessAsync(args, output, error);
