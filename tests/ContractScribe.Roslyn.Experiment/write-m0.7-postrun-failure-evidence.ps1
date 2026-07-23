[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,
    [Parameter(Mandatory = $true)]
    [string]$BaselineCommit
)

$ErrorActionPreference = "Stop"
$outputRootPath = [IO.Path]::GetFullPath($OutputRoot)
$successEvidencePath = Join-Path $outputRootPath "m0.7-evidence.json"
$failurePath = Join-Path $outputRootPath "m0.7-failure-evidence.json"
$hadSuccessEvidence = Test-Path -LiteralPath $successEvidencePath
if (Test-Path -LiteralPath $outputRootPath) {
    Get-ChildItem -LiteralPath $outputRootPath -Directory -Filter "run-*" -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $successEvidencePath -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null
if (-not (Test-Path -LiteralPath $failurePath)) {
    $aggregateOutcome = if ($hadSuccessEvidence) { "protocol-failure" } else { "inconclusive" }
    $reasonCode = if ($hadSuccessEvidence) { "post-run-validation-failure" } else { "pre-verifier-validation-failure" }
    $failure = [ordered]@{
        formatVersion = "contractscribe-m0.7-failure-evidence-v1"
        aggregateOutcome = $aggregateOutcome
        reasonCode = $reasonCode
        selectedBaselineCommit = $BaselineCommit
        fixtureCommit = $null
        protocolCommit = $env:GITHUB_SHA
        ci = [ordered]@{ runId = $env:GITHUB_RUN_ID; job = $env:GITHUB_JOB; sha = $env:GITHUB_SHA }
        retainedFailure = $true
    }
    [IO.File]::WriteAllText($failurePath, ($failure | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}
Write-Output "M0.7 post-run failure evidence retained: raw run output is removed and only bounded failure evidence remains."
