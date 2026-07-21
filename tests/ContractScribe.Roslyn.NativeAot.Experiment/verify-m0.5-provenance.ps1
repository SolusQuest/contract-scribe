[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot,
    [Parameter(Mandatory = $true)]
    [string]$FrozenSourceRevision,
    [Parameter(Mandatory = $true)]
    [string]$CurrentRevision,
    [Parameter(Mandatory = $true)]
    [string]$AllowedFiles
)

$ErrorActionPreference = "Stop"
$allowedFileSet = @($AllowedFiles -split "\|")

function Assert-Condition([bool]$condition, [string]$message) {
    if (-not $condition) {
        throw $message
    }
}

$resolvedRepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$frozenCommit = (& git -C $resolvedRepositoryRoot rev-parse --verify "$FrozenSourceRevision^{commit}" 2>$null).Trim()
Assert-Condition ($LASTEXITCODE -eq 0 -and $frozenCommit -match "^[0-9a-f]{40}$") "The frozen source revision object is missing or invalid."
$currentCommit = (& git -C $resolvedRepositoryRoot rev-parse --verify "$CurrentRevision^{commit}" 2>$null).Trim()
Assert-Condition ($LASTEXITCODE -eq 0 -and $currentCommit -match "^[0-9a-f]{40}$") "The current revision object is missing or invalid."

$frozenTree = (& git -C $resolvedRepositoryRoot rev-parse --verify "$frozenCommit^{tree}" 2>$null).Trim()
Assert-Condition ($LASTEXITCODE -eq 0 -and $frozenTree -match "^[0-9a-f]{40}$") "The frozen source tree object is missing or invalid."
$currentTree = (& git -C $resolvedRepositoryRoot rev-parse --verify "$currentCommit^{tree}" 2>$null).Trim()
Assert-Condition ($LASTEXITCODE -eq 0 -and $currentTree -match "^[0-9a-f]{40}$") "The current source tree object is missing or invalid."

$changedFiles = @(& git -C $resolvedRepositoryRoot diff --name-only --no-renames $frozenTree $currentTree)
Assert-Condition ($LASTEXITCODE -eq 0) "The frozen and current source trees could not be compared."
$unexpectedFiles = @($changedFiles | Where-Object { $allowedFileSet -notcontains $_ })
Assert-Condition ($unexpectedFiles.Count -eq 0) "The squashed reproduction trees differ outside the closed provenance allowlist: $($unexpectedFiles -join ', ')."

Write-Output "M0.5 provenance verified: frozen and current trees are comparable and all changes are in the closed allowlist."
