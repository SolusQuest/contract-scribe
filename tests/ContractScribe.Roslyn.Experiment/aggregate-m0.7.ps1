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
    [string]$ExpectedRunId,
    [string]$ExpectedRunAttempt
)

$ErrorActionPreference = "Stop"
$script:aggregateOutcome = "protocol-failure"
$script:aggregateReasonCode = "aggregate-validation-failure"
$script:currentRunId = $null
$script:currentRunAttempt = $null
$script:cellSelections = $null

function Set-AggregateFailureContext([string]$Outcome, [string]$ReasonCode) {
    $script:aggregateOutcome = $Outcome
    $script:aggregateReasonCode = $ReasonCode
}

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        $exception = [InvalidOperationException]::new($Message)
        $exception.Data["M07Outcome"] = $script:aggregateOutcome
        $exception.Data["M07ReasonCode"] = $script:aggregateReasonCode
        throw $exception
    }
}

function Test-CommitIdentity([object]$Value) {
    return $null -ne $Value -and ([string]$Value) -cmatch "^[0-9a-f]{40}$"
}

function ConvertFrom-TextPositiveInteger([object]$Value, [string]$FieldName) {
    $text = if ($null -eq $Value) { "" } else { [string]$Value }
    Assert-Condition ($text -cmatch "^[1-9][0-9]*$") "$FieldName must be a positive integer."
    $parsed = 0L
    Assert-Condition ([long]::TryParse($text, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) "$FieldName is outside the supported integer range."
    return $parsed
}

function ConvertFrom-JsonPositiveInteger([object]$Value, [string]$FieldName) {
    Assert-Condition ($null -ne $Value) "$FieldName is missing."
    $supportedTypes = @(
        [byte].FullName,
        [sbyte].FullName,
        [short].FullName,
        [ushort].FullName,
        [int].FullName,
        [uint].FullName,
        [long].FullName,
        [ulong].FullName
    )
    Assert-Condition ($Value.GetType().FullName -in $supportedTypes) "$FieldName must be a JSON integer."
    Assert-Condition ([decimal]$Value -ge 1 -and [decimal]$Value -le [long]::MaxValue) "$FieldName must be a positive supported integer."
    return [long]$Value
}

function Get-FileSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-AggregateFailure([string]$Outcome, [string]$ReasonCode, [object[]]$Selections = $null) {
    $outputDirectory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    $failure = [ordered]@{
        formatVersion = "contractscribe-m0.7-aggregate-evidence-v1"
        aggregateOutcome = $Outcome
        reasonCode = $ReasonCode
        evidenceGeneratingCommit = if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_SHA)) { $env:GITHUB_SHA } else { $ExpectedValidationMergeCommit }
        evidenceGeneratingRunId = $script:currentRunId
        evidenceGeneratingRunAttempt = $script:currentRunAttempt
        evidenceGeneratingRunUrl = if (-not [string]::IsNullOrWhiteSpace($script:currentRunId) -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_SERVER_URL) -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) { "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$script:currentRunId" } else { $null }
        validateResult = $ValidateResult
        retainedFailure = $true
    }
    if ($null -ne $Selections) {
        $failure["cellSelections"] = @($Selections)
    }
    [IO.File]::WriteAllText($OutputPath, ($failure | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}

trap {
    $exception = $_.Exception
    $outcome = if ($exception.Data.Contains("M07Outcome")) { [string]$exception.Data["M07Outcome"] } else { $script:aggregateOutcome }
    $reasonCode = if ($exception.Data.Contains("M07ReasonCode")) { [string]$exception.Data["M07ReasonCode"] } else { $script:aggregateReasonCode }
    Write-AggregateFailure $outcome $reasonCode $script:cellSelections
    Write-Output "M0.7 aggregate evidence failed: $outcome ($reasonCode)."
    exit 1
}

Set-AggregateFailureContext "protocol-failure" "aggregate-evidence-invalid"
if ([string]::IsNullOrWhiteSpace($ExpectedRunId)) { $ExpectedRunId = $env:GITHUB_RUN_ID }
if ([string]::IsNullOrWhiteSpace($ExpectedRunAttempt)) { $ExpectedRunAttempt = $env:GITHUB_RUN_ATTEMPT }
Assert-Condition ($ExpectedRunId -cmatch "^[1-9][0-9]*$") "The aggregate invocation run ID must be a non-empty decimal string."
$script:currentRunId = $ExpectedRunId
$script:currentRunAttempt = ConvertFrom-TextPositiveInteger $ExpectedRunAttempt "The aggregate invocation run attempt"
if ([string]::IsNullOrWhiteSpace($ExpectedValidationMergeCommit)) { $ExpectedValidationMergeCommit = $env:GITHUB_SHA }
if (-not [string]::IsNullOrWhiteSpace($ExpectedPrHeadCommit)) { Assert-Condition (Test-CommitIdentity $ExpectedPrHeadCommit) "The expected PR head is invalid." }
if (-not [string]::IsNullOrWhiteSpace($ExpectedValidationMergeCommit)) { Assert-Condition (Test-CommitIdentity $ExpectedValidationMergeCommit) "The expected validation merge is invalid." }

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

Set-AggregateFailureContext "protocol-failure" "aggregate-manifest-invalid"
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
Assert-Condition ($manifest.formatVersion -eq "contractscribe-m0.7-validation-v1") "The M0.7 validation manifest version is unsupported."

$terminalFiles = @()
$terminalFiles += @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File -Filter "m0.7-evidence.json" | ForEach-Object { [pscustomobject]@{ kind = "success"; file = $_ } })
$terminalFiles += @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File -Filter "m0.7-failure-evidence.json" | ForEach-Object { [pscustomobject]@{ kind = "failure"; file = $_ } })

