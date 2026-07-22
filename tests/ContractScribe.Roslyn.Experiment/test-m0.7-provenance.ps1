[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaselineRepositoryPath,
    [Parameter(Mandatory = $true)]
    [string]$FixtureRepositoryPath,
    [Parameter(Mandatory = $true)]
    [string]$BaselineCommit,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$target = Join-Path $repositoryRoot "TestResults\m0.7-provenance-tamper"
if (Test-Path -LiteralPath $target) {
    Remove-Item -LiteralPath $target -Recurse -Force
}
New-Item -ItemType Directory -Path $target | Out-Null
Copy-Item -Path (Join-Path (Resolve-Path $FixtureRepositoryPath).Path "*") -Destination $target -Recurse -Force
$oracle = Join-Path $target "expected-payload.json"
[IO.File]::AppendAllText($oracle, " ")

$output = & pwsh -NoProfile -File (Join-Path $PSScriptRoot "verify-m0.7.ps1") `
    -Configuration $Configuration `
    -BaselineRepositoryPath $BaselineRepositoryPath `
    -FixtureRepositoryPath $target `
    -BaselineCommit $BaselineCommit 2>&1
$exitCode = $LASTEXITCODE
if ($exitCode -eq 0) {
    throw "M0.7 provenance regression did not reject the tampered independent oracle."
}
if (($output | Out-String) -notmatch "oracle|fixture file hash") {
    throw "M0.7 provenance regression failed for an unexpected reason."
}
Write-Output "M0.7 provenance regression passed: a tampered independent oracle was rejected."
