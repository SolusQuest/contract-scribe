[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("linux-x64", "win-x64")]
    [string]$RuntimeIdentifier,
    [ValidateSet("Release")]
    [string]$Configuration = "Release",
    [string]$EvidencePath,
    [switch]$EvidenceReproduction
)

[Console]::Error.WriteLine('M0.5-TOMBSTONE: verify-m0.5.ps1 is retired as a current-tree verifier. The M0.5 Native AOT evidence is historical-only, pinned to commit 63fd9a0ab5ff33ae20d8f7b9e66714a96feea39e. Inspect the manifest and evidence at that commit; see docs/20_architecture/experiments/m0.5-native-aot-feasibility.md. No validation was performed.')
exit 1