$records = @($terminalFiles | ForEach-Object {
    $terminal = $_
    Set-AggregateFailureContext "protocol-failure" "aggregate-evidence-invalid"
    try {
        $document = Get-Content -LiteralPath $terminal.file.FullName -Raw | ConvertFrom-Json
    }
    catch {
        Assert-Condition $false "A terminal evidence document is not valid JSON."
    }

    if ($terminal.kind -eq "success") {
        Assert-Condition ($document.formatVersion -ceq "contractscribe-m0.7-evidence-v1") "A success evidence format is unsupported."
        Assert-Condition ($document.aggregateOutcome -ceq "succeeded") "A success evidence document has an invalid outcome."
    }
    else {
        Assert-Condition ($document.formatVersion -ceq "contractscribe-m0.7-failure-evidence-v1") "A failure evidence format is unsupported."
        Assert-Condition ($document.aggregateOutcome -in @("baseline-invalidated", "protocol-failure", "baseline-failure", "inconclusive")) "A failure evidence document has an invalid outcome."
        Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$document.reasonCode)) "A failure evidence document has no bounded reason code."
        Assert-Condition ($document.retainedFailure -eq $true) "A failure evidence document is not retained failure evidence."
    }

    Assert-Condition ($null -ne $document.ci) "A terminal evidence document has no CI identity."
    Assert-Condition ($document.ci.runId -is [string] -and $document.ci.runId -cmatch "^[1-9][0-9]*$") "A terminal evidence run ID is invalid."
    $runAttempt = ConvertFrom-JsonPositiveInteger $document.ci.runAttempt "A terminal evidence run attempt"
    Assert-Condition ($runAttempt -le $script:currentRunAttempt) "A terminal evidence record claims a future run attempt."
    Assert-Condition ($document.runnerOs -in @("Linux", "Windows")) "A terminal evidence runner OS is unsupported."
    $expectedRid = if ($document.runnerOs -ceq "Linux") { "linux-x64" } else { "win-x64" }
    Assert-Condition ($document.rid -ceq $expectedRid) "A terminal evidence runner OS and RID are cross-wired."
    Assert-Condition (Test-CommitIdentity $document.protocolPrHeadCommit) "A terminal evidence PR head is invalid."
    Assert-Condition (Test-CommitIdentity $document.validationMergeCommit) "A terminal evidence validation merge is invalid."
    Assert-Condition (Test-CommitIdentity $document.protocolCommit) "A terminal evidence protocol revision is invalid."
    Assert-Condition (Test-CommitIdentity $document.selectedBaselineCommit) "A terminal evidence selected baseline is invalid."
    Assert-Condition (Test-CommitIdentity $document.ci.sha) "A terminal evidence CI SHA is invalid."
    Assert-Condition ($document.ci.sha -ceq $document.validationMergeCommit) "A terminal evidence CI SHA contradicts its validation merge."

    Set-AggregateFailureContext "protocol-failure" "aggregate-provenance-mismatch"
    Assert-Condition ($document.ci.runId -ceq $script:currentRunId) "A terminal evidence document belongs to a different workflow run."
    Assert-Condition ($document.protocolCommit -ceq $document.validationMergeCommit) "A terminal evidence protocol revision contradicts its validation merge."
    if (-not [string]::IsNullOrWhiteSpace($ExpectedPrHeadCommit)) { Assert-Condition ($document.protocolPrHeadCommit -ceq $ExpectedPrHeadCommit) "A terminal evidence document belongs to a different PR head." }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedValidationMergeCommit)) { Assert-Condition ($document.validationMergeCommit -ceq $ExpectedValidationMergeCommit) "A terminal evidence document belongs to a different validation merge." }

    Set-AggregateFailureContext "baseline-invalidated" "aggregate-baseline-drift"
    Assert-Condition ($document.selectedBaselineCommit -ceq $manifest.selectedBaseline.commit) "A terminal evidence document used a baseline outside the M0.7 manifest."

    Set-AggregateFailureContext "protocol-failure" "aggregate-provenance-mismatch"
    if ($null -ne $document.fixtureCommit -and -not [string]::IsNullOrWhiteSpace([string]$document.fixtureCommit)) {
        Assert-Condition (Test-CommitIdentity $document.fixtureCommit) "A terminal evidence fixture commit is invalid."
        Assert-Condition ($document.fixtureCommit -ceq $manifest.fixture.commit) "A terminal evidence document used a fixture outside the M0.7 manifest."
    }
    if ($null -ne $document.oracleSha256 -and -not [string]::IsNullOrWhiteSpace([string]$document.oracleSha256)) {
        Assert-Condition ([string]$document.oracleSha256 -cmatch "^[0-9a-f]{64}$") "A terminal evidence oracle digest is invalid."
        Assert-Condition ($document.oracleSha256 -ceq $manifest.fixture.oracleSha256) "A terminal evidence document used an oracle outside the M0.7 manifest."
    }
    if ($terminal.kind -eq "success") {
        Assert-Condition ($document.fixtureCommit -ceq $manifest.fixture.commit) "A success evidence document has no bound fixture."
        Assert-Condition ($document.oracleSha256 -ceq $manifest.fixture.oracleSha256) "A success evidence document has no bound oracle."
    }

    [pscustomobject]@{
        kind = $terminal.kind
        file = $terminal.file
        document = $document
        runId = [string]$document.ci.runId
        runAttempt = $runAttempt
        runnerOs = [string]$document.runnerOs
        rid = [string]$document.rid
        protocolPrHeadCommit = [string]$document.protocolPrHeadCommit
        validationMergeCommit = [string]$document.validationMergeCommit
        protocolCommit = [string]$document.protocolCommit
        selectedBaselineCommit = [string]$document.selectedBaselineCommit
        fixtureCommit = if ($null -eq $document.fixtureCommit) { $null } else { [string]$document.fixtureCommit }
        oracleSha256 = if ($null -eq $document.oracleSha256) { $null } else { [string]$document.oracleSha256 }
        key = "{0}|{1}|{2}|{3}" -f $document.ci.runId, $document.runnerOs, $document.rid, $runAttempt
    }
})

