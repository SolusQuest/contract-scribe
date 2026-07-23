[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidenceRoot,
    [string]$OutputPath = "TestResults\m0.7-independent-validation\m0.7-aggregate-evidence.json",
    [ValidateSet("success", "failure", "cancelled", "skipped")]
    [string]$ValidateResult = "success",
    [string]$ManifestPath = "tests\fixtures\roslyn-msbuild\m0.7-independent-validation-manifest.json",
    [string]$ExpectedPrHeadCommit,
    [string]$ExpectedValidationMergeCommit,
    [string]$ExpectedRunId
)

$ErrorActionPreference = "Stop"
$script:aggregateOutcome = "protocol-failure"
$script:aggregateReasonCode = "aggregate-validation-failure"

trap {
    New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
    $failure = [ordered]@{
        formatVersion = "contractscribe-m0.7-aggregate-evidence-v1"
        aggregateOutcome = $script:aggregateOutcome
        reasonCode = $script:aggregateReasonCode
        evidenceGeneratingCommit = $env:GITHUB_SHA
        evidenceGeneratingRunUrl = if ($env:GITHUB_RUN_ID) { "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID" } else { $null }
        retainedFailure = $true
    }
    [IO.File]::WriteAllText($OutputPath, ($failure | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    Write-Output "M0.7 aggregate evidence failed: $script:aggregateOutcome ($script:aggregateReasonCode)."
    exit 1
}

function Get-FileSha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-Condition([bool]$condition, [string]$message) {
    if (-not $condition) {
        $exception = [InvalidOperationException]::new($message)
        $exception.Data["M07Outcome"] = $script:aggregateOutcome
        $exception.Data["M07ReasonCode"] = $script:aggregateReasonCode
        throw $exception
    }
}

function Set-AggregateFailureContext([string]$outcome, [string]$reasonCode) {
    $script:aggregateOutcome = $outcome
    $script:aggregateReasonCode = $reasonCode
}

$evidenceFiles = @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File -Filter "m0.7-evidence.json")
$failureFiles = @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File -Filter "m0.7-failure-evidence.json")
$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
Set-AggregateFailureContext "protocol-failure" "aggregate-manifest-invalid"
Assert-Condition ($manifest.formatVersion -eq "contractscribe-m0.7-validation-v1") "The M0.7 validation manifest version is unsupported."
if ([string]::IsNullOrWhiteSpace($ExpectedValidationMergeCommit)) { $ExpectedValidationMergeCommit = $env:GITHUB_SHA }
if ([string]::IsNullOrWhiteSpace($ExpectedRunId)) { $ExpectedRunId = $env:GITHUB_RUN_ID }

