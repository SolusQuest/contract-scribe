[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidenceRoot,
    [string]$OutputPath = "TestResults\m0.7-independent-validation\m0.7-aggregate-evidence.json"
)

$ErrorActionPreference = "Stop"

trap {
    $outcome = "protocol-failure"
    $reasonCode = "aggregate-validation-failure"
    if ($_.Exception.Message -match "two successful|required cell|incomplete") {
        $outcome = "inconclusive"
        $reasonCode = "required-cell-evidence-incomplete"
    }
    elseif ($_.Exception.Message -match "payload|fresh|canonical|byte") {
        $outcome = "baseline-failure"
        $reasonCode = "cross-cell-byte-mismatch"
    }
    elseif ($_.Exception.Message -match "baseline") {
        $outcome = "baseline-invalidated"
        $reasonCode = "aggregate-baseline-drift"
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
    $failure = [ordered]@{
        formatVersion = "contractscribe-m0.7-aggregate-evidence-v1"
        aggregateOutcome = $outcome
        reasonCode = $reasonCode
        evidenceGeneratingCommit = $env:GITHUB_SHA
        evidenceGeneratingRunUrl = if ($env:GITHUB_RUN_ID) { "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID" } else { $null }
        retainedFailure = $true
    }
    [IO.File]::WriteAllText($OutputPath, ($failure | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    Write-Output "M0.7 aggregate evidence failed: $outcome ($reasonCode)."
    exit 1
}

function Get-FileSha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-Condition([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

$evidenceFiles = @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File -Filter "m0.7-evidence.json")
$failureFiles = @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File -Filter "m0.7-failure-evidence.json")
$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

if ($failureFiles.Count -gt 0 -or $evidenceFiles.Count -ne 2) {
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
        reasonCode = if ($failureFiles.Count -gt 0) { "required-cell-failure" } else { "required-cell-evidence-incomplete" }
        evidenceGeneratingCommit = $env:GITHUB_SHA
        retainedFailure = $true
    }
    [IO.File]::WriteAllText($OutputPath, ($failure | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    throw "M0.7 aggregate evidence did not contain two successful required cells."
}

$cells = @($evidenceFiles | ForEach-Object {
    $document = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    Assert-Condition ($document.aggregateOutcome -eq "succeeded") "A required cell did not succeed."
    Assert-Condition ($document.comparison.crossRunEquality -and $document.comparison.oracleEquality) "A required cell did not prove byte equality."
    Assert-Condition (@($document.runs).Count -eq 2) "A required cell did not record exactly two fresh runs."
    $runHashes = @()
    foreach ($run in $document.runs) {
        $payloadPath = Join-Path $_.DirectoryName ("run-{0}\semantic-payload.json" -f $run.run)
        Assert-Condition (Test-Path -LiteralPath $payloadPath) "A required cell payload artifact is missing."
        $hash = Get-FileSha256 $payloadPath
        Assert-Condition ($hash -eq $run.payloadSha256) "A recorded payload hash does not match its artifact."
        $runHashes += $hash
    }
    Assert-Condition ($runHashes[0] -eq $runHashes[1]) "Fresh runs in a required cell are not byte-identical."
    [pscustomobject]@{
        runnerOs = $document.runnerOs
        rid = $document.rid
        selectedBaselineCommit = $document.selectedBaselineCommit
        protocolCommit = $document.protocolCommit
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

Assert-Condition (((@($cells.runnerOs) | Sort-Object) -join ",") -eq "Linux,Windows") "The required Linux and Windows cells are incomplete."
Assert-Condition (((@($cells.rid) | Sort-Object) -join ",") -eq "linux-x64,win-x64") "The required runtime RID cells are incomplete."
Assert-Condition ((@($cells.selectedBaselineCommit) | Select-Object -Unique).Count -eq 1) "Required cells used different selected baselines."
Assert-Condition ((@($cells.protocolCommit) | Select-Object -Unique).Count -eq 1) "Required cells used different protocol commits."
Assert-Condition ((@($cells.fixtureCommit) | Select-Object -Unique).Count -eq 1) "Required cells used different fixtures."
Assert-Condition ((@($cells.oracleSha256) | Select-Object -Unique).Count -eq 1) "Required cells used different oracles."
Assert-Condition ((@($cells.payloadSha256) | Select-Object -Unique).Count -eq 1) "Required cells produced different canonical payload bytes."
Assert-Condition (($cells | Where-Object { $_.processArchitecture -ne "X64" }).Count -eq 0) "A required cell was not X64."

$aggregate = [ordered]@{
    formatVersion = "contractscribe-m0.7-aggregate-evidence-v1"
    aggregateOutcome = "succeeded"
    evidenceGeneratingCommit = $env:GITHUB_SHA
    evidenceGeneratingRunUrl = if ($env:GITHUB_RUN_ID) { "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID" } else { $null }
    selectedBaselineCommit = $cells[0].selectedBaselineCommit
    protocolCommit = $cells[0].protocolCommit
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