Set-AggregateFailureContext "protocol-failure" "aggregate-evidence-invalid"
$duplicateGroups = @($records | Group-Object key | Where-Object { $_.Count -gt 1 })
Assert-Condition ($duplicateGroups.Count -eq 0) "More than one terminal record was discovered for the same run, cell, and attempt."

Set-AggregateFailureContext "protocol-failure" "aggregate-provenance-mismatch"
Assert-Condition ((@($records.protocolCommit) | Select-Object -Unique).Count -le 1) "Discovered terminal records use different protocol revisions."

$requiredCells = @(
    [pscustomobject]@{ runnerOs = "Linux"; rid = "linux-x64" },
    [pscustomobject]@{ runnerOs = "Windows"; rid = "win-x64" }
)
$selectedRecords = @()
$selectionComplete = $true
foreach ($requiredCell in $requiredCells) {
    $cellRecords = @($records | Where-Object { $_.runnerOs -ceq $requiredCell.runnerOs -and $_.rid -ceq $requiredCell.rid } | Sort-Object runAttempt -Descending)
    if ($cellRecords.Count -eq 0) {
        $selectionComplete = $false
        continue
    }
    $selectedRecords += $cellRecords[0]
}

if (-not $selectionComplete) {
    Set-AggregateFailureContext "inconclusive" "required-cell-evidence-incomplete"
    Write-AggregateFailure $script:aggregateOutcome $script:aggregateReasonCode
    exit 1
}

