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

$fixtureMismatchRoot = Join-Path $outputRoot "fixture-mismatch"
New-Item -ItemType Directory -Path $fixtureMismatchRoot -Force | Out-Null
$fixtureMismatchOutput = Join-Path $outputRoot "fixture-mismatch-output"
$fixtureMismatch = @{
    Configuration = "Release"
    BaselineRepositoryPath = $BaselineRepositoryPath
    FixtureRepositoryPath = $fixtureMismatchRoot
    BaselineCommit = "645c0946b8b811d633b471b232b0654c10e6d7f6"
    OutputRoot = $fixtureMismatchOutput
}
$output = & pwsh -NoProfile -File (Join-Path $PSScriptRoot "verify-m0.7.ps1") @fixtureMismatch 2>&1
if ($LASTEXITCODE -eq 0) { throw "Fixture checkout classification regression unexpectedly succeeded." }
$fixtureFailurePath = Join-Path $fixtureMismatchOutput "m0.7-failure-evidence.json"
$fixtureFailure = Get-Content -LiteralPath $fixtureFailurePath -Raw | ConvertFrom-Json
if ($fixtureFailure.aggregateOutcome -ne "protocol-failure" -or $fixtureFailure.reasonCode -ne "fixture-checkout-drift") {
    throw "Fixture checkout drift was not routed through the typed protocol-failure classifier."
}
Remove-Item -LiteralPath $fixtureMismatchRoot -Recurse -Force
Remove-Item -LiteralPath $fixtureMismatchOutput -Recurse -Force

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
. (Join-Path $PSScriptRoot "m0.7-output-policy.ps1")
if (Test-M07PublicOutputSafe "safe result") { } else { throw "Safe public output was rejected." }
if (Test-M07PublicOutputSafe "C:\private\secret") { throw "Unsafe public output was accepted." }
Write-Output "M0.7 verifier classification regression passed: fixture drift, baseline drift, and public safety use typed outcomes."
