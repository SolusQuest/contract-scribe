[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$VerifierPath
)

$ErrorActionPreference = "Stop"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("contract-scribe-m05-provenance-" + [Guid]::NewGuid().ToString("N"))

function Invoke-Git([string[]]$arguments) {
    $output = & git -C $testRoot @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($arguments -join ' ') failed: $($output -join "`n")"
    }
    return ($output -join "`n").Trim()
}

function Invoke-Provenance([string[]]$allowedFiles) {
    $output = & pwsh -NoProfile -File $VerifierPath `
        -RepositoryRoot $testRoot `
        -FrozenSourceRevision $frozenRevision `
        -CurrentRevision $currentRevision `
        -AllowedFiles ($allowedFiles -join "|") 2>&1
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
}

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    Invoke-Git @("init", "-b", "main") | Out-Null
    Invoke-Git @("config", "user.email", "test@example.invalid") | Out-Null
    Invoke-Git @("config", "user.name", "ContractScribe test") | Out-Null

    Set-Content -LiteralPath (Join-Path $testRoot "baseline.txt") -Value "frozen baseline" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "m04-ci.yml") -Value "m04 metadata" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "m04-verify.ps1") -Value "m04 verifier" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "m04-transfer.json") -Value "m04 transfer" -NoNewline
    Invoke-Git @("add", ".") | Out-Null
    Invoke-Git @("commit", "-m", "freeze m04") | Out-Null
    $frozenRevision = Invoke-Git @("rev-parse", "HEAD")

    Invoke-Git @("checkout", "--orphan", "squashed") | Out-Null
    Get-ChildItem -LiteralPath $testRoot -Force | Where-Object { $_.Name -ne ".git" } | Remove-Item -Recurse -Force
    Set-Content -LiteralPath (Join-Path $testRoot "baseline.txt") -Value "frozen baseline" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "m04-ci.yml") -Value "m04 metadata" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "m04-verify.ps1") -Value "m04 verifier" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "m04-transfer.json") -Value "m04 transfer" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "m05-verifier.ps1") -Value "m05 verifier" -NoNewline
    Set-Content -LiteralPath (Join-Path $testRoot "m05-manifest.json") -Value "m05 manifest" -NoNewline
    Invoke-Git @("add", ".") | Out-Null
    Invoke-Git @("commit", "-m", "squash m05") | Out-Null
    $currentRevision = Invoke-Git @("rev-parse", "HEAD")
    $allowedFiles = @(
        "m04-ci.yml",
        "m04-verify.ps1",
        "m04-transfer.json",
        "m05-verifier.ps1",
        "m05-manifest.json"
    )

    $result = Invoke-Provenance $allowedFiles
    if ($result.ExitCode -ne 0) { throw "The non-ancestral squash graph was rejected: $($result.Output)" }

    Set-Content -LiteralPath (Join-Path $testRoot "unexpected.txt") -Value "not allowed" -NoNewline
    Invoke-Git @("add", ".") | Out-Null
    Invoke-Git @("commit", "--amend", "--no-edit") | Out-Null
    $currentRevision = Invoke-Git @("rev-parse", "HEAD")
    $rejected = Invoke-Provenance $allowedFiles
    if ($rejected.ExitCode -eq 0) { throw "The provenance verifier accepted an out-of-allowlist tree change." }

    Write-Output "M0.5 provenance regression passed: non-ancestral squash accepted and out-of-allowlist change rejected."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