$script:cellSelections = @($selectedRecords | Sort-Object runnerOs, rid | ForEach-Object {
    $selected = $_
    $superseded = @($records | Where-Object { $_.runnerOs -ceq $selected.runnerOs -and $_.rid -ceq $selected.rid -and $_.runAttempt -lt $selected.runAttempt })
    [pscustomobject][ordered]@{
        runnerOs = $selected.runnerOs
        rid = $selected.rid
        selectedRunAttempt = $selected.runAttempt
        supersededRecordCount = [long]$superseded.Count
        highestSupersededRunAttempt = if ($superseded.Count -eq 0) { $null } else { [long](($superseded.runAttempt | Measure-Object -Maximum).Maximum) }
    }
})

$selectedFailures = @($selectedRecords | Where-Object { $_.kind -eq "failure" })
if ($selectedFailures.Count -gt 0) {
    $failureOutcomes = @($selectedFailures.document.aggregateOutcome)
    $aggregateOutcome = if ($failureOutcomes -contains "baseline-invalidated") { "baseline-invalidated" } elseif ($failureOutcomes -contains "protocol-failure") { "protocol-failure" } elseif ($failureOutcomes -contains "baseline-failure") { "baseline-failure" } else { "inconclusive" }
    Write-AggregateFailure $aggregateOutcome "required-cell-failure" $script:cellSelections
    exit 1
}

if ($ValidateResult -ne "success") {
    Write-AggregateFailure "inconclusive" "required-cell-validation-incomplete" $script:cellSelections
    exit 1
}

