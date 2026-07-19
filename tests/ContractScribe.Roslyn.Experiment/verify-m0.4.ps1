[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$fixtureRoot = Join-Path $repositoryRoot "tests\fixtures\roslyn-msbuild\v1"
$manifestPath = Join-Path $fixtureRoot "transfer-manifest.json"
$hostPath = Join-Path $repositoryRoot "tests\ContractScribe.Roslyn.Experiment\bin\$Configuration\net10.0\ContractScribe.Roslyn.Experiment.dll"
$outputRoot = Join-Path $repositoryRoot "TestResults\m0.4-protocol"

function Assert-Condition([bool]$condition, [string]$message) {
    if (-not $condition) {
        throw $message
    }
}

Assert-Condition (Test-Path -LiteralPath $hostPath) "The built experiment host was not found."
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

foreach ($entry in $manifest.fixtureContentSha256.PSObject.Properties) {
    $path = Join-Path $fixtureRoot $entry.Name
    Assert-Condition (Test-Path -LiteralPath $path) "Manifest fixture file is missing."
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Condition ($actual -eq $entry.Value) "Fixture content hash does not match the transfer manifest."
}

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outputRoot | Out-Null

$payloadPaths = @()
for ($run = 1; $run -le 2; $run++) {
    $runDirectory = Join-Path $outputRoot "run-$run"
    New-Item -ItemType Directory -Path $runDirectory | Out-Null
    $stdoutPath = Join-Path $runDirectory "stdout.txt"
    $stderrPath = Join-Path $runDirectory "stderr.txt"
    & dotnet $hostPath (Join-Path $fixtureRoot $manifest.solution) $runDirectory 1> $stdoutPath 2> $stderrPath
    $exitCode = $LASTEXITCODE
    Assert-Condition ($exitCode -eq 0) "The framework-dependent experiment did not succeed."

    $resultPath = Join-Path $runDirectory "result.json"
    $payloadPath = Join-Path $runDirectory $manifest.comparison.artifact
    Assert-Condition (Test-Path -LiteralPath $resultPath) "The experiment result envelope was not written."
    Assert-Condition (Test-Path -LiteralPath $payloadPath) "The semantic payload was not written."
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    Assert-Condition ($result.status -eq "succeeded") "The result envelope did not report success."
    Assert-Condition ($result.toolchain.sdkVersion -eq $manifest.sdk.globalJsonVersion) "The selected SDK does not match global.json."
    Assert-Condition ($result.toolchain.discoveryType -eq "DotNetSdk") "The selected toolchain was not discovered as a dotnet SDK."
    Assert-Condition ($result.toolchain.sdkVersion -eq $manifest.observedEvidence.toolchain.sdkVersion) "Observed SDK evidence does not match the run."
    Assert-Condition ($result.toolchain.msbuildVersion -eq $manifest.observedEvidence.toolchain.msbuildVersion) "Observed MSBuild evidence does not match the run."
    $payloadPaths += $payloadPath

    $payloadBytes = [System.IO.File]::ReadAllBytes($payloadPath)
    Assert-Condition ($payloadBytes.Length -gt 0) "The semantic payload is empty."
    Assert-Condition (-not ($payloadBytes[0] -eq 0xEF -and $payloadBytes[1] -eq 0xBB -and $payloadBytes[2] -eq 0xBF)) "The semantic payload has a BOM."
    Assert-Condition ($payloadBytes[$payloadBytes.Length - 1] -ne 0x0A -and $payloadBytes[$payloadBytes.Length - 1] -ne 0x0D) "The semantic payload has a trailing newline."
    $payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Condition ($payloadHash -eq $manifest.observedEvidence.payloadSha256) "Observed payload evidence does not match the run."

    $publicOutput = (Get-Content -LiteralPath $stdoutPath -Raw) + (Get-Content -LiteralPath $stderrPath -Raw) + (Get-Content -LiteralPath $resultPath -Raw)
    Assert-Condition ($publicOutput -notmatch "(?i)([A-Z]:\\|/home/|/Users/|authorization|bearer\s|access[_-]?token|api[_-]?key)") "Public experiment output contains a machine path or credential-like value."
}

$first = [System.IO.File]::ReadAllBytes($payloadPaths[0])
$second = [System.IO.File]::ReadAllBytes($payloadPaths[1])
Assert-Condition ([System.Linq.Enumerable]::SequenceEqual($first, $second)) "Fresh-process semantic payloads are not byte-identical."

Write-Output "M0.4 protocol verified: fixture hashes, toolchain identity, two fresh-process runs, byte comparison, and public-safety scan passed."
