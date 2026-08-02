[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,
    [Parameter(Mandatory = $true)]
    [string]$BaselineCommit,
    [string]$ManifestPath,
    [string]$EvidencePrHeadCommit,
    [string]$EvidenceValidationMergeCommit,
    [string]$EvidenceRunId,
    [string]$EvidenceRunAttempt,
    [string]$EvidenceRunnerOs,
    [string]$EvidenceRid,
    [string]$EvidenceEventPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "resolve-m0.7-terminal-identity.ps1")
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $repositoryRoot "tests\fixtures\roslyn-msbuild\m0.7-independent-validation-manifest.json"
}
$manifest = $null
try {
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
}
catch {
    $manifest = $null
}
$terminalIdentity = $null
$identityError = $null
try {
    $terminalIdentity = Resolve-M07TerminalIdentity `
        -PrHeadCommit $EvidencePrHeadCommit `
        -ValidationMergeCommit $EvidenceValidationMergeCommit `
        -RunId $EvidenceRunId `
        -RunAttempt $EvidenceRunAttempt `
        -RunnerOs $EvidenceRunnerOs `
        -Rid $EvidenceRid `
        -EventPath $EvidenceEventPath
}
catch {
    $identityError = $_.Exception.Message
}
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
    $aggregateOutcome = if ($null -ne $identityError -or $hadSuccessEvidence) { "protocol-failure" } else { "inconclusive" }
    $reasonCode = if ($null -ne $identityError) { "evidence-identity-invalid" } elseif ($hadSuccessEvidence) { "post-run-validation-failure" } else { "pre-verifier-validation-failure" }
    $failure = [ordered]@{
        formatVersion = "contractscribe-m0.7-failure-evidence-v1"
        aggregateOutcome = $aggregateOutcome
        reasonCode = $reasonCode
        protocolPrHeadCommit = if ($null -ne $terminalIdentity) { $terminalIdentity.protocolPrHeadCommit } else { $null }
        validationMergeCommit = if ($null -ne $terminalIdentity) { $terminalIdentity.validationMergeCommit } else { $null }
        runnerOs = if ($null -ne $terminalIdentity) { $terminalIdentity.runnerOs } else { $null }
        rid = if ($null -ne $terminalIdentity) { $terminalIdentity.rid } else { $null }
        selectedBaselineCommit = $BaselineCommit
        fixtureCommit = if ($null -ne $manifest) { $manifest.fixture.commit } else { $null }
        oracleSha256 = if ($null -ne $manifest) { $manifest.fixture.oracleSha256 } else { $null }
        protocolCommit = if ($null -ne $terminalIdentity) { $terminalIdentity.protocolCommit } else { $null }
        ci = [ordered]@{
            runId = if ($null -ne $terminalIdentity) { $terminalIdentity.runId } else { $null }
            runAttempt = if ($null -ne $terminalIdentity) { $terminalIdentity.runAttempt } else { $null }
            job = $env:GITHUB_JOB
            sha = if ($null -ne $terminalIdentity) { $terminalIdentity.validationMergeCommit } else { $null }
        }
        retainedFailure = $true
    }
    [IO.File]::WriteAllText($failurePath, ($failure | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}
Write-Output "M0.7 post-run failure evidence retained: raw run output is removed and only bounded failure evidence remains."
