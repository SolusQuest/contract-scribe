[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$anchorRevision = "63fd9a0ab5ff33ae20d8f7b9e66714a96feea39e"
$inventory = @(
    "tests/fixtures/roslyn-msbuild/v1/transfer-manifest.json",
    "docs/20_architecture/experiments/m0.4-framework-dependent-loading.md",
    "docs/20_architecture/experiments/m0.4-failure-registry-v1.json",
    "tests/fixtures/roslyn-msbuild/v1/expected-symbols.json",
    "tests/ContractScribe.Tests/RoslynExperimentTests.cs",
    "docs/20_architecture/experiments/m0.5-native-aot-feasibility.md",
    "schemas/experiments/m0.5-native-aot-evidence-v1.schema.json",
    "docs/20_architecture/experiments/m0.5-native-aot-registry-v1.json",
    "tests/fixtures/roslyn-msbuild/v1/m0.5-native-aot-manifest.json",
    "tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-linux-x64-evidence-v1.json",
    "tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-win-x64-evidence-v1.json",
    "tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-summary-v1.json"
)
$allowed = @(
    "Directory.Packages.props",
    "tests/ContractScribe.Roslyn.Experiment/verify-m0.4.ps1",
    "tests/ContractScribe.Roslyn.Experiment/test-m0.4-provenance.ps1",
    "tests/ContractScribe.Roslyn.NativeAot.Experiment/verify-m0.5.ps1",
    "tests/ContractScribe.Roslyn.NativeAot.Experiment/aggregate-m0.5.ps1",
    "tests/ContractScribe.Roslyn.NativeAot.Experiment/reproduce-m0.5-v1.ps1",
    "tests/ContractScribe.Roslyn.NativeAot.Experiment/reproduce-m0.5-v1-aggregate.ps1",
    "tests/ContractScribe.Roslyn.NativeAot.Experiment/test-m0.5-v1-provenance.ps1",
    "tests/ContractScribe.Tests/M05NativeAotContractTests.cs",
    ".github/workflows/ci.yml"
)

function Assert-Condition([bool]$condition) {
    if (-not $condition) { throw "migration-check-failed" }
}

function Invoke-Git([string[]]$arguments) {
    $output = & git -C $repositoryRoot @arguments 2>$null
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

function Get-Sha256([byte[]]$bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-WorktreeList {
    return @(Invoke-Git @("worktree", "list", "--porcelain"))
}

function Invoke-CapturedPwsh([string[]]$arguments, [hashtable]$environment = @{}) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "pwsh"
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $arguments) { [void]$startInfo.ArgumentList.Add($argument) }
    foreach ($entry in $environment.GetEnumerator()) { $startInfo.Environment[$entry.Key] = [string]$entry.Value }
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw "process-start-failed" }
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    return [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout; Stderr = $stderr }
}

$changedFiles = @(Invoke-Git @("diff", "--name-only", ($anchorRevision + "..HEAD")))
Assert-Condition (@($changedFiles | Where-Object { $allowed -notcontains $_ }).Count -eq 0)
foreach ($path in $inventory) {
    $expected = (Invoke-Git @("rev-parse", ($anchorRevision + ":" + $path))).Trim()
    $actual = (Invoke-Git @("hash-object", "--", $path)).Trim()
    Assert-Condition ($expected -eq $actual)
}

$historicalPackageBytes = Get-GitBytes $anchorRevision "Directory.Packages.props"
$historicalPackageText = [Text.Encoding]::UTF8.GetString($historicalPackageBytes)
$oldPackageLine = '<PackageVersion Include="System.Security.Cryptography.Xml" Version="9.0.15" />'
$newPackageLine = '<PackageVersion Include="System.Security.Cryptography.Xml" Version="9.0.18" />'
Assert-Condition ([Regex]::Matches($historicalPackageText, [Regex]::Escape($oldPackageLine)).Count -eq 1)
$expectedPackageText = $historicalPackageText.Replace($oldPackageLine, $newPackageLine)
$expectedPackageBytes = [Text.Encoding]::UTF8.GetBytes($expectedPackageText)
$actualPackageBytes = [IO.File]::ReadAllBytes((Join-Path $repositoryRoot "Directory.Packages.props"))
Assert-Condition ((Get-Sha256 $expectedPackageBytes) -eq (Get-Sha256 $actualPackageBytes))

$manifest = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "tests/fixtures/roslyn-msbuild/v1/m0.5-native-aot-manifest.json") | ConvertFrom-Json
Assert-Condition ($manifest.m04ManifestSha256 -eq "c728b8ab10696767de6a37809f4cde60bdb060621ce3febec1869b92b5801bd3")
Assert-Condition ($manifest.m04FrozenSourceRevision -eq "63e7aa5c0cc16f10b1a5f732f69ca76379a0b34c")
Assert-Condition ($manifest.implementationRevision -eq "ed305a36f076d2d9aef981c44746d7a5a34d5bff")

