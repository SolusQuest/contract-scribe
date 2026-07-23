[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$root = Join-Path $repositoryRoot "TestResults\m0.7-postrun-failure-safety"
$cellRoot = Join-Path $root "cell-1"
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $cellRoot "run-1") -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $cellRoot "m0.7-evidence.json"), (@{ aggregateOutcome = "succeeded" } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $cellRoot "run-1\stdout.txt"), "Authorization: Bearer synthetic-postrun-token", [Text.UTF8Encoding]::new($false))

& pwsh -NoProfile -File (Join-Path $PSScriptRoot "write-m0.7-postrun-failure-evidence.ps1") -OutputRoot $cellRoot -BaselineCommit "645c0946b8b811d633b471b232b0654c10e6d7f6"
$failurePath = Join-Path $cellRoot "m0.7-failure-evidence.json"
if (-not (Test-Path -LiteralPath $failurePath)) { throw "Post-run failure regression did not create bounded failure evidence." }
if (Test-Path -LiteralPath (Join-Path $cellRoot "run-1")) { throw "Post-run failure regression retained raw run output." }
if (Test-Path -LiteralPath (Join-Path $cellRoot "m0.7-evidence.json")) { throw "Post-run failure regression retained success evidence." }
if ((Get-Content -LiteralPath $failurePath -Raw) -match "Bearer|synthetic-postrun-token|stdout") { throw "Post-run failure regression leaked raw failure content." }

$aggregatePath = Join-Path $root "aggregate.json"
& pwsh -NoProfile -File (Join-Path $PSScriptRoot "aggregate-m0.7.ps1") -EvidenceRoot $root -OutputPath $aggregatePath 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) { throw "Post-run failure regression unexpectedly produced a successful aggregate." }
$aggregate = Get-Content -LiteralPath $aggregatePath -Raw | ConvertFrom-Json
if ($aggregate.aggregateOutcome -eq "succeeded") { throw "Post-run failure regression produced a false-success aggregate." }
Remove-Item -LiteralPath $root -Recurse -Force

$preVerifierRoot = Join-Path $repositoryRoot "TestResults\m0.7-pre-verifier-failure-safety"
if (Test-Path -LiteralPath $preVerifierRoot) { Remove-Item -LiteralPath $preVerifierRoot -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $preVerifierRoot "run-1") -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $preVerifierRoot "run-1\stdout.txt"), "synthetic infrastructure failure", [Text.UTF8Encoding]::new($false))
& pwsh -NoProfile -File (Join-Path $PSScriptRoot "write-m0.7-postrun-failure-evidence.ps1") -OutputRoot $preVerifierRoot -BaselineCommit "645c0946b8b811d633b471b232b0654c10e6d7f6"
$preVerifierFailure = Get-Content -LiteralPath (Join-Path $preVerifierRoot "m0.7-failure-evidence.json") -Raw | ConvertFrom-Json
if ($preVerifierFailure.aggregateOutcome -ne "inconclusive" -or $preVerifierFailure.reasonCode -ne "pre-verifier-validation-failure") { throw "Pre-verifier failure was not classified as inconclusive." }
if (Test-Path -LiteralPath (Join-Path $preVerifierRoot "run-1")) { throw "Pre-verifier failure regression retained raw run output." }
Remove-Item -LiteralPath $preVerifierRoot -Recurse -Force

$typedFailureRoot = Join-Path $repositoryRoot "TestResults\m0.7-typed-failure-safety"
if (Test-Path -LiteralPath $typedFailureRoot) { Remove-Item -LiteralPath $typedFailureRoot -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $typedFailureRoot "run-1") -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $typedFailureRoot "m0.7-failure-evidence.json"), (@{ formatVersion = "contractscribe-m0.7-failure-evidence-v1"; aggregateOutcome = "baseline-invalidated"; reasonCode = "selected-baseline-drift"; retainedFailure = $true } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $typedFailureRoot "run-1\stdout.txt"), "synthetic typed failure", [Text.UTF8Encoding]::new($false))
& pwsh -NoProfile -File (Join-Path $PSScriptRoot "write-m0.7-postrun-failure-evidence.ps1") -OutputRoot $typedFailureRoot -BaselineCommit "645c0946b8b811d633b471b232b0654c10e6d7f6"
$typedFailure = Get-Content -LiteralPath (Join-Path $typedFailureRoot "m0.7-failure-evidence.json") -Raw | ConvertFrom-Json
if ($typedFailure.aggregateOutcome -ne "baseline-invalidated" -or $typedFailure.reasonCode -ne "selected-baseline-drift") { throw "Typed failure evidence was not preserved." }
if (Test-Path -LiteralPath (Join-Path $typedFailureRoot "run-1")) { throw "Typed failure regression retained raw run output." }
Remove-Item -LiteralPath $typedFailureRoot -Recurse -Force
Write-Output "M0.7 post-run failure-safety regression passed: post-run, pre-verifier, and typed failures remain bounded and cannot aggregate succeeded."
