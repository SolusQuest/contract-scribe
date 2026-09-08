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
        InitializeGitHub();
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
                var temporaryPath = acknowledgementPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(
                               temporaryPath,
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.None,
                               bufferSize: 1,
                               FileOptions.WriteThrough))
                    {
                        stream.Write(System.Text.Encoding.UTF8.GetBytes(reached + "\n"));
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(temporaryPath, acknowledgementPath);
                }
                catch
                {
                    File.Delete(temporaryPath);
                    throw;
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
    private static void InitializeGitHub()
    {
        // Child MSBuild/test hosts inherit environment but must never register a CLI transport.
        var command = Environment.GetCommandLineArgs().FirstOrDefault();
        if (command is null || Path.GetFileNameWithoutExtension(command) != CliAssemblyName) return;
        var endpoint = Environment.GetEnvironmentVariable("CONTRACTSCRIBE_TEST_GITHUB_ENDPOINT");
        var observations = Environment.GetEnvironmentVariable("CONTRACTSCRIBE_TEST_GITHUB_OBSERVATIONS");
        var fault = Environment.GetEnvironmentVariable("CONTRACTSCRIBE_TEST_GITHUB_FAULT");
        var pause = Environment.GetEnvironmentVariable("CONTRACTSCRIBE_TEST_GITHUB_PAUSE");
        var release = Environment.GetEnvironmentVariable("CONTRACTSCRIBE_TEST_GITHUB_RELEASE");
        var terminal = Environment.GetEnvironmentVariable("CONTRACTSCRIBE_TEST_GITHUB_TERMINAL");
        if (endpoint is null && observations is null && fault is null && terminal is null) return;
        var cli = Assembly.Load(CliAssemblyName);
        if (endpoint is not null)
        {
            var adapter = Assembly.Load("ContractScribe.GitHub");
            var hook = adapter.GetType("ContractScribe.GitHub.Transport.GitHubTransportTestHook", true)!;
            var milliseconds = Environment.GetEnvironmentVariable("CONTRACTSCRIBE_TEST_GITHUB_TIMEOUT") is { } value
                ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : 30000;
            _ = hook.GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [new Uri(endpoint), null, milliseconds]);
        }
        var boundaries = cli.GetType("ContractScribe.Cli.GitHubProposalProcessHooks", true)!;
        if (terminal is not null)
            boundaries.GetMethod("RegisterTerminal", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [terminal, (long?)12]);
        Action<string> callback = name =>
        {
            if (observations is not null) File.AppendAllText(observations, name + "\n");
            if (fault == name) throw new InvalidOperationException("synthetic private fault sentinel");
            if (pause == name && (release is null || !SpinWait.SpinUntil(() => File.Exists(release), TimeSpan.FromSeconds(45))))
                throw new TimeoutException("Synthetic physical-output pause expired.");
        };
        boundaries.GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [callback]);
    }
}