if ($failureFiles.Count -gt 0 -or $ValidateResult -ne "success" -or $evidenceFiles.Count -ne 2) {
    Set-AggregateFailureContext "inconclusive" "required-cell-evidence-incomplete"
    $aggregateOutcome = "inconclusive"
    if ($failureFiles.Count -gt 0) {
        $failureOutcomes = @($failureFiles | ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).aggregateOutcome })
        if ($failureOutcomes -contains "baseline-invalidated") { $aggregateOutcome = "baseline-invalidated" }
        elseif ($failureOutcomes -contains "protocol-failure") { $aggregateOutcome = "protocol-failure" }
        elseif ($failureOutcomes -contains "baseline-failure") { $aggregateOutcome = "baseline-failure" }
    }
    $failure = [ordered]@{
        formatVersion = "contractscribe-m0.7-aggregate-evidence-v1"
        aggregateOutcome = $aggregateOutcome
        reasonCode = if ($failureFiles.Count -gt 0) { "required-cell-failure" } elseif ($ValidateResult -ne "success") { "required-cell-validation-incomplete" } else { "required-cell-evidence-incomplete" }
        evidenceGeneratingCommit = $env:GITHUB_SHA
        validateResult = $ValidateResult
        retainedFailure = $true
    }
    [IO.File]::WriteAllText($OutputPath, ($failure | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    exit 1
}

$cells = @($evidenceFiles | ForEach-Object {
    Set-AggregateFailureContext "protocol-failure" "aggregate-evidence-invalid"
    $document = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    Assert-Condition ($document.formatVersion -eq "contractscribe-m0.7-evidence-v1") "A required cell evidence format is unsupported."
    Assert-Condition ($document.aggregateOutcome -eq "succeeded") "A required cell did not succeed."
    Set-AggregateFailureContext "baseline-failure" "cross-cell-byte-mismatch"
    Assert-Condition ($document.comparison.crossRunEquality) "A required cell did not prove fresh-run byte equality."
    Set-AggregateFailureContext "protocol-failure" "aggregate-evidence-invalid"
    Assert-Condition ($document.comparison.oracleEquality) "A required cell did not prove oracle byte equality."
    Assert-Condition (@($document.runs).Count -eq 2) "A required cell did not record exactly two fresh runs."
    Set-AggregateFailureContext "baseline-invalidated" "aggregate-baseline-drift"
    Assert-Condition ($document.selectedBaselineCommit -eq $manifest.selectedBaseline.commit) "A required cell used a baseline outside the M0.7 manifest."
    Set-AggregateFailureContext "protocol-failure" "aggregate-provenance-mismatch"
    Assert-Condition ($document.fixtureCommit -eq $manifest.fixture.commit) "A required cell used a fixture outside the M0.7 manifest."
    Assert-Condition ($document.oracleSha256 -eq $manifest.fixture.oracleSha256) "A required cell used an oracle outside the M0.7 manifest."
    Assert-Condition ($document.executionPolicy.networkDependencyDeclared -eq $manifest.executionPolicy.networkDependencyDeclared) "A required cell used an unbound network-dependency policy."
    Assert-Condition ($document.executionPolicy.networkIsolationEnforced -eq $manifest.executionPolicy.networkIsolationEnforced) "A required cell used an unbound network-isolation policy."
    Assert-Condition (@($document.observedCommands).Count -eq 2) "A required cell did not record each host invocation."
    foreach ($invocation in $document.observedCommands) {
        Assert-Condition ($invocation.executable -eq "dotnet" -and $invocation.arguments.Count -eq 3 -and $invocation.workingDirectory -eq "repository") "A required cell recorded an invalid host invocation."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedPrHeadCommit)) {
        Assert-Condition ($document.protocolPrHeadCommit -eq $ExpectedPrHeadCommit) "A required cell was generated from a different PR head."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedValidationMergeCommit)) {
        Assert-Condition ($document.validationMergeCommit -eq $ExpectedValidationMergeCommit) "A required cell was generated from a different validation merge ref."
        Assert-Condition ($document.ci.sha -eq $ExpectedValidationMergeCommit) "A required cell CI SHA does not match the validation merge ref."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedRunId)) {
        Assert-Condition ($document.ci.runId -eq $ExpectedRunId) "A required cell belongs to a different CI run."
    }
    $runHashes = @()
    foreach ($run in $document.runs) {
        $payloadPath = Join-Path $_.DirectoryName ("run-{0}\semantic-payload.json" -f $run.run)
        Assert-Condition (Test-Path -LiteralPath $payloadPath) "A required cell payload artifact is missing."
        $hash = Get-FileSha256 $payloadPath
        Assert-Condition ($hash -eq $run.payloadSha256) "A recorded payload hash does not match its artifact."
        Set-AggregateFailureContext "baseline-failure" "oracle-mismatch"
        Assert-Condition ($hash -eq $manifest.fixture.oracleSha256) "A required cell payload does not match the pinned oracle."
        $runHashes += $hash
    }
    Set-AggregateFailureContext "baseline-failure" "fresh-process-nondeterminism"
    Assert-Condition ($runHashes[0] -eq $runHashes[1]) "Fresh runs in a required cell are not byte-identical."
    [pscustomobject]@{
        runnerOs = $document.runnerOs
        rid = $document.rid
        selectedBaselineCommit = $document.selectedBaselineCommit
        protocolCommit = $document.protocolCommit
        protocolPrHeadCommit = $document.protocolPrHeadCommit
        validationMergeCommit = $document.validationMergeCommit
        fixtureCommit = $document.fixtureCommit
        oracleSha256 = $document.oracleSha256
        payloadSha256 = $runHashes[0]
        sdkVersion = $document.runs[0].sdkVersion
        msbuildVersion = $document.runs[0].msbuildVersion
        runtimeVersion = $document.runs[0].runtimeVersion
        processArchitecture = $document.runs[0].processArchitecture
        ci = $document.ci
    }
})

