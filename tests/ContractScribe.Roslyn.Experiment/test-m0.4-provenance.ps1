[CmdletBinding()]
param(
    [string]$VerifierPath = (Join-Path $PSScriptRoot "verify-m0.4.ps1")
)

$ErrorActionPreference = "Stop"
$sourceRepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("contract-scribe-m04-provenance-" + [Guid]::NewGuid().ToString("N"))

function Invoke-Git([string[]]$arguments) {
    $output = & git -C $testRoot @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($arguments -join ' ') failed: $($output -join "`n")"
    }
    return ($output -join "`n").Trim()
}

function Invoke-Provenance {
    $output = & pwsh -NoProfile -File $VerifierPath -RepositoryRoot $testRoot -ProvenanceOnly 2>&1
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
}

function Assert-Passed([string]$caseName) {
    $result = Invoke-Provenance
    if ($result.ExitCode -ne 0) {
        throw "$caseName should pass provenance validation: $($result.Output)"
    }
}

function Assert-Rejected([string]$caseName) {
    $result = Invoke-Provenance
    if ($result.ExitCode -eq 0) {
        throw "$caseName should be rejected by provenance validation."
    }
}

try {
    & git clone --no-local --no-hardlinks $sourceRepositoryRoot $testRoot 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "The provenance regression repository clone failed."
    }
    Invoke-Git @("config", "user.email", "test@example.invalid") | Out-Null
    Invoke-Git @("config", "user.name", "ContractScribe provenance test") | Out-Null
    $baselineRevision = Invoke-Git @("rev-parse", "HEAD")
    Assert-Passed "baseline"

    Set-Content -LiteralPath (Join-Path $testRoot "docs/20_architecture/decisions/0001.md") -Value "ADR" -NoNewline
    Invoke-Git @("add", ".") | Out-Null
    Invoke-Git @("commit", "-m", "add documentation") | Out-Null
    Assert-Passed "documentation addition"

    Set-Content -LiteralPath (Join-Path $testRoot "tests/ContractScribe.Roslyn/Runner.cs") -Value "changed runner" -NoNewline
    Invoke-Git @("add", ".") | Out-Null
    Invoke-Git @("commit", "-m", "change runner") | Out-Null
    Assert-Rejected "runner drift"

    Invoke-Git @("restore", "--source", $baselineRevision, "--", "tests/ContractScribe.Roslyn/Runner.cs") | Out-Null
    Set-Content -LiteralPath (Join-Path $testRoot "tests/fixtures/roslyn-msbuild/v1/SampleApp/App.cs") -Value "changed fixture" -NoNewline
    Invoke-Git @("add", ".") | Out-Null
    Invoke-Git @("commit", "-m", "change fixture") | Out-Null
    Assert-Rejected "fixture drift"

    Invoke-Git @("restore", "--source", $baselineRevision, "--", "tests/fixtures/roslyn-msbuild/v1/SampleApp/App.cs") | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $testRoot "tests") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $testRoot "tests/fixtures") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $testRoot "tests/fixtures/roslyn-msbuild") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $testRoot "tests/fixtures/roslyn-msbuild/v1") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $testRoot "tests/Directory.Build.targets") -Value "<Project />" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "tests/Directory.Packages.props") -Value "<Project />" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "tests/fixtures/Directory.Build.targets") -Value "<Project />" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "tests/fixtures/Directory.Packages.props") -Value "<Project />" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "tests/fixtures/roslyn-msbuild/Directory.Build.targets") -Value "<Project />" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "tests/fixtures/roslyn-msbuild/Directory.Packages.props") -Value "<Project />" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "tests/fixtures/roslyn-msbuild/v1/Directory.Build.targets") -Value "<Project />" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "tests/fixtures/roslyn-msbuild/v1/Directory.Packages.props") -Value "<Project />" -NoNewline
    Invoke-Git @("add", ".") | Out-Null
    Invoke-Git @("commit", "-m", "inject ancestor build inputs") | Out-Null
    Assert-Rejected "nested build and package injection"

    Remove-Item -LiteralPath (Join-Path $testRoot "tests/Directory.Build.targets"),(Join-Path $testRoot "tests/Directory.Packages.props"),(Join-Path $testRoot "tests/fixtures/Directory.Build.targets"),(Join-Path $testRoot "tests/fixtures/Directory.Packages.props"),(Join-Path $testRoot "tests/fixtures/roslyn-msbuild/Directory.Build.targets"),(Join-Path $testRoot "tests/fixtures/roslyn-msbuild/Directory.Packages.props"),(Join-Path $testRoot "tests/fixtures/roslyn-msbuild/v1/Directory.Build.targets"),(Join-Path $testRoot "tests/fixtures/roslyn-msbuild/v1/Directory.Packages.props") -Force
    $manifestPath = Join-Path $testRoot "tests/fixtures/roslyn-msbuild/v1/transfer-manifest.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifest.sourceRevision = $baselineRevision
    $manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifestPath -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "tests/ContractScribe.Roslyn/Runner.cs") -Value "changed runner" -NoNewline
    Invoke-Git @("add", "-A") | Out-Null
    Invoke-Git @("commit", "-m", "rewrite provenance with runner drift") | Out-Null
    Assert-Rejected "runner drift with rewritten transfer manifest"

    Write-Output "M0.4 provenance regression passed: documentation is accepted; runner, fixture, manifest, and ancestor build/package drift are rejected."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
