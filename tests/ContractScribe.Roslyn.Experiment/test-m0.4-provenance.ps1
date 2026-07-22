[CmdletBinding()]
param(
    [string]$VerifierPath = (Join-Path $PSScriptRoot "verify-m0.4.ps1")
)

$ErrorActionPreference = "Stop"
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
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    Invoke-Git @("init", "-b", "main") | Out-Null
    Invoke-Git @("config", "user.email", "test@example.invalid") | Out-Null
    Invoke-Git @("config", "user.name", "ContractScribe test") | Out-Null

    New-Item -ItemType Directory -Path (Join-Path $testRoot "tests/ContractScribe.Roslyn") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $testRoot "tests/fixtures/roslyn-msbuild/v1/SampleApp") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $testRoot "docs/20_architecture/decisions") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $testRoot "tests/ContractScribe.Roslyn/Runner.cs") -Value "frozen runner" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "tests/fixtures/roslyn-msbuild/v1/SampleApp/App.cs") -Value "frozen fixture" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "docs/20_architecture/decisions/README.md") -Value "baseline docs" -NoNewline
    Invoke-Git @("add", ".") | Out-Null
    Invoke-Git @("commit", "-m", "freeze m04 semantic inputs") | Out-Null
    $frozenRevision = Invoke-Git @("rev-parse", "HEAD")

    $manifest = [ordered]@{
        sourceRevision = $frozenRevision
        fixtureContentSha256 = [ordered]@{}
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot "tests/fixtures/roslyn-msbuild/v1/transfer-manifest.json") -NoNewline
    Invoke-Git @("add", ".") | Out-Null
    Invoke-Git @("commit", "-m", "record m04 transfer metadata") | Out-Null
    Assert-Passed "baseline"

    Set-Content -LiteralPath (Join-Path $testRoot "docs/20_architecture/decisions/0001.md") -Value "ADR" -NoNewline
    Invoke-Git @("add", ".") | Out-Null
    Invoke-Git @("commit", "-m", "add documentation") | Out-Null
    Assert-Passed "documentation addition"

    Set-Content -LiteralPath (Join-Path $testRoot "tests/ContractScribe.Roslyn/Runner.cs") -Value "changed runner" -NoNewline
    Invoke-Git @("add", ".") | Out-Null
    Invoke-Git @("commit", "-m", "change runner") | Out-Null
    Assert-Rejected "runner drift"

    Invoke-Git @("restore", "--source", $frozenRevision, "--", "tests/ContractScribe.Roslyn/Runner.cs") | Out-Null
    Set-Content -LiteralPath (Join-Path $testRoot "tests/fixtures/roslyn-msbuild/v1/SampleApp/App.cs") -Value "changed fixture" -NoNewline
    Invoke-Git @("add", ".") | Out-Null
    Invoke-Git @("commit", "-m", "change fixture") | Out-Null
    Assert-Rejected "fixture drift"

    Invoke-Git @("restore", "--source", $frozenRevision, "--", "tests/fixtures/roslyn-msbuild/v1/SampleApp/App.cs") | Out-Null
    Set-Content -LiteralPath (Join-Path $testRoot "Directory.Build.targets") -Value "<Project />" -NoNewline
    Invoke-Git @("add", ".") | Out-Null
    Invoke-Git @("commit", "-m", "inject build target") | Out-Null
    Assert-Rejected "Directory.Build.targets injection"

    Write-Output "M0.4 provenance regression passed: documentation is accepted; runner, fixture, and build-injection drift are rejected."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
