[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaselineRepositoryPath,
    [Parameter(Mandatory = $true)]
    [string]$BaselineCommit
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$outputRoot = Join-Path $repositoryRoot "TestResults\m0.7-failure-safety"
if (Test-Path -LiteralPath $outputRoot) { Remove-Item -LiteralPath $outputRoot -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $outputRoot "run-1") | Out-Null
[IO.File]::WriteAllText((Join-Path $outputRoot "run-1\stdout.txt"), "Authorization: Bearer synthetic-test-token", [Text.UTF8Encoding]::new($false))

$missingFixture = Join-Path $outputRoot "missing-fixture"
$output = & pwsh -NoProfile -File (Join-Path $PSScriptRoot "verify-m0.7.ps1") `
    -Configuration Release `
    -BaselineRepositoryPath $BaselineRepositoryPath `
    -FixtureRepositoryPath $missingFixture `
    -BaselineCommit $BaselineCommit `
    -OutputRoot $outputRoot 2>&1
if ($LASTEXITCODE -eq 0) { throw "Failure-safety regression unexpectedly succeeded." }
$failurePath = Join-Path $outputRoot "m0.7-failure-evidence.json"
if (-not (Test-Path -LiteralPath $failurePath)) { throw "Failure-safety regression did not retain bounded failure evidence." }
if ((Get-ChildItem -LiteralPath $outputRoot -Directory -Filter "run-*").Count -ne 0) { throw "Failure-safety regression retained raw run directories." }
if ((Get-Content -LiteralPath $failurePath -Raw) -match "Authorization|Bearer|synthetic-test-token|stdout") { throw "Failure-safety regression leaked raw failure content." }
Remove-Item -LiteralPath $outputRoot -Recurse -Force
Write-Output "M0.7 failure-safety regression passed: raw run output is removed and only bounded failure evidence remains."
