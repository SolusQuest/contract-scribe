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
$headCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$runnerOs = if ($IsWindows) { "Windows" } else { "Linux" }
$rid = if ($IsWindows) { "win-x64" } else { "linux-x64" }
$identityArguments = @{
    EvidencePrHeadCommit = $headCommit
    EvidenceValidationMergeCommit = $headCommit
    EvidenceRunId = "202"
    EvidenceRunAttempt = "1"
    EvidenceRunnerOs = $runnerOs
    EvidenceRid = $rid
}
$target = Join-Path $repositoryRoot "TestResults\m0.7-provenance-tamper"
if (Test-Path -LiteralPath $target) {
    Remove-Item -LiteralPath $target -Recurse -Force
}
New-Item -ItemType Directory -Path $target | Out-Null
Copy-Item -Path (Join-Path (Resolve-Path $FixtureRepositoryPath).Path "*") -Destination $target -Recurse -Force
$oracle = Join-Path $target "expected-payload.json"
[IO.File]::AppendAllText($oracle, " ")
$validationOutput = Join-Path $target "validation-output"

$output = & pwsh -NoProfile -File (Join-Path $PSScriptRoot "verify-m0.7.ps1") `
    -Configuration $Configuration `
    -BaselineRepositoryPath $BaselineRepositoryPath `
    -FixtureRepositoryPath $target `
    -BaselineCommit $BaselineCommit `
    -OutputRoot $validationOutput @identityArguments 2>&1
$exitCode = $LASTEXITCODE
if ($exitCode -eq 0) {
    throw "M0.7 provenance regression did not reject the tampered independent oracle."
}
if (($output | Out-String) -notmatch "oracle|fixture file hash|protocol-failure|protocol-input-invalid") {
    throw "M0.7 provenance regression failed for an unexpected reason."
}
$failureEvidencePath = Join-Path $validationOutput "m0.7-failure-evidence.json"
if (-not (Test-Path -LiteralPath $failureEvidencePath)) {
    throw "M0.7 provenance regression did not retain bounded failure evidence."
}
$failureEvidence = Get-Content -LiteralPath $failureEvidencePath -Raw | ConvertFrom-Json
if ($failureEvidence.aggregateOutcome -ne "protocol-failure" -or -not $failureEvidence.retainedFailure) {
    throw "M0.7 provenance regression retained an invalid failure outcome."
}
if ($failureEvidence.runnerOs -cne $runnerOs -or $failureEvidence.rid -cne $rid -or $failureEvidence.ci.runAttempt -ne 1 -or $failureEvidence.protocolPrHeadCommit -cne $headCommit -or $failureEvidence.validationMergeCommit -cne $headCommit) {
    throw "M0.7 provenance regression retained invalid terminal identity."
}
Remove-Item -LiteralPath $failureEvidencePath -Force
Remove-Item -LiteralPath $target -Recurse -Force
Write-Output "M0.7 provenance regression passed: a tampered independent oracle was rejected."
