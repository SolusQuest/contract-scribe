[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath,
    [Parameter(Mandatory = $true)]
    [string]$CurrentHeadCommit
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$resolvedEvidencePath = Join-Path $repositoryRoot $EvidencePath
$evidence = Get-Content -LiteralPath $resolvedEvidencePath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($evidence.protocolPrHeadCommit)) { throw "Committed M0.7 evidence has no protocol PR-head identity." }
if ([string]::IsNullOrWhiteSpace($evidence.validationMergeCommit)) { throw "Committed M0.7 evidence has no validation merge-ref identity." }
if ([string]::IsNullOrWhiteSpace($evidence.evidencePublicationCommit)) { throw "Committed M0.7 evidence has no publication commit identity." }
if ($CurrentHeadCommit -notmatch "^[0-9a-f]{40}$" -or $evidence.protocolPrHeadCommit -notmatch "^[0-9a-f]{40}$" -or $evidence.evidencePublicationCommit -notmatch "^[0-9a-f]{40}$") { throw "M0.7 evidence identity is not a full commit SHA." }
& git -C $repositoryRoot cat-file -e "$($evidence.evidencePublicationCommit)^{commit}"
if ($LASTEXITCODE -ne 0) { throw "The evidence publication commit does not exist in the repository history." }
& git -C $repositoryRoot merge-base --is-ancestor $evidence.protocolPrHeadCommit $evidence.evidencePublicationCommit
if ($LASTEXITCODE -ne 0) { throw "The evidence publication commit predates the protocol PR head." }
& git -C $repositoryRoot merge-base --is-ancestor $evidence.evidencePublicationCommit $CurrentHeadCommit
if ($LASTEXITCODE -ne 0) { throw "The evidence publication commit is not part of the current PR history." }
$publicationFiles = @(& git -C $repositoryRoot diff-tree --no-commit-id --name-only -r $evidence.evidencePublicationCommit -- $EvidencePath | ForEach-Object { $_.Trim().Replace("\", "/") } | Where-Object { $_ })
if ($publicationFiles -notcontains $EvidencePath.Replace("\", "/")) { throw "The evidence publication commit did not publish the committed evidence file." }
if ($CurrentHeadCommit -eq $evidence.protocolPrHeadCommit) {
    Write-Output "M0.7 evidence freshness guard passed: current protocol PR head is covered."
    exit 0
}
& git -C $repositoryRoot merge-base --is-ancestor $evidence.protocolPrHeadCommit $CurrentHeadCommit
if ($LASTEXITCODE -ne 0) { throw "The current PR head is not a descendant of the evidence protocol PR head." }
$changedFiles = @(& git -C $repositoryRoot diff --name-only $evidence.protocolPrHeadCommit $CurrentHeadCommit | ForEach-Object { $_.Trim().Replace("\", "/") } | Where-Object { $_ })
$allowedFiles = @($EvidencePath.Replace("\", "/"))
$unexpectedFiles = @($changedFiles | Where-Object { $allowedFiles -notcontains $_ })
if ($unexpectedFiles.Count -gt 0) {
    throw ("M0.7 evidence is stale after protocol changes: " + ($unexpectedFiles -join ", "))
}
Write-Output "M0.7 evidence freshness guard passed: only evidence publication files changed after the protocol head."
