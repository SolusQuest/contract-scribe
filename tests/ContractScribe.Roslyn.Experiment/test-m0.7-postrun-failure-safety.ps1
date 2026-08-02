[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
. (Join-Path $PSScriptRoot "resolve-m0.7-terminal-identity.ps1")
$headCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$runnerOs = if ($IsWindows) { "Windows" } else { "Linux" }
$rid = if ($IsWindows) { "win-x64" } else { "linux-x64" }
$runId = "200"
$runAttempt = "1"
$identityArguments = @{
    EvidencePrHeadCommit = $headCommit
    EvidenceValidationMergeCommit = $headCommit
    EvidenceRunId = $runId
    EvidenceRunAttempt = $runAttempt
    EvidenceRunnerOs = $runnerOs
    EvidenceRid = $rid
}

function Assert-TerminalIdentity([object]$Document) {
    if ($Document.protocolPrHeadCommit -cne $headCommit -or $Document.validationMergeCommit -cne $headCommit -or $Document.protocolCommit -cne $headCommit) { throw "Failure evidence did not retain exact commit identity." }
    if ($Document.runnerOs -cne $runnerOs -or $Document.rid -cne $rid) { throw "Failure evidence did not retain the closed cell identity." }
    if ($Document.ci.runId -cne $runId -or $Document.ci.runAttempt -ne 1 -or $Document.ci.runAttempt -is [string] -or $Document.ci.sha -cne $headCommit) { throw "Failure evidence did not retain integer run-attempt identity." }
}

$root = Join-Path $repositoryRoot "TestResults\m0.7-postrun-failure-safety"
$cellRoot = Join-Path $root "cell-1"
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $cellRoot "run-1") -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $cellRoot "m0.7-evidence.json"), (@{ aggregateOutcome = "succeeded" } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $cellRoot "run-1\stdout.txt"), "Authorization: Bearer synthetic-postrun-token", [Text.UTF8Encoding]::new($false))

& pwsh -NoProfile -File (Join-Path $PSScriptRoot "write-m0.7-postrun-failure-evidence.ps1") -OutputRoot $cellRoot -BaselineCommit "645c0946b8b811d633b471b232b0654c10e6d7f6" @identityArguments
$failurePath = Join-Path $cellRoot "m0.7-failure-evidence.json"
if (-not (Test-Path -LiteralPath $failurePath)) { throw "Post-run failure regression did not create bounded failure evidence." }
if (Test-Path -LiteralPath (Join-Path $cellRoot "run-1")) { throw "Post-run failure regression retained raw run output." }
if (Test-Path -LiteralPath (Join-Path $cellRoot "m0.7-evidence.json")) { throw "Post-run failure regression retained success evidence." }
if ((Get-Content -LiteralPath $failurePath -Raw) -match "Bearer|synthetic-postrun-token|stdout") { throw "Post-run failure regression leaked raw failure content." }
$postRunFailure = Get-Content -LiteralPath $failurePath -Raw | ConvertFrom-Json
Assert-TerminalIdentity $postRunFailure

$aggregatePath = Join-Path $root "aggregate.json"
& pwsh -NoProfile -File (Join-Path $PSScriptRoot "aggregate-m0.7.ps1") -EvidenceRoot $root -OutputPath $aggregatePath -ExpectedPrHeadCommit $headCommit -ExpectedValidationMergeCommit $headCommit -ExpectedRunId $runId -ExpectedRunAttempt $runAttempt 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) { throw "Post-run failure regression unexpectedly produced a successful aggregate." }
$aggregate = Get-Content -LiteralPath $aggregatePath -Raw | ConvertFrom-Json
if ($aggregate.aggregateOutcome -eq "succeeded" -or $aggregate.reasonCode -ne "required-cell-evidence-incomplete") { throw "Post-run failure regression produced an invalid missing-cell aggregate." }
Remove-Item -LiteralPath $root -Recurse -Force

$preVerifierRoot = Join-Path $repositoryRoot "TestResults\m0.7-pre-verifier-failure-safety"
if (Test-Path -LiteralPath $preVerifierRoot) { Remove-Item -LiteralPath $preVerifierRoot -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $preVerifierRoot "run-1") -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $preVerifierRoot "run-1\stdout.txt"), "synthetic infrastructure failure", [Text.UTF8Encoding]::new($false))
& pwsh -NoProfile -File (Join-Path $PSScriptRoot "write-m0.7-postrun-failure-evidence.ps1") -OutputRoot $preVerifierRoot -BaselineCommit "645c0946b8b811d633b471b232b0654c10e6d7f6" @identityArguments
$preVerifierFailure = Get-Content -LiteralPath (Join-Path $preVerifierRoot "m0.7-failure-evidence.json") -Raw | ConvertFrom-Json
if ($preVerifierFailure.aggregateOutcome -ne "inconclusive" -or $preVerifierFailure.reasonCode -ne "pre-verifier-validation-failure") { throw "Pre-verifier failure was not classified as inconclusive." }
Assert-TerminalIdentity $preVerifierFailure
if (Test-Path -LiteralPath (Join-Path $preVerifierRoot "run-1")) { throw "Pre-verifier failure regression retained raw run output." }
Remove-Item -LiteralPath $preVerifierRoot -Recurse -Force

