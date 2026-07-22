[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$testRoot = Join-Path $repositoryRoot "TestResults\m0.7-public-artifact"
$sourceRoot = Join-Path $testRoot "source"
$publicRoot = Join-Path $testRoot "public"
if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $sourceRoot "run-1") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $sourceRoot "run-2") -Force | Out-Null
Set-Content -LiteralPath (Join-Path $sourceRoot "m0.7-evidence.json") -Value "{}" -NoNewline
Set-Content -LiteralPath (Join-Path $sourceRoot "run-1\semantic-payload.json") -Value "{}" -NoNewline
Set-Content -LiteralPath (Join-Path $sourceRoot "run-2\semantic-payload.json") -Value "{}" -NoNewline
Set-Content -LiteralPath (Join-Path $sourceRoot "run-1\stdout.txt") -Value "Authorization: Bearer synthetic-test-token" -NoNewline
Set-Content -LiteralPath (Join-Path $sourceRoot "run-1\stderr.txt") -Value "synthetic stderr" -NoNewline
Set-Content -LiteralPath (Join-Path $sourceRoot "run-1\result.json") -Value "{}" -NoNewline

& pwsh -NoProfile -File (Join-Path $PSScriptRoot "prepare-m0.7-public-evidence.ps1") -SourceRoot $sourceRoot -PublicRoot $publicRoot
$actual = @(Get-ChildItem -LiteralPath $publicRoot -File -Recurse | ForEach-Object { $_.FullName.Substring($publicRoot.Length + 1).Replace("\", "/") } | Sort-Object)
$expected = @("m0.7-evidence.json", "run-1/semantic-payload.json", "run-2/semantic-payload.json") | Sort-Object
if ((($actual -join ",") -ne ($expected -join ","))) { throw "Public artifact regression published an unexpected file set." }
if (Get-ChildItem -LiteralPath $publicRoot -File -Recurse | Where-Object { $_.Name -in @("stdout.txt", "stderr.txt", "result.json") }) { throw "Public artifact regression published raw run output." }
Remove-Item -LiteralPath $testRoot -Recurse -Force
Write-Output "M0.7 public-artifact regression passed: only the explicit allowlist is publishable."
