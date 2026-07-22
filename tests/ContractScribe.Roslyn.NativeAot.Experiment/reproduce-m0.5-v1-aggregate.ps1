[CmdletBinding()]
param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",
    [string]$CellProofRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$anchorRevision = "63fd9a0ab5ff33ae20d8f7b9e66714a96feea39e"
$implementationRevision = "ed305a36f076d2d9aef981c44746d7a5a34d5bff"
$manifestRelativePath = "tests/fixtures/roslyn-msbuild/v1/m0.5-native-aot-manifest.json"
$transferManifestRelativePath = "tests/fixtures/roslyn-msbuild/v1/transfer-manifest.json"
$linuxEvidenceRelativePath = "tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-linux-x64-evidence-v1.json"
$windowsEvidenceRelativePath = "tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-win-x64-evidence-v1.json"
$summaryRelativePath = "tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-summary-v1.json"
$overlayPaths = @(
    $transferManifestRelativePath,
    $manifestRelativePath,
    "schemas/experiments/m0.5-native-aot-evidence-v1.schema.json",
    "docs/20_architecture/experiments/m0.5-native-aot-registry-v1.json",
    $linuxEvidenceRelativePath,
    $windowsEvidenceRelativePath,
    $summaryRelativePath
)
$worktreePath = Join-Path ([IO.Path]::GetTempPath()) ("contract-scribe-m05-v1-aggregate-" + [Guid]::NewGuid().ToString("N"))
$proofPath = Join-Path $repositoryRoot "TestResults\m05-v1-reproduction-aggregate-proof.json"
$initialStatus = @()
$initialFullStatus = @()
$initialWorktrees = @()
$failure = $false
$cleanupFailure = $false
$proof = $null

function Assert-Condition([bool]$condition, [string]$message = "closed-check-failed") {
    if (-not $condition) { throw $message }
}

function Invoke-Git([string[]]$arguments, [string]$workingDirectory = $repositoryRoot) {
    $output = & git -C $workingDirectory @arguments 2>$null
    if ($LASTEXITCODE -ne 0) { throw "git-command-failed" }
    return @($output)
}

function Get-GitBytes([string]$revision, [string]$relativePath) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "git"
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @("-C", $repositoryRoot, "cat-file", "blob", ($revision + ":" + $relativePath))) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw "git-process-failed" }
    $stream = [IO.MemoryStream]::new()
    $process.StandardOutput.BaseStream.CopyTo($stream)
    $process.StandardError.ReadToEnd() | Out-Null
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "git-blob-unavailable" }
    return $stream.ToArray()
}

function Get-FileBytes([string]$path) {
    Assert-Condition (Test-Path -LiteralPath $path) "file-missing"
    return [IO.File]::ReadAllBytes($path)
}

function Get-Sha256([byte[]]$bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Assert-BytesEqual([byte[]]$expected, [byte[]]$actual) {
    Assert-Condition ($expected.Length -eq $actual.Length -and (Get-Sha256 $expected) -eq (Get-Sha256 $actual)) "blob-mismatch"
}

function Get-GitStatusPaths([string]$path) {
    $lines = @(Invoke-Git @("status", "--porcelain", "--untracked-files=all", "--ignored") $path)
    return @($lines | ForEach-Object {
        if ($_.Length -ge 4) { $_.Substring(3) }
    } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Get-GitWorktreeList {
    return @(Invoke-Git @("worktree", "list", "--porcelain"))
}

function Remove-Proof {
    if (Test-Path -LiteralPath $proofPath) {
        Remove-Item -LiteralPath $proofPath -Force
    }
}

function Invoke-Captured([string]$fileName, [string[]]$arguments, [string]$workingDirectory) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $fileName
    $startInfo.WorkingDirectory = $workingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $arguments) { [void]$startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw "historical-process-failed" }
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    return [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout; Stderr = $stderr }
}

function Write-CanonicalJson([string]$path, [object]$value) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 10 -Compress), [Text.UTF8Encoding]::new($false))
}

function Assert-CellProofs([string]$root, [string]$orchestrationRevision) {
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($root)) "cell-proof-root-missing"
    Assert-Condition (Test-Path -LiteralPath $root -PathType Container) "cell-proof-root-missing"
    $expected = @("linux-x64", "win-x64")
    $files = @(Get-ChildItem -LiteralPath $root -File -Filter "*.json" | Select-Object -ExpandProperty Name | Sort-Object)
    Assert-Condition (($files -join "`n") -ceq ((@("m05-v1-reproduction-proof-linux-x64.json", "m05-v1-reproduction-proof-win-x64.json") | Sort-Object) -join "`n")) "cell-proof-set-mismatch"
    foreach ($rid in $expected) {
        $path = Join-Path $root ("m05-v1-reproduction-proof-" + $rid + ".json")
        try { $cellProof = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { throw "cell-proof-malformed" }
        Assert-Condition ($cellProof.version -eq "m0.5-v1-reproduction-proof-v1") "cell-proof-version-mismatch"
        Assert-Condition ($cellProof.rid -eq $rid) "cell-proof-rid-mismatch"
        Assert-Condition ($cellProof.anchorRevision -eq $anchorRevision) "cell-proof-anchor-mismatch"
        Assert-Condition ($cellProof.implementationRevision -eq $implementationRevision) "cell-proof-implementation-mismatch"
        Assert-Condition ($cellProof.orchestrationRevision -eq $orchestrationRevision) "cell-proof-orchestration-mismatch"
        Assert-Condition ($cellProof.byteEqual -eq $true) "cell-proof-byte-mismatch"
    }
}