$typedFailureRoot = Join-Path $repositoryRoot "TestResults\m0.7-typed-failure-safety"
if (Test-Path -LiteralPath $typedFailureRoot) { Remove-Item -LiteralPath $typedFailureRoot -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $typedFailureRoot "run-1") -Force | Out-Null
$manifest = Get-Content -LiteralPath (Join-Path $repositoryRoot "tests\fixtures\roslyn-msbuild\m0.7-independent-validation-manifest.json") -Raw | ConvertFrom-Json
$typedFailureDocument = [ordered]@{
    formatVersion = "contractscribe-m0.7-failure-evidence-v1"
    aggregateOutcome = "baseline-invalidated"
    reasonCode = "selected-baseline-drift"
    protocolPrHeadCommit = $headCommit
    validationMergeCommit = $headCommit
    runnerOs = $runnerOs
    rid = $rid
    selectedBaselineCommit = $manifest.selectedBaseline.commit
    fixtureCommit = $manifest.fixture.commit
    oracleSha256 = $manifest.fixture.oracleSha256
    protocolCommit = $headCommit
    ci = [ordered]@{ runId = $runId; runAttempt = 1; sha = $headCommit }
    retainedFailure = $true
}
[IO.File]::WriteAllText((Join-Path $typedFailureRoot "m0.7-failure-evidence.json"), ($typedFailureDocument | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $typedFailureRoot "run-1\stdout.txt"), "synthetic typed failure", [Text.UTF8Encoding]::new($false))
& pwsh -NoProfile -File (Join-Path $PSScriptRoot "write-m0.7-postrun-failure-evidence.ps1") -OutputRoot $typedFailureRoot -BaselineCommit "645c0946b8b811d633b471b232b0654c10e6d7f6" @identityArguments
$typedFailure = Get-Content -LiteralPath (Join-Path $typedFailureRoot "m0.7-failure-evidence.json") -Raw | ConvertFrom-Json
if ($typedFailure.aggregateOutcome -ne "baseline-invalidated" -or $typedFailure.reasonCode -ne "selected-baseline-drift") { throw "Typed failure evidence was not preserved." }
Assert-TerminalIdentity $typedFailure
if (Test-Path -LiteralPath (Join-Path $typedFailureRoot "run-1")) { throw "Typed failure regression retained raw run output." }
Remove-Item -LiteralPath $typedFailureRoot -Recurse -Force

$invalidIdentityRoot = Join-Path $repositoryRoot "TestResults\m0.7-invalid-terminal-identity"
if (Test-Path -LiteralPath $invalidIdentityRoot) { Remove-Item -LiteralPath $invalidIdentityRoot -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $invalidIdentityRoot "run-1") -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $invalidIdentityRoot "run-1\stdout.txt"), "synthetic invalid identity", [Text.UTF8Encoding]::new($false))
$invalidIdentityArguments = $identityArguments.Clone()
$invalidIdentityArguments["EvidenceRunAttempt"] = "1.0"
& pwsh -NoProfile -File (Join-Path $PSScriptRoot "write-m0.7-postrun-failure-evidence.ps1") -OutputRoot $invalidIdentityRoot -BaselineCommit "645c0946b8b811d633b471b232b0654c10e6d7f6" @invalidIdentityArguments
$invalidIdentityFailure = Get-Content -LiteralPath (Join-Path $invalidIdentityRoot "m0.7-failure-evidence.json") -Raw | ConvertFrom-Json
if ($invalidIdentityFailure.aggregateOutcome -ne "protocol-failure" -or $invalidIdentityFailure.reasonCode -ne "evidence-identity-invalid" -or $null -ne $invalidIdentityFailure.ci.runAttempt) { throw "Invalid terminal identity did not produce bounded non-fabricated protocol failure." }
if (Test-Path -LiteralPath (Join-Path $invalidIdentityRoot "run-1")) { throw "Invalid terminal identity retained raw run output." }
Remove-Item -LiteralPath $invalidIdentityRoot -Recurse -Force