Set-AggregateFailureContext "inconclusive" "required-cell-evidence-incomplete"
Assert-Condition (((@($cells.runnerOs) | Sort-Object) -join ",") -eq "Linux,Windows") "The required Linux and Windows cells are incomplete."
Assert-Condition (((@($cells.rid) | Sort-Object) -join ",") -eq "linux-x64,win-x64") "The required runtime RID cells are incomplete."
Set-AggregateFailureContext "baseline-invalidated" "aggregate-baseline-drift"
Assert-Condition ((@($cells.selectedBaselineCommit) | Select-Object -Unique).Count -eq 1) "Required cells used different selected baselines."
Set-AggregateFailureContext "protocol-failure" "aggregate-provenance-mismatch"
Assert-Condition ((@($cells.protocolCommit) | Select-Object -Unique).Count -eq 1) "Required cells used different protocol commits."
Assert-Condition ((@($cells.protocolPrHeadCommit) | Select-Object -Unique).Count -eq 1) "Required cells used different PR heads."
Assert-Condition ((@($cells.validationMergeCommit) | Select-Object -Unique).Count -eq 1) "Required cells used different validation merge refs."
Assert-Condition ((@($cells.fixtureCommit) | Select-Object -Unique).Count -eq 1) "Required cells used different fixtures."
Assert-Condition ((@($cells.oracleSha256) | Select-Object -Unique).Count -eq 1) "Required cells used different oracles."
Set-AggregateFailureContext "baseline-failure" "cross-cell-byte-mismatch"
Assert-Condition ((@($cells.payloadSha256) | Select-Object -Unique).Count -eq 1) "Required cells produced different canonical payload bytes."
Set-AggregateFailureContext "inconclusive" "required-cell-inconclusive"
Assert-Condition (($cells | Where-Object { $_.processArchitecture -ne "X64" }).Count -eq 0) "A required cell was not X64."

$aggregate = [ordered]@{
    formatVersion = "contractscribe-m0.7-aggregate-evidence-v1"
    aggregateOutcome = "succeeded"
    evidenceGeneratingCommit = $env:GITHUB_SHA
    evidenceGeneratingRunUrl = if ($env:GITHUB_RUN_ID) { "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID" } else { $null }
    validateResult = $ValidateResult
    selectedBaselineCommit = $cells[0].selectedBaselineCommit
    protocolCommit = $cells[0].protocolCommit
    protocolPrHeadCommit = $cells[0].protocolPrHeadCommit
    validationMergeCommit = $cells[0].validationMergeCommit
    fixtureCommit = $cells[0].fixtureCommit
    oracleSha256 = $cells[0].oracleSha256
    comparison = [ordered]@{ freshProcessCountPerCell = 2; requiredCellCount = 2; crossRunEquality = $true; crossCellEquality = $true; payloadSha256 = $cells[0].payloadSha256 }
    cells = $cells
    unresolvedRisks = @(
        "The selected baseline remains limited to the documented two-project synthetic shape and Ubuntu/Windows X64 framework-dependent matrix.",
        "Production process topology and distribution channel remain deferred to Issues #17 and #18."
    )
}
[IO.File]::WriteAllText($OutputPath, ($aggregate | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
Write-Output "M0.7 aggregate evidence succeeded: Linux and Windows payload bytes match across two fresh runs per cell."
