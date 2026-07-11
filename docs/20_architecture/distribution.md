# Distribution

No distribution channel is selected during bootstrap. Candidate outcomes include a framework-dependent CLI, a Native AOT CLI, a framework-dependent loader with an AOT semantic core, a loader child process, or .NET-tool-first distribution.

The M0 experiments must exercise the same semantic path, not merely `--version`. The loader/distribution ADR will choose a baseline using observed Roslyn, SDK-resolution, trimming/reflection, MSBuild-host, and AOT evidence.