try {
    Remove-Proof
    # The aggregate proof is transitive: anchor blobs equal historical blobs, and historical reproduction equals those blobs.
    $initialStatus = @(Invoke-Git @("status", "--porcelain", "--untracked-files=all") $repositoryRoot)
    $initialFullStatus = @(Invoke-Git @("status", "--porcelain", "--untracked-files=all", "--ignored") $repositoryRoot)
    $initialWorktrees = @(Get-GitWorktreeList)
    Assert-Condition ($initialStatus.Count -eq 0) "current-tree-dirty"
    Invoke-Git @("cat-file", "-e", ($anchorRevision + "^{commit}")) | Out-Null
    try { Invoke-Git @("cat-file", "-e", ($implementationRevision + "^{commit}")) | Out-Null }
    catch {
        Invoke-Git @("fetch", "--no-tags", "origin", $implementationRevision) | Out-Null
        Invoke-Git @("cat-file", "-e", ($implementationRevision + "^{commit}")) | Out-Null
    }

    $orchestrationRevision = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { (Invoke-Git @("rev-parse", "HEAD")).Trim() }
    Assert-Condition ($orchestrationRevision -match "^[0-9a-f]{40}$") "orchestration-revision-invalid"
    if (-not [string]::IsNullOrWhiteSpace($CellProofRoot)) { Assert-CellProofs $CellProofRoot $orchestrationRevision }

    foreach ($relativePath in @($linuxEvidenceRelativePath, $windowsEvidenceRelativePath, $summaryRelativePath)) {
        Assert-BytesEqual (Get-GitBytes $anchorRevision $relativePath) (Get-FileBytes (Join-Path $repositoryRoot $relativePath))
    }

    Invoke-Git @("worktree", "add", "--detach", $worktreePath, $implementationRevision) | Out-Null
    Assert-Condition ((Get-GitStatusPaths $worktreePath).Count -eq 0) "historical-tree-dirty-before-overlay"
    $changedOverlayPaths = @()
    foreach ($relativePath in $overlayPaths) {
        $anchorBytes = Get-GitBytes $anchorRevision $relativePath
        $targetPath = Join-Path $worktreePath $relativePath
        $targetBytes = if (Test-Path -LiteralPath $targetPath) { Get-FileBytes $targetPath } else { [byte[]]@() }
        if (-not ($anchorBytes.Length -eq $targetBytes.Length -and (Get-Sha256 $anchorBytes) -eq (Get-Sha256 $targetBytes))) {
            $changedOverlayPaths += $relativePath
            New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force | Out-Null
            [IO.File]::WriteAllBytes($targetPath, $anchorBytes)
        }
    }
    $dirtyPaths = @(Get-GitStatusPaths $worktreePath)
    Assert-Condition (@($dirtyPaths | Where-Object { $changedOverlayPaths -notcontains $_ }).Count -eq 0) "overlay-outside-closed-set"
    Assert-Condition (@($dirtyPaths | Where-Object { $changedOverlayPaths -contains $_ }).Count -eq $changedOverlayPaths.Count) "overlay-dirty-set-mismatch"

    if ($env:CONTRACTSCRIBE_M05_TEST_FAIL_HISTORICAL -eq "1") { throw "injected-historical-failure" }
    $historicalScript = Join-Path $worktreePath "tests\ContractScribe.Roslyn.NativeAot.Experiment\aggregate-m0.5.ps1"
    $run = Invoke-Captured "pwsh" @("-NoProfile", "-File", $historicalScript, "-LinuxEvidencePath", $linuxEvidenceRelativePath, "-WindowsEvidencePath", $windowsEvidenceRelativePath, "-EvidenceReproduction") $worktreePath
    Assert-Condition ($run.ExitCode -eq 0) "historical-aggregate-failed"
    $proof = [ordered]@{
        version = "m0.5-v1-reproduction-aggregate-proof-v1"
        anchorRevision = $anchorRevision
        implementationRevision = $implementationRevision
        orchestrationRevision = $orchestrationRevision
        byteEqual = $true
    }
}
catch {
    $failure = $true
    Write-Output "M0.5 V1 aggregate reproduction failed closed."
}
finally {
    if (Test-Path -LiteralPath $worktreePath) {
        try { Invoke-Git @("worktree", "remove", "--force", $worktreePath) | Out-Null } catch { $cleanupFailure = $true }
    }
    try { Invoke-Git @("worktree", "prune") | Out-Null } catch { $cleanupFailure = $true }
    try {
        if (Test-Path -LiteralPath $worktreePath) { $cleanupFailure = $true }
        $finalWorktrees = @(Get-GitWorktreeList)
        if ((($finalWorktrees -join "`n") -cne ($initialWorktrees -join "`n"))) { $cleanupFailure = $true }
        $finalStatus = @(Invoke-Git @("status", "--porcelain", "--untracked-files=all", "--ignored") $repositoryRoot)
        if ((($finalStatus -join "`n") -cne ($initialFullStatus -join "`n"))) { $cleanupFailure = $true }
        if (Test-Path -LiteralPath $proofPath) { $cleanupFailure = $true }
    }
    catch { $cleanupFailure = $true }
}
if ($failure -or $cleanupFailure) {
    try { Remove-Proof } catch { }
    exit 1
}
try {
    Write-CanonicalJson $proofPath $proof
    Write-Output "M0.5 V1 aggregate reproduction proof passed."
    exit 0
}
catch {
    try { Remove-Proof } catch { }
    Write-Output "M0.5 V1 aggregate reproduction failed closed."
    exit 1
}