$tombstones = @(
    "tests/ContractScribe.Roslyn.Experiment/verify-m0.4.ps1",
    "tests/ContractScribe.Roslyn.NativeAot.Experiment/verify-m0.5.ps1",
    "tests/ContractScribe.Roslyn.NativeAot.Experiment/aggregate-m0.5.ps1"
)
foreach ($path in $tombstones) {
    $content = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot $path)
    Assert-Condition ($content -match "current-tree")
    Assert-Condition ($content -match "exit 1")
}

$workflow = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot ".github/workflows/ci.yml")
Assert-Condition ($workflow -match "m05-v1-dispatch-guard")
Assert-Condition ($workflow -match 'test "\$\{\{ github\.ref \}\}" = "refs/heads/main"')
Assert-Condition ($workflow -match "needs\.m05-native-aot-reproduction\.result == 'success'")
Assert-Condition (-not ($workflow -match "Upload sanitized reproduction proof\r?\n\s+if: always\(\)"))
Assert-Condition (-not ($workflow -match "Upload sanitized aggregate reproduction proof\r?\n\s+if: always\(\)"))

$tombstoneHashes = @{}
foreach ($path in $tombstones) {
    $tombstoneHashes[$path] = Get-Sha256 ([IO.File]::ReadAllBytes((Join-Path $repositoryRoot $path)))
}
$tombstoneCases = @(
    @{ Path = "tests/ContractScribe.Roslyn.Experiment/verify-m0.4.ps1"; Guidance = "reproduce-m0.5-v1.ps1" },
    @{ Path = "tests/ContractScribe.Roslyn.NativeAot.Experiment/verify-m0.5.ps1"; Guidance = "reproduce-m0.5-v1.ps1" },
    @{ Path = "tests/ContractScribe.Roslyn.NativeAot.Experiment/aggregate-m0.5.ps1"; Guidance = "reproduce-m0.5-v1-aggregate.ps1" }
)
foreach ($case in $tombstoneCases) {
    $result = Invoke-CapturedPwsh @("-NoProfile", "-File", (Join-Path $repositoryRoot $case.Path))
    Assert-Condition ($result.ExitCode -ne 0)
    Assert-Condition ($result.Stdout -match [Regex]::Escape($case.Guidance))
    Assert-Condition (($result.Stdout + $result.Stderr) -notmatch "(?i)([A-Z]:\\|/home/|/Users/|authorization|bearer\s|access[_-]?token|api[_-]?key)")
    Assert-Condition ($tombstoneHashes[$case.Path] -eq (Get-Sha256 ([IO.File]::ReadAllBytes((Join-Path $repositoryRoot $case.Path)))))
}

$cellProofPath = Join-Path $repositoryRoot "TestResults\m05-v1-reproduction-proof-linux-x64.json"
$aggregateProofPath = Join-Path $repositoryRoot "TestResults\m05-v1-reproduction-aggregate-proof.json"
New-Item -ItemType Directory -Path (Split-Path -Parent $cellProofPath) -Force | Out-Null
[IO.File]::WriteAllText($cellProofPath, '{"byteEqual":true}', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($aggregateProofPath, '{"byteEqual":true}', [Text.UTF8Encoding]::new($false))
$beforeWorktrees = @(Get-WorktreeList)
$failedCell = Invoke-CapturedPwsh @("-NoProfile", "-File", (Join-Path $repositoryRoot "tests/ContractScribe.Roslyn.NativeAot.Experiment/reproduce-m0.5-v1.ps1"), "-RuntimeIdentifier", "linux-x64", "-Configuration", "Release") @{ CONTRACTSCRIBE_M05_TEST_FAIL_HISTORICAL = "1" }
Assert-Condition ($failedCell.ExitCode -ne 0)
Assert-Condition (-not (Test-Path -LiteralPath $cellProofPath))
$afterCellWorktrees = @(Get-WorktreeList)
Assert-Condition (($beforeWorktrees -join "`n") -ceq ($afterCellWorktrees -join "`n"))
Assert-Condition (@($afterCellWorktrees | Where-Object { $_ -match "contract-scribe-m05-v1-" }).Count -eq 0)

$beforeAggregateWorktrees = @(Get-WorktreeList)
$failedAggregate = Invoke-CapturedPwsh @("-NoProfile", "-File", (Join-Path $repositoryRoot "tests/ContractScribe.Roslyn.NativeAot.Experiment/reproduce-m0.5-v1-aggregate.ps1"), "-Configuration", "Release") @{ CONTRACTSCRIBE_M05_TEST_FAIL_HISTORICAL = "1" }
Assert-Condition ($failedAggregate.ExitCode -ne 0)
Assert-Condition (-not (Test-Path -LiteralPath $aggregateProofPath))
$afterAggregateWorktrees = @(Get-WorktreeList)
Assert-Condition (($beforeAggregateWorktrees -join "`n") -ceq ($afterAggregateWorktrees -join "`n"))
Assert-Condition (@($afterAggregateWorktrees | Where-Object { $_ -match "contract-scribe-m05-v1-aggregate-" }).Count -eq 0)

Write-Output "M0.4 security migration and historical V1 protection checks passed."
