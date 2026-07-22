[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$root = Join-Path $repositoryRoot "TestResults\m0.7-aggregate-outcome-vectors"
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Path $root | Out-Null

$vectors = @(
    @{ name = "baseline-invalidated-over-protocol"; outcomes = @("baseline-invalidated", "protocol-failure"); expected = "baseline-invalidated" },
    @{ name = "protocol-over-baseline-failure"; outcomes = @("protocol-failure", "baseline-failure"); expected = "protocol-failure" },
    @{ name = "baseline-failure-over-inconclusive"; outcomes = @("baseline-failure", "inconclusive"); expected = "baseline-failure" },
    @{ name = "inconclusive-only"; outcomes = @("inconclusive"); expected = "inconclusive" }
)

foreach ($vector in $vectors) {
    $evidenceRoot = Join-Path $root $vector.name
    New-Item -ItemType Directory -Path $evidenceRoot | Out-Null
    $index = 0
    foreach ($outcome in $vector.outcomes) {
        $index++
        $cellRoot = Join-Path $evidenceRoot ("cell-{0}" -f $index)
        New-Item -ItemType Directory -Path $cellRoot | Out-Null
        [IO.File]::WriteAllText((Join-Path $cellRoot "m0.7-failure-evidence.json"), (@{ formatVersion = "contractscribe-m0.7-failure-evidence-v1"; aggregateOutcome = $outcome; retainedFailure = $true } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    }
    $outputPath = Join-Path $evidenceRoot "aggregate.json"
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "aggregate-m0.7.ps1") -EvidenceRoot $evidenceRoot -OutputPath $outputPath 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { throw "Aggregate outcome vector unexpectedly succeeded." }
    $aggregate = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    if ($aggregate.aggregateOutcome -ne $vector.expected -or -not $aggregate.retainedFailure) {
        throw "Aggregate outcome precedence vector did not retain its expected outcome."
    }
}

function Write-SyntheticCellEvidence([string]$cellRoot, [bool]$crossRunEquality, [int]$runCount, [bool]$writePayloads) {
    New-Item -ItemType Directory -Path $cellRoot -Force | Out-Null
    $runs = @()
    for ($run = 1; $run -le $runCount; $run++) {
        $payloadPath = Join-Path $cellRoot ("run-{0}\semantic-payload.json" -f $run)
        if ($writePayloads) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $payloadPath) -Force | Out-Null
            [IO.File]::WriteAllText($payloadPath, "payload", [Text.UTF8Encoding]::new($false))
        }
        $runs += [ordered]@{ run = $run; payloadSha256 = if ($writePayloads) { (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { "0000000000000000000000000000000000000000000000000000000000000000" }; sdkVersion = "10.0.302"; msbuildVersion = "18.6.11.33009"; runtimeVersion = "10.0.10"; processArchitecture = "X64" }
    }
    $document = [ordered]@{
        aggregateOutcome = "succeeded"
        runnerOs = "Linux"
        rid = "linux-x64"
        selectedBaselineCommit = "645c0946b8b811d633b471b232b0654c10e6d7f6"
        protocolCommit = "0000000000000000000000000000000000000000"
        fixtureCommit = "aee85e30a7634fdf6adce7ac8b1a185a68b9698a"
        oracleSha256 = "df8202a209fc0005fe897779fa97c9c44212140f633229414a9271f739338fdc"
        comparison = @{ crossRunEquality = $crossRunEquality; oracleEquality = $true }
        runs = $runs
    }
    [IO.File]::WriteAllText((Join-Path $cellRoot "m0.7-evidence.json"), ($document | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}

$postRunVectors = @(
    @{ name = "fresh-run-byte-mismatch"; crossRunEquality = $false; runCount = 0; writePayloads = $false; expected = "baseline-failure" },
    @{ name = "fresh-run-count-invalid"; crossRunEquality = $true; runCount = 1; writePayloads = $false; expected = "protocol-failure" },
    @{ name = "payload-artifact-missing"; crossRunEquality = $true; runCount = 2; writePayloads = $false; expected = "protocol-failure" }
)
foreach ($vector in $postRunVectors) {
    $evidenceRoot = Join-Path $root $vector.name
    Write-SyntheticCellEvidence (Join-Path $evidenceRoot "cell-1") $vector.crossRunEquality $vector.runCount $vector.writePayloads
    Write-SyntheticCellEvidence (Join-Path $evidenceRoot "cell-2") $vector.crossRunEquality $vector.runCount $vector.writePayloads
    $outputPath = Join-Path $evidenceRoot "aggregate.json"
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "aggregate-m0.7.ps1") -EvidenceRoot $evidenceRoot -OutputPath $outputPath 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { throw "Aggregate post-run vector unexpectedly succeeded." }
    $aggregate = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    if ($aggregate.aggregateOutcome -ne $vector.expected -or -not $aggregate.retainedFailure) {
        throw "Aggregate post-run vector did not retain its expected outcome."
    }
}

Remove-Item -LiteralPath $root -Recurse -Force
Write-Output "M0.7 aggregate outcome vectors passed: baseline-invalidated, protocol-failure, baseline-failure, and inconclusive precedence are retained."
