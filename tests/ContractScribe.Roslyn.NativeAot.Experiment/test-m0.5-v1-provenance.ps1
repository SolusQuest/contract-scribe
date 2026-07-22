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

$changedFiles = @(Invoke-Git @("diff", "--name-only", ($anchorRevision + "..HEAD")))
Assert-Condition (@($changedFiles | Where-Object { $allowed -notcontains $_ }).Count -eq 0)
foreach ($path in $inventory) {
    $expected = (Invoke-Git @("rev-parse", ($anchorRevision + ":" + $path))).Trim()
    $actual = (Invoke-Git @("hash-object", "--", $path)).Trim()
    Assert-Condition ($expected -eq $actual)
}

$packageDiff = (Invoke-Git @("diff", ($anchorRevision + "..HEAD"), "--", "Directory.Packages.props")) -join [Environment]::NewLine
Assert-Condition ($packageDiff -match "-\s*<PackageVersion Include=""System.Security.Cryptography.Xml"" Version=""9\.0\.15""")
Assert-Condition ($packageDiff -match "\+\s*<PackageVersion Include=""System.Security.Cryptography.Xml"" Version=""9\.0\.18""")
$packageVersionChanges = @($packageDiff -split "\r?\n" | Where-Object { $_ -match "^[+-]\s*<PackageVersion" })
Assert-Condition ($packageVersionChanges.Count -eq 2)

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

Write-Output "M0.4 security migration and historical V1 protection checks passed."
