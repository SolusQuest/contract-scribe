# Distribution

No distribution channel is selected during bootstrap. Candidate outcomes include a framework-dependent CLI, a Native AOT CLI, a framework-dependent loader with an AOT semantic core, a loader child process, or .NET-tool-first distribution.

The M0 experiments must exercise the same semantic path, not merely `--version`. The loader/distribution ADR will choose a baseline using observed Roslyn, SDK-resolution, trimming/reflection, MSBuild-host, and AOT evidence.

ADR 0001 selected the framework-dependent semantic execution baseline, and [ADR 0002](decisions/0002-process-topology.md) selected an in-process production process topology for M1: the future channel must carry a single-binary, framework-dependent host with no worker co-location requirement. The channel decision itself remains with issue #18.