$eventRoot = Join-Path $repositoryRoot "TestResults\m0.7-identity-event-vectors"
if (Test-Path -LiteralPath $eventRoot) { Remove-Item -LiteralPath $eventRoot -Recurse -Force }
New-Item -ItemType Directory -Path $eventRoot | Out-Null
$pullHead = "1111111111111111111111111111111111111111"
$dispatchHead = "2222222222222222222222222222222222222222"
$pushHead = "3333333333333333333333333333333333333333"
$mergeHead = "4444444444444444444444444444444444444444"
$pullEventPath = Join-Path $eventRoot "pull-request.json"
$dispatchEventPath = Join-Path $eventRoot "workflow-dispatch.json"
$pushEventPath = Join-Path $eventRoot "push.json"
[IO.File]::WriteAllText($pullEventPath, (@{ pull_request = @{ head = @{ sha = $pullHead } } } | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($dispatchEventPath, (@{ inputs = @{ pr_head_sha = $dispatchHead } } | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($pushEventPath, (@{ after = $pushHead } | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

$savedPrHead = $env:M07_PR_HEAD_SHA
$savedMerge = $env:M07_VALIDATION_MERGE_SHA
$savedSha = $env:GITHUB_SHA
try {
    Remove-Item Env:M07_PR_HEAD_SHA -ErrorAction SilentlyContinue
    Remove-Item Env:M07_VALIDATION_MERGE_SHA -ErrorAction SilentlyContinue
    $pullIdentity = Resolve-M07TerminalIdentity -ValidationMergeCommit $mergeHead -RunId "300" -RunAttempt "2" -RunnerOs "Linux" -Rid "linux-x64" -EventPath $pullEventPath
    if ($pullIdentity.protocolPrHeadCommit -cne $pullHead) { throw "pull_request event identity was not resolved." }
    $dispatchIdentity = Resolve-M07TerminalIdentity -ValidationMergeCommit $mergeHead -RunId "300" -RunAttempt "2" -RunnerOs "Windows" -Rid "win-x64" -EventPath $dispatchEventPath
    if ($dispatchIdentity.protocolPrHeadCommit -cne $dispatchHead) { throw "workflow_dispatch input identity was not resolved." }
    $env:GITHUB_SHA = $pushHead
    $pushIdentity = Resolve-M07TerminalIdentity -RunId "300" -RunAttempt "2" -RunnerOs "Linux" -Rid "linux-x64" -EventPath $pushEventPath
    if ($pushIdentity.protocolPrHeadCommit -cne $pushHead -or $pushIdentity.validationMergeCommit -cne $pushHead) { throw "push event fallback identity was not resolved." }

    $env:M07_PR_HEAD_SHA = $pullHead
    $precedenceIdentity = Resolve-M07TerminalIdentity -ValidationMergeCommit $mergeHead -RunId "300" -RunAttempt "2" -RunnerOs "Linux" -Rid "linux-x64" -EventPath $dispatchEventPath
    if ($precedenceIdentity.protocolPrHeadCommit -cne $pullHead) { throw "M07_PR_HEAD_SHA did not take precedence over event inputs." }

    foreach ($invalidArguments in @(
        @{ PrHeadCommit = "ABCDEF"; ValidationMergeCommit = $mergeHead; RunId = "300"; RunAttempt = "2"; RunnerOs = "Linux"; Rid = "linux-x64" },
        @{ PrHeadCommit = $pullHead; ValidationMergeCommit = $mergeHead; RunId = "300"; RunAttempt = "1.0"; RunnerOs = "Linux"; Rid = "linux-x64" },
        @{ PrHeadCommit = $pullHead; ValidationMergeCommit = $mergeHead; RunId = "300"; RunAttempt = "2"; RunnerOs = "macOS"; Rid = "linux-x64" },
        @{ PrHeadCommit = $pullHead; ValidationMergeCommit = $mergeHead; RunId = "300"; RunAttempt = "2"; RunnerOs = "windows"; Rid = "win-x64" },
        @{ PrHeadCommit = $pullHead; ValidationMergeCommit = $mergeHead; RunId = "300"; RunAttempt = "2"; RunnerOs = "LINUX"; Rid = "linux-x64" },
        @{ PrHeadCommit = $pullHead; ValidationMergeCommit = $mergeHead; RunId = "300"; RunAttempt = "2"; RunnerOs = "Linux"; Rid = "win-x64" }
    )) {
        $threw = $false
        try { Resolve-M07TerminalIdentity @invalidArguments | Out-Null } catch { $threw = $true }
        if (-not $threw) { throw "Invalid terminal identity input was accepted." }
    }
}
finally {
    if ($null -eq $savedPrHead) { Remove-Item Env:M07_PR_HEAD_SHA -ErrorAction SilentlyContinue } else { $env:M07_PR_HEAD_SHA = $savedPrHead }
    if ($null -eq $savedMerge) { Remove-Item Env:M07_VALIDATION_MERGE_SHA -ErrorAction SilentlyContinue } else { $env:M07_VALIDATION_MERGE_SHA = $savedMerge }
    if ($null -eq $savedSha) { Remove-Item Env:GITHUB_SHA -ErrorAction SilentlyContinue } else { $env:GITHUB_SHA = $savedSha }
    Remove-Item -LiteralPath $eventRoot -Recurse -Force
}

Write-Output "M0.7 post-run failure-safety regression passed: bounded terminal identity, event fallback, raw cleanup, and typed-failure preservation are retained."
