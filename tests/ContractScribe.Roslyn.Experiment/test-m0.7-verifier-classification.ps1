[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaselineRepositoryPath,
    [Parameter(Mandatory = $true)]
    [string]$FixtureRepositoryPath
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$outputRoot = Join-Path $repositoryRoot "TestResults\m0.7-verifier-classification"
if (Test-Path -LiteralPath $outputRoot) { Remove-Item -LiteralPath $outputRoot -Recurse -Force }

$verifyParameters = @{
    Configuration = "Release"
    BaselineRepositoryPath = $BaselineRepositoryPath
    FixtureRepositoryPath = $FixtureRepositoryPath
    BaselineCommit = "0000000000000000000000000000000000000000"
    OutputRoot = $outputRoot
}
$output = & pwsh -NoProfile -File (Join-Path $PSScriptRoot "verify-m0.7.ps1") @verifyParameters 2>&1
if ($LASTEXITCODE -eq 0) { throw "Verifier classification regression unexpectedly succeeded." }
$failurePath = Join-Path $outputRoot "m0.7-failure-evidence.json"
if (-not (Test-Path -LiteralPath $failurePath)) { throw "Verifier classification regression did not write failure evidence." }
$failure = Get-Content -LiteralPath $failurePath -Raw | ConvertFrom-Json
if ($failure.aggregateOutcome -ne "baseline-invalidated" -or $failure.reasonCode -ne "selected-baseline-drift") {
    throw "Verifier classification regression did not route baseline drift through the typed classifier."
}
if ((Get-ChildItem -LiteralPath $outputRoot -Directory -Filter "run-*").Count -ne 0) { throw "Verifier classification regression retained raw run output." }
Remove-Item -LiteralPath $outputRoot -Recurse -Force
Write-Output "M0.7 verifier classification regression passed: baseline drift uses the typed baseline-invalidated outcome."
