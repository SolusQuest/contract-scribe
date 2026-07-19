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
$fixtureRoot = Join-Path $repositoryRoot "tests\fixtures\roslyn-msbuild\v1"
$manifest = Get-Content -LiteralPath (Join-Path $fixtureRoot "m0.5-native-aot-manifest.json") -Raw | ConvertFrom-Json
$registry = Get-Content -LiteralPath (Join-Path $repositoryRoot $manifest.registryPath) -Raw | ConvertFrom-Json
$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $fixtureRoot "evidence")).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

function Fail-Aggregate([string]$message) {
    Write-Output "M0.5 aggregate failure: $message"
    exit 1
}

if (-not $OutputPath.StartsWith($evidenceRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($OutputPath) -ne "m0.5-summary-v1.json") {
    Fail-Aggregate "the summary path is outside the controlled evidence directory."
}

function Write-CanonicalJson([string]$path, [object]$value) {
    $directory = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 20 -Compress), [Text.UTF8Encoding]::new($false))
}

function Read-Evidence([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { Fail-Aggregate "evidence artifact missing." }
    try { return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    catch { Fail-Aggregate "evidence artifact malformed." }
}

function Validate-Cell([object]$cell) {
    if ($cell.recordType -ne "cell") { Fail-Aggregate "a protocol-failure artifact cannot be aggregated." }
    $expectedOs = if ($cell.cell.rid -eq "linux-x64") { "Ubuntu" } elseif ($cell.cell.rid -eq "win-x64") { "Windows" } else { "" }
    if ([string]::IsNullOrWhiteSpace($expectedOs) -or $cell.cell.runnerOs -ne $expectedOs -or $cell.cell.processArchitecture -ne "X64") { Fail-Aggregate "cell matrix identity is inconsistent." }
    if ($cell.profile.runtimeIdentifier -ne $cell.cell.rid -or $cell.profile.targetFramework -ne "net10.0" -or $cell.profile.configuration -ne "Release" -or $cell.profile.publishAot -ne $true -or $cell.profile.selfContained -ne $true -or $cell.profile.publishTrimmed -ne $true) { Fail-Aggregate "cell publish profile is inconsistent." }
    if ($cell.outcome -notin @("feasible-clean", "feasible-with-warnings", "not-feasible", "inconclusive")) { Fail-Aggregate "cell outcome is not closed." }
    if ($null -ne $cell.code) {
        if ($registry.cellCodes.PSObject.Properties.Name -notcontains [string]$cell.code) { Fail-Aggregate "cell code is not in the closed registry." }
        $definition = $registry.cellCodes.($cell.code)
        if ($definition.phase -ne $cell.phase -or $definition.allowedCauses -notcontains $cell.cause -or $definition.allowedOutcomes -notcontains $cell.outcome) { Fail-Aggregate "cell phase, cause, and outcome do not match the registry." }
    }
    elseif ($cell.outcome -notin @("feasible-clean", "feasible-with-warnings")) {
        Fail-Aggregate "a negative or inconclusive cell is missing its stable code."
    }
    if ($cell.cause -eq "unknown" -and $cell.outcome -ne "inconclusive") { Fail-Aggregate "unknown cause cannot be conclusive." }
    if ($cell.outcome -eq "feasible-clean" -and @($cell.warnings).Count -ne 0) { Fail-Aggregate "feasible-clean cannot contain warnings." }
    if ($cell.outcome -eq "feasible-with-warnings" -and @($cell.warnings).Count -eq 0) { Fail-Aggregate "feasible-with-warnings requires a warning observation." }
    if ($cell.comparison.status -eq "compared") {
        if ([string]::IsNullOrWhiteSpace($cell.comparison.frameworkPayloadSha256) -or [string]::IsNullOrWhiteSpace($cell.comparison.aotPayloadSha256) -or $null -eq $cell.comparison.repeatedAotPayloadByteEqual -or $null -eq $cell.comparison.frameworkByteEqual) { Fail-Aggregate "a compared cell is missing payload comparison facts." }
    }
    elseif ($cell.comparison.status -ne "not-run" -or $null -ne $cell.comparison.aotPayloadSha256 -or $null -ne $cell.comparison.repeatedAotPayloadByteEqual -or $null -ne $cell.comparison.frameworkByteEqual) {
        Fail-Aggregate "the cell comparison object is contradictory."
    }
}

$linux = Read-Evidence $LinuxEvidencePath
$windows = Read-Evidence $WindowsEvidencePath
Validate-Cell $linux
Validate-Cell $windows

$cells = @($linux, $windows) | Sort-Object { $_.cell.rid }
$rids = @($cells | ForEach-Object { $_.cell.rid })
if ((($rids | Sort-Object) -join "`n") -cne "linux-x64`nwin-x64") { Fail-Aggregate "the aggregate does not contain exactly the required matrix." }
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
        $summaryCell = [ordered]@{
            runnerOs = $_.cell.runnerOs
            rid = $_.cell.rid
            processArchitecture = $_.cell.processArchitecture
            outcome = $_.outcome
            phase = $_.phase
            cause = $_.cause
        }
        if ($null -ne $_.code) { $summaryCell.code = $_.code }
        $summaryCell
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
