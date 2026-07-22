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

Remove-Item -LiteralPath $root -Recurse -Force
Write-Output "M0.7 aggregate outcome vectors passed: baseline-invalidated, protocol-failure, baseline-failure, and inconclusive precedence are retained."
