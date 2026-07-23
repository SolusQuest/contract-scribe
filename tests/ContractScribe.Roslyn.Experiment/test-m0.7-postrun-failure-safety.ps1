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
Write-Output "M0.7 post-run failure-safety regression passed: later gate failure cannot publish success evidence or aggregate succeeded."
