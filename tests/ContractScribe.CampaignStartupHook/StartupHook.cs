using System.Reflection;

internal static class StartupHook
{
    private const string HookNameVariable = "CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_NAME";
    private const string AcknowledgementVariable = "CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_ACK";
    private const string ReleaseVariable = "CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_RELEASE";
    private const string CliAssemblyName = "ContractScribe.Cli";
    private static readonly object Gate = new();
    private static IDisposable? registration;

    public static void Initialize()
    {
        var hookName = Environment.GetEnvironmentVariable(HookNameVariable);
        var acknowledgementPath = Environment.GetEnvironmentVariable(AcknowledgementVariable);
        var releasePath = Environment.GetEnvironmentVariable(ReleaseVariable);
        if (string.IsNullOrEmpty(hookName) || string.IsNullOrEmpty(acknowledgementPath))
        {
            return;
        }

        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
            string.Equals(assembly.GetName().Name, CliAssemblyName, StringComparison.Ordinal));
        if (loaded is not null)
        {
            Register(loaded, hookName, acknowledgementPath, releasePath);
        }
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (!string.Equals(args.LoadedAssembly.GetName().Name, CliAssemblyName, StringComparison.Ordinal))
        {
            return;
        }
        var hookName = Environment.GetEnvironmentVariable(HookNameVariable);
        var acknowledgementPath = Environment.GetEnvironmentVariable(AcknowledgementVariable);
        var releasePath = Environment.GetEnvironmentVariable(ReleaseVariable);
        if (!string.IsNullOrEmpty(hookName) && !string.IsNullOrEmpty(acknowledgementPath))
        {
            Register(args.LoadedAssembly, hookName, acknowledgementPath, releasePath);
        }
    }

    private static void Register(
        Assembly assembly,
        string selectedName,
        string acknowledgementPath,
        string? releasePath)
    {
        lock (Gate)
        {
            if (registration is not null)
            {
                return;
            }
            var type = assembly.GetType("ContractScribe.Cli.CampaignProcessBoundaryHooks", throwOnError: true)!;
            var allowlist = (IEnumerable<string>)type.GetProperty(
                "Allowlist", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            if (!allowlist.Contains(selectedName, StringComparer.Ordinal))
            {
                return;
            }
            var register = type.GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!;
            Action<string> observer = reached =>
            {
                if (!string.Equals(reached, selectedName, StringComparison.Ordinal))
                {
                    return;
                }
                using (var stream = new FileStream(
                           acknowledgementPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.Read,
                           bufferSize: 1,
                           FileOptions.WriteThrough))
                {
                    stream.Write(System.Text.Encoding.UTF8.GetBytes(reached + "\n"));
                    stream.Flush(flushToDisk: true);
                }
                if (string.IsNullOrEmpty(releasePath))
                {
                    Thread.Sleep(Timeout.Infinite);
                }
                while (!File.Exists(releasePath))
                {
                    Thread.Sleep(10);
                }
            };
            registration = (IDisposable)register.Invoke(null, [observer])!;
        }
    }
}
