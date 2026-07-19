[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$LinuxEvidencePath,
    [Parameter(Mandatory = $true)]
    [string]$WindowsEvidencePath,
    [string]$OutputPath = "tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-summary-v1.json"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$OutputPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))

function Write-CanonicalJson([string]$path, [object]$value) {
    $directory = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 20 -Compress), [Text.UTF8Encoding]::new($false))
}

function Read-Evidence([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { Write-Output "M0.5 aggregate failure: evidence artifact missing."; exit 1 }
    try { return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    catch { Write-Output "M0.5 aggregate failure: evidence artifact malformed."; exit 1 }
}

$linux = Read-Evidence $LinuxEvidencePath
$windows = Read-Evidence $WindowsEvidencePath
if ($linux.recordType -ne "cell" -or $windows.recordType -ne "cell") {
    Write-Output "M0.5 aggregate failure: a protocol-failure artifact cannot be aggregated."
    exit 1
}

$cells = @($linux, $windows) | Sort-Object { $_.cell.rid }
$outcomes = @($cells | ForEach-Object { $_.outcome })
if ($outcomes -contains "inconclusive") {
    $aggregateOutcome = "inconclusive"
}
elseif (($outcomes -contains "not-feasible") -and ($outcomes | Where-Object { $_ -like "feasible-*" }).Count -gt 0) {
    $aggregateOutcome = "mixed"
}
elseif (($outcomes | Where-Object { $_ -eq "not-feasible" }).Count -eq 2) {
    $aggregateOutcome = "not-feasible"
}
elseif ($outcomes -contains "feasible-with-warnings") {
    $aggregateOutcome = "feasible-with-warnings"
}
else {
    $aggregateOutcome = "feasible-clean"
}

$summary = [ordered]@{
    evidenceVersion = "m0.5-native-aot-summary-v1"
    recordType = "aggregate"
    outcome = $aggregateOutcome
    exitCode = if ($aggregateOutcome -eq "inconclusive") { 1 } else { 0 }
    cells = @($cells | ForEach-Object {
        [ordered]@{
            runnerOs = $_.cell.runnerOs
            rid = $_.cell.rid
            processArchitecture = $_.cell.processArchitecture
            outcome = $_.outcome
            phase = $_.phase
            cause = $_.cause
            code = $_.code
        }
    })
    warnings = @($cells | ForEach-Object { $_.warnings } | Sort-Object phase, cause, code -Unique)
}

Write-CanonicalJson $OutputPath $summary
$content = [IO.File]::ReadAllText($OutputPath)
if ($content -match "(?i)([A-Z]:\\|/home/|/Users/|authorization|bearer\s|access[_-]?token|api[_-]?key|username|hostname|environment)") {
    Write-Output "M0.5 aggregate failure: public-safety scan failed."
    exit 1
}

Write-Output "M0.5 aggregate outcome: $aggregateOutcome"
exit $summary.exitCode