$cells = @($selectedRecords | Sort-Object runnerOs, rid | ForEach-Object {
    $record = $_
    $document = $record.document
    Set-AggregateFailureContext "baseline-failure" "cross-cell-byte-mismatch"
    Assert-Condition ($document.comparison.crossRunEquality -eq $true) "A selected cell did not prove fresh-run byte equality."
    Set-AggregateFailureContext "protocol-failure" "aggregate-evidence-invalid"
    Assert-Condition ($document.comparison.oracleEquality -eq $true) "A selected cell did not prove oracle byte equality."
    Assert-Condition (@($document.runs).Count -eq 2) "A selected cell did not record exactly two fresh runs."
    Assert-Condition (@($document.observedCommands).Count -eq 2) "A selected cell did not record each host invocation."
    Assert-Condition ($document.executionPolicy.networkDependencyDeclared -eq $manifest.executionPolicy.networkDependencyDeclared) "A selected cell used an unbound network-dependency policy."
    Assert-Condition ($document.executionPolicy.networkIsolationEnforced -eq $manifest.executionPolicy.networkIsolationEnforced) "A selected cell used an unbound network-isolation policy."
    foreach ($invocation in $document.observedCommands) {
        Assert-Condition ($invocation.executable -eq "dotnet" -and @($invocation.arguments).Count -eq 3 -and $invocation.workingDirectory -eq "repository") "A selected cell recorded an invalid host invocation."
    }

    $runHashes = @()
    foreach ($run in $document.runs) {
        $payloadPath = Join-Path $record.file.DirectoryName ("run-{0}\semantic-payload.json" -f $run.run)
        Assert-Condition (Test-Path -LiteralPath $payloadPath -PathType Leaf) "A selected cell payload artifact is missing."
        $hash = Get-FileSha256 $payloadPath
        Assert-Condition ($hash -ceq $run.payloadSha256) "A selected cell payload hash does not match its record."
        Set-AggregateFailureContext "baseline-failure" "oracle-mismatch"
        Assert-Condition ($hash -ceq $manifest.fixture.oracleSha256) "A selected cell payload does not match the pinned oracle."
        $runHashes += $hash
    }
    Set-AggregateFailureContext "baseline-failure" "fresh-process-nondeterminism"
    Assert-Condition ($runHashes[0] -ceq $runHashes[1]) "Fresh runs in a selected cell are not byte-identical."
    Set-AggregateFailureContext "inconclusive" "required-cell-inconclusive"
    Assert-Condition ($document.runs[0].processArchitecture -ceq "X64") "A selected cell was not X64."

    [pscustomobject][ordered]@{
        runnerOs = $record.runnerOs
        rid = $record.rid
        selectedBaselineCommit = $record.selectedBaselineCommit
        protocolCommit = $record.protocolCommit
        protocolPrHeadCommit = $record.protocolPrHeadCommit
        validationMergeCommit = $record.validationMergeCommit
        fixtureCommit = $record.fixtureCommit
        oracleSha256 = $record.oracleSha256
        payloadSha256 = $runHashes[0]
        sdkVersion = $document.runs[0].sdkVersion
        msbuildVersion = $document.runs[0].msbuildVersion
        runtimeVersion = $document.runs[0].runtimeVersion
        processArchitecture = $document.runs[0].processArchitecture
        ci = $document.ci
    }
})

Set-AggregateFailureContext "protocol-failure" "aggregate-provenance-mismatch"
Assert-Condition ((@($cells.protocolCommit) | Select-Object -Unique).Count -eq 1) "Selected cells used different protocol revisions."
Assert-Condition ((@($cells.protocolPrHeadCommit) | Select-Object -Unique).Count -eq 1) "Selected cells used different PR heads."
Assert-Condition ((@($cells.validationMergeCommit) | Select-Object -Unique).Count -eq 1) "Selected cells used different validation merge refs."
Set-AggregateFailureContext "baseline-failure" "cross-cell-byte-mismatch"
Assert-Condition ((@($cells.payloadSha256) | Select-Object -Unique).Count -eq 1) "Selected cells produced different canonical payload bytes."

$aggregate = [ordered]@{
    formatVersion = "contractscribe-m0.7-aggregate-evidence-v1"
    aggregateOutcome = "succeeded"
    evidenceGeneratingCommit = if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_SHA)) { $env:GITHUB_SHA } else { $ExpectedValidationMergeCommit }
    evidenceGeneratingRunId = $script:currentRunId
    evidenceGeneratingRunAttempt = $script:currentRunAttempt
    evidenceGeneratingRunUrl = if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_SERVER_URL) -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) { "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$script:currentRunId" } else { $null }
    validateResult = $ValidateResult
    selectedBaselineCommit = $cells[0].selectedBaselineCommit
    protocolCommit = $cells[0].protocolCommit
    protocolPrHeadCommit = $cells[0].protocolPrHeadCommit
    validationMergeCommit = $cells[0].validationMergeCommit
    fixtureCommit = $cells[0].fixtureCommit
    oracleSha256 = $cells[0].oracleSha256
    cellSelections = $script:cellSelections
    comparison = [ordered]@{ freshProcessCountPerCell = 2; requiredCellCount = 2; crossRunEquality = $true; crossCellEquality = $true; payloadSha256 = $cells[0].payloadSha256 }
    cells = $cells
    unresolvedRisks = @(
        "The selected baseline remains limited to the documented two-project synthetic shape and Ubuntu/Windows X64 framework-dependent matrix.",
        "Production process topology and distribution channel remain deferred to Issues #17 and #18."
    )
}
[IO.File]::WriteAllText($OutputPath, ($aggregate | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
Write-Output "M0.7 aggregate evidence succeeded: latest discovered Linux and Windows attempts produced matching payload bytes."
