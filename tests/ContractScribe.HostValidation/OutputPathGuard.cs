namespace ContractScribe.HostValidation;

public static class OutputPathGuard
{
    public static void Validate(
        BundleContext context,
        IEnumerable<string> inputPaths,
        params string[] outputPaths)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var inputs = inputPaths
            .Append(RepositoryPaths.ResolveConfined(context.Root, BundleValidator.ProtocolRelativePath))
            .Append(RepositoryPaths.ResolveConfined(context.Root, BundleValidator.VectorsRelativePath))
            .Append(RepositoryPaths.ResolveConfined(context.Root, BundleValidator.LockRelativePath))
            .Concat(context.Lock.Entries.Select(entry =>
                RepositoryPaths.ResolveConfined(context.Root, entry.Path)))
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToArray();
        var outputs = outputPaths.Select(Path.GetFullPath).ToArray();
        if (outputs.Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal).Count() != outputs.Length)
        {
            throw new ProtocolException("HV194_OUTPUT_PATH_COLLISION");
        }

        var repositoryRoot = Path.GetFullPath(context.Root);
        var allowedRepositoryOutputRoot = Path.Join(
            repositoryRoot,
            "TestResults",
            "m1-host-validation") + Path.DirectorySeparatorChar;
        foreach (var output in outputs)
        {
            if (IsWithin(output, repositoryRoot, comparison)
                && !output.StartsWith(allowedRepositoryOutputRoot, comparison))
            {
                throw new ProtocolException("HV204_OUTPUT_PROTECTED");
            }
            if (inputs.Any(input => Overlaps(input, output, comparison)))
            {
                throw new ProtocolException("HV194_OUTPUT_PATH_COLLISION");
            }

            RejectLinkAlias(output);
            var parent = Directory.GetParent(output);
            while (parent is not null)
            {
                if (parent.LinkTarget is not null
                    || (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ProtocolException("HV205_OUTPUT_LINK_ALIAS");
                }
                parent = parent.Parent;
            }
        }
    }

    private static bool Overlaps(string left, string right, StringComparison comparison) =>
        left.Equals(right, comparison)
        || IsWithin(left, right, comparison)
        || IsWithin(right, left, comparison);

    private static bool IsWithin(string path, string directory, StringComparison comparison)
    {
        var normalizedDirectory = directory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedDirectory, comparison);
    }

    private static void RejectLinkAlias(string output)
    {
        if (!File.Exists(output))
        {
            return;
        }
        var info = new FileInfo(output);
        if (info.LinkTarget is not null || HasMultipleLinks(output))
        {
            throw new ProtocolException("HV205_OUTPUT_LINK_ALIAS");
        }
    }

    private static bool HasMultipleLinks(string path)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("fsutil")
            : new System.Diagnostics.ProcessStartInfo("stat");
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("hardlink");
            startInfo.ArgumentList.Add("list");
            startInfo.ArgumentList.Add(path);
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("%h");
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(path);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new ProtocolException("HV205_OUTPUT_LINK_ALIAS");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || error.Length > 4096 || output.Length > 16 * 1024)
        {
            throw new ProtocolException("HV205_OUTPUT_LINK_ALIAS");
        }
        if (OperatingSystem.IsWindows())
        {
            return output.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Length > 1;
        }
        return !int.TryParse(output.Trim(), out var linkCount) || linkCount > 1;
    }
}
