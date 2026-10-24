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
        var deadlineBoundary = fault switch
        {
            "deadline-after-terminal" => "after-terminal",
            "deadline-before-publication" => "before-publication",
            "deadline-after-publication-recovery" => "after-publication-recovery",
            _ => null,
        };
        var deadlineClock = deadlineBoundary is null ? null : new FinalizationClock();
        if (deadlineClock is not null)
            boundaries.GetMethod("RegisterFinalizationClock", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [deadlineClock]);
        else if (observations is not null)
            boundaries.GetMethod("RegisterFinalizationClock", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [new ObservedFinalizationClock(observations)]);
        if (terminal is not null)
            boundaries.GetMethod("RegisterTerminal", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [terminal, (long?)12]);
        Action<string> callback = name =>
        {
            if (observations is not null) File.AppendAllText(observations, name + "\n");
            if (name == deadlineBoundary)
            {
                deadlineClock!.Expire();
                if (observations is not null) File.AppendAllText(observations, "finalization-deadline-expired\n");
            }
            if (fault == name) throw new InvalidOperationException("synthetic private fault sentinel");
            if (pause == name && (release is null || !SpinWait.SpinUntil(() => File.Exists(release), TimeSpan.FromSeconds(45))))
                throw new TimeoutException("Synthetic physical-output pause expired.");
        };
        boundaries.GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [callback]);
    }

    // Observe the original real timer; do not replace its elapsed-time source,
    // due time or cancellation. Fault scenarios above keep their existing clock.
    private sealed class ObservedFinalizationClock(string path) : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != TimeSpan.FromMinutes(5)) throw new InvalidOperationException("Unexpected finalization timer.");
            File.AppendAllText(path, "finalization-start:"
                + global::System.Diagnostics.Stopwatch.GetTimestamp().ToString(global::System.Globalization.CultureInfo.InvariantCulture) + "\n");
            return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
        }
    }

    private sealed class FinalizationClock : TimeProvider
    {
        private Action? expire;
        internal void Expire() => (expire ?? throw new InvalidOperationException("Finalization timer was not started."))();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (expire is not null || dueTime != TimeSpan.FromMinutes(5))
                throw new InvalidOperationException("Unexpected finalization timer.");
            var timer = new FinalizationTimer(callback, state);
            expire = timer.Fire;
            return timer;
        }
        private sealed class FinalizationTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            internal void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
