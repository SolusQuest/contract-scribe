[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$root = Join-Path $repositoryRoot "TestResults\m0.7-aggregate-outcome-vectors"
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Path $root | Out-Null

$script:runId = "100"
$script:prHead = "1111111111111111111111111111111111111111"
$script:validationMerge = "2222222222222222222222222222222222222222"
$script:protocolCommit = $script:validationMerge
$script:baselineCommit = "645c0946b8b811d633b471b232b0654c10e6d7f6"
$script:fixtureCommit = "aee85e30a7634fdf6adce7ac8b1a185a68b9698a"
$syntheticOracleContent = "payload"
$syntheticOraclePath = Join-Path $root "synthetic-oracle.txt"
[IO.File]::WriteAllText($syntheticOraclePath, $syntheticOracleContent, [Text.UTF8Encoding]::new($false))
$script:oracleSha256 = (Get-FileHash -LiteralPath $syntheticOraclePath -Algorithm SHA256).Hash.ToLowerInvariant()
$syntheticManifestPath = Join-Path $root "synthetic-manifest.json"
$script:manifest = Get-Content -Raw (Join-Path $repositoryRoot "tests/fixtures/roslyn-msbuild/m0.7-independent-validation-manifest.json") | ConvertFrom-Json
$script:manifest.fixture.oracleSha256 = $script:oracleSha256
[IO.File]::WriteAllText($syntheticManifestPath, ($script:manifest | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))

function Get-CellRid([string]$RunnerOs) {
    if ($RunnerOs -eq "Linux") { return "linux-x64" }
    if ($RunnerOs -eq "Windows") { return "win-x64" }
    return "unknown-x64"
}

function Write-SyntheticSuccess {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$RunnerOs,
        [Parameter(Mandatory = $true)][AllowNull()][object]$RunAttempt,
        [string]$Rid,
        [string]$RunId,
        [string]$PrHead,
        [string]$ValidationMerge,
        [string]$ProtocolCommit,
        [string]$BaselineCommit,
        [string]$FixtureCommit,
        [string]$OracleSha256,
        [string]$PayloadContent = "payload",
        [int]$RunCount = 2,
        [switch]$OmitPayloads,
        [switch]$OmitRunAttempt,
        [bool]$CrossRunEquality = $true,
        [bool]$OracleEquality = $true,
        [string]$UnsafeExtraContent
    )
    if ([string]::IsNullOrWhiteSpace($Rid)) { $Rid = Get-CellRid $RunnerOs }
    if ([string]::IsNullOrWhiteSpace($RunId)) { $RunId = $script:runId }
    if ([string]::IsNullOrWhiteSpace($PrHead)) { $PrHead = $script:prHead }
    if ([string]::IsNullOrWhiteSpace($ValidationMerge)) { $ValidationMerge = $script:validationMerge }
    if ([string]::IsNullOrWhiteSpace($ProtocolCommit)) { $ProtocolCommit = $script:protocolCommit }
    if ([string]::IsNullOrWhiteSpace($BaselineCommit)) { $BaselineCommit = $script:baselineCommit }
    if ([string]::IsNullOrWhiteSpace($FixtureCommit)) { $FixtureCommit = $script:fixtureCommit }
    if ([string]::IsNullOrWhiteSpace($OracleSha256)) { $OracleSha256 = $script:oracleSha256 }
    if ($PayloadContent -eq "payload") { $PayloadContent = $syntheticOracleContent }

    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $runs = @()
    for ($run = 1; $run -le $RunCount; $run++) {
        $payloadPath = Join-Path $Directory ("run-{0}\semantic-payload.json" -f $run)
        if (-not $OmitPayloads) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $payloadPath) -Force | Out-Null
            [IO.File]::WriteAllText($payloadPath, $PayloadContent, [Text.UTF8Encoding]::new($false))
        }
        $runs += [ordered]@{
            run = $run
            payloadSha256 = if ($OmitPayloads) { $script:oracleSha256 } else { (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant() }
            sdkVersion = "10.0.302"
            msbuildVersion = "18.6.11.33009"
            runtimeVersion = "10.0.10"
            processArchitecture = "X64"
        }
    }
    $ci = [ordered]@{ runId = $RunId; sha = $ValidationMerge }
    if (-not $OmitRunAttempt) { $ci["runAttempt"] = $RunAttempt }
    $document = [ordered]@{
        formatVersion = "contractscribe-m0.7-evidence-v1"
        aggregateOutcome = "succeeded"
        runnerOs = $RunnerOs
        rid = $Rid
        selectedBaselineCommit = $BaselineCommit
        protocolCommit = $ProtocolCommit
        protocolPrHeadCommit = $PrHead
        validationMergeCommit = $ValidationMerge
        fixtureCommit = $FixtureCommit
        oracleSha256 = $OracleSha256
        executionPolicy = [ordered]@{
            networkDependencyDeclared = $script:manifest.executionPolicy.networkDependencyDeclared
            networkIsolationEnforced = $script:manifest.executionPolicy.networkIsolationEnforced
        }
        comparison = [ordered]@{ crossRunEquality = $CrossRunEquality; oracleEquality = $OracleEquality }
        observedCommands = @(
            [ordered]@{ runNumber = 1; executable = "dotnet"; arguments = @("host.dll", "fixture/Sample.sln", "run-1"); workingDirectory = "repository" },
            [ordered]@{ runNumber = 2; executable = "dotnet"; arguments = @("host.dll", "fixture/Sample.sln", "run-2"); workingDirectory = "repository" }
        )
        runs = $runs
        ci = $ci
    }
    if (-not [string]::IsNullOrEmpty($UnsafeExtraContent)) { $document["unexpectedDiagnostic"] = $UnsafeExtraContent }
    [IO.File]::WriteAllText((Join-Path $Directory "m0.7-evidence.json"), ($document | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}

function Write-SyntheticFailure {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$RunnerOs,
        [Parameter(Mandatory = $true)][AllowNull()][object]$RunAttempt,
        [string]$Outcome = "inconclusive",
        [string]$ReasonCode = "synthetic-failure",
        [string]$Rid,
        [string]$RunId,
        [string]$PrHead,
        [string]$ValidationMerge,
        [string]$ProtocolCommit,
        [string]$BaselineCommit,
        [string]$FixtureCommit,
        [string]$OracleSha256,
        [switch]$OmitRunAttempt
    )
    if ([string]::IsNullOrWhiteSpace($Rid)) { $Rid = Get-CellRid $RunnerOs }
    if ([string]::IsNullOrWhiteSpace($RunId)) { $RunId = $script:runId }
    if ([string]::IsNullOrWhiteSpace($PrHead)) { $PrHead = $script:prHead }
    if ([string]::IsNullOrWhiteSpace($ValidationMerge)) { $ValidationMerge = $script:validationMerge }
    if ([string]::IsNullOrWhiteSpace($ProtocolCommit)) { $ProtocolCommit = $script:protocolCommit }
    if ([string]::IsNullOrWhiteSpace($BaselineCommit)) { $BaselineCommit = $script:baselineCommit }
    if ([string]::IsNullOrWhiteSpace($FixtureCommit)) { $FixtureCommit = $script:fixtureCommit }
    if ([string]::IsNullOrWhiteSpace($OracleSha256)) { $OracleSha256 = $script:oracleSha256 }
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $ci = [ordered]@{ runId = $RunId; sha = $ValidationMerge }
    if (-not $OmitRunAttempt) { $ci["runAttempt"] = $RunAttempt }
    $document = [ordered]@{
        formatVersion = "contractscribe-m0.7-failure-evidence-v1"
        aggregateOutcome = $Outcome
        reasonCode = $ReasonCode
        runnerOs = $RunnerOs
        rid = $Rid
        selectedBaselineCommit = $BaselineCommit
        protocolCommit = $ProtocolCommit
        protocolPrHeadCommit = $PrHead
        validationMergeCommit = $ValidationMerge
        fixtureCommit = $FixtureCommit
        oracleSha256 = $OracleSha256
        ci = $ci
        retainedFailure = $true
    }
    [IO.File]::WriteAllText((Join-Path $Directory "m0.7-failure-evidence.json"), ($document | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}

function Invoke-SyntheticAggregate {
    param(
        [Parameter(Mandatory = $true)][string]$EvidenceRoot,
        [Parameter(Mandatory = $true)][long]$CurrentAttempt,
        [string]$ValidateResult = "success"
    )
    $outputPath = Join-Path $EvidenceRoot "aggregate.json"
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "aggregate-m0.7.ps1") `
        -EvidenceRoot $EvidenceRoot `
        -OutputPath $outputPath `
        -ValidateResult $ValidateResult `
        -ManifestPath $syntheticManifestPath `
        -ExpectedPrHeadCommit $script:prHead `
        -ExpectedValidationMergeCommit $script:validationMerge `
        -ExpectedRunId $script:runId `
        -ExpectedRunAttempt $CurrentAttempt 2>&1 | Out-Null
    $exitCode = $LASTEXITCODE
    return [pscustomobject]@{ exitCode = $exitCode; document = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json; outputPath = $outputPath }
}

function Assert-Aggregate([object]$Result, [int]$ExitCode, [string]$Outcome, [string]$ReasonCode = $null) {
    if ($Result.exitCode -ne $ExitCode) { throw "Aggregate exit code was $($Result.exitCode), expected $ExitCode." }
    if ($Result.document.aggregateOutcome -cne $Outcome) { throw "Aggregate outcome was $($Result.document.aggregateOutcome), expected $Outcome." }
    if (-not [string]::IsNullOrEmpty($ReasonCode) -and $Result.document.reasonCode -cne $ReasonCode) { throw "Aggregate reason was $($Result.document.reasonCode), expected $ReasonCode." }
    if ($Result.document.evidenceGeneratingRunId -cne $script:runId) { throw "Aggregate run ID was not serialized exactly." }
    if ($Result.document.evidenceGeneratingRunAttempt -isnot [long] -and $Result.document.evidenceGeneratingRunAttempt -isnot [int]) { throw "Aggregate run attempt was not a JSON integer." }
}

function New-Scenario([string]$Name) {
    $path = Join-Path $root $Name
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    return $path
}

$scenario = New-Scenario "attempt-1-success"
Write-SyntheticSuccess (Join-Path $scenario "linux-a1") "Linux" 1
Write-SyntheticSuccess (Join-Path $scenario "windows-a1") "Windows" 1
$result = Invoke-SyntheticAggregate $scenario 1
Assert-Aggregate $result 0 "succeeded"
if (@($result.document.cellSelections).Count -ne 2 -or $result.document.cellSelections[0].runnerOs -cne "Linux" -or $result.document.cellSelections[1].runnerOs -cne "Windows") { throw "Cell selections are not complete and ordinally ordered." }
if (($result.document.cellSelections | Where-Object { $_.supersededRecordCount -ne 0 -or $null -ne $_.highestSupersededRunAttempt }).Count -ne 0) { throw "Attempt-1 evidence reported a superseded record." }

$savedRunId = $env:GITHUB_RUN_ID
$savedRunAttempt = $env:GITHUB_RUN_ATTEMPT
try {
    $env:GITHUB_RUN_ID = $script:runId
    $env:GITHUB_RUN_ATTEMPT = "1"
    $environmentOutputPath = Join-Path $scenario "aggregate-from-environment.json"
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "aggregate-m0.7.ps1") `
        -EvidenceRoot $scenario `
        -OutputPath $environmentOutputPath `
        -ManifestPath $syntheticManifestPath `
        -ExpectedPrHeadCommit $script:prHead `
        -ExpectedValidationMergeCommit $script:validationMerge 2>&1 | Out-Null
    $environmentResult = [pscustomobject]@{ exitCode = $LASTEXITCODE; document = Get-Content -LiteralPath $environmentOutputPath -Raw | ConvertFrom-Json }
    Assert-Aggregate $environmentResult 0 "succeeded"
}
finally {
    if ($null -eq $savedRunId) { Remove-Item Env:GITHUB_RUN_ID -ErrorAction SilentlyContinue } else { $env:GITHUB_RUN_ID = $savedRunId }
    if ($null -eq $savedRunAttempt) { Remove-Item Env:GITHUB_RUN_ATTEMPT -ErrorAction SilentlyContinue } else { $env:GITHUB_RUN_ATTEMPT = $savedRunAttempt }
}

$scenario = New-Scenario "partial-rerun-failure-to-success"
Write-SyntheticFailure (Join-Path $scenario "linux-a1-failure") "Linux" 1 "baseline-failure" "synthetic-baseline-failure"
Write-SyntheticSuccess (Join-Path $scenario "linux-a2-success") "Linux" 2
Write-SyntheticSuccess (Join-Path $scenario "windows-a1-success") "Windows" 1
$supersededFailurePath = Join-Path $scenario "linux-a1-failure\m0.7-failure-evidence.json"
$result = Invoke-SyntheticAggregate $scenario 2
Assert-Aggregate $result 0 "succeeded"
$linuxSelection = $result.document.cellSelections | Where-Object runnerOs -eq "Linux"
$windowsSelection = $result.document.cellSelections | Where-Object runnerOs -eq "Windows"
if ($linuxSelection.selectedRunAttempt -ne 2 -or $linuxSelection.supersededRecordCount -ne 1 -or $linuxSelection.highestSupersededRunAttempt -ne 1 -or $windowsSelection.selectedRunAttempt -ne 1) { throw "Partial rerun selection metadata is incorrect." }
if (-not (Test-Path -LiteralPath $supersededFailurePath)) { throw "Aggregation deleted superseded failure evidence." }

$scenario = New-Scenario "later-failure-wins"
Write-SyntheticSuccess (Join-Path $scenario "linux-a1-success") "Linux" 1
Write-SyntheticFailure (Join-Path $scenario "linux-a2-failure") "Linux" 2 "baseline-failure" "synthetic-baseline-failure"
Write-SyntheticSuccess (Join-Path $scenario "windows-a1-success") "Windows" 1
$result = Invoke-SyntheticAggregate $scenario 2
Assert-Aggregate $result 1 "baseline-failure" "required-cell-failure"
if (@($result.document.cellSelections).Count -ne 2) { throw "A selected latest failure did not retain the complete selection summary." }

$scenario = New-Scenario "both-cells-rerun"
foreach ($runnerOs in @("Linux", "Windows")) {
    Write-SyntheticSuccess (Join-Path $scenario ("{0}-a1" -f $runnerOs.ToLowerInvariant())) $runnerOs 1
    Write-SyntheticSuccess (Join-Path $scenario ("{0}-a2" -f $runnerOs.ToLowerInvariant())) $runnerOs 2
}
$result = Invoke-SyntheticAggregate $scenario 2
Assert-Aggregate $result 0 "succeeded"
if (($result.document.cellSelections | Where-Object { $_.selectedRunAttempt -ne 2 -or $_.supersededRecordCount -ne 1 }).Count -ne 0) { throw "Whole-matrix rerun selection is incorrect." }

$scenario = New-Scenario "sparse-attempts"
Write-SyntheticSuccess (Join-Path $scenario "linux-a1") "Linux" 1
Write-SyntheticSuccess (Join-Path $scenario "linux-a3") "Linux" 3
Write-SyntheticSuccess (Join-Path $scenario "windows-a1") "Windows" 1
$result = Invoke-SyntheticAggregate $scenario 3
Assert-Aggregate $result 0 "succeeded"
if (($result.document.cellSelections | Where-Object runnerOs -eq "Linux").selectedRunAttempt -ne 3) { throw "Sparse attempts were not selected numerically." }

$scenario = New-Scenario "superseded-success-payload-absent"
Write-SyntheticSuccess (Join-Path $scenario "linux-a1-no-payload") "Linux" 1 -OmitPayloads
Write-SyntheticSuccess (Join-Path $scenario "linux-a2") "Linux" 2
Write-SyntheticSuccess (Join-Path $scenario "windows-a1") "Windows" 1
$result = Invoke-SyntheticAggregate $scenario 2
Assert-Aggregate $result 0 "succeeded"

$scenario = New-Scenario "superseded-success-public-output-unsafe"
Write-SyntheticSuccess (Join-Path $scenario "linux-a1-unsafe") "Linux" 1 -UnsafeExtraContent "Authorization: Bearer synthetic-secret"
Write-SyntheticSuccess (Join-Path $scenario "linux-a2") "Linux" 2
Write-SyntheticSuccess (Join-Path $scenario "windows-a1") "Windows" 1
$result = Invoke-SyntheticAggregate $scenario 2
Assert-Aggregate $result 0 "succeeded"
$linuxSelection = $result.document.cellSelections | Where-Object runnerOs -eq "Linux"
if ($linuxSelection.selectedRunAttempt -ne 2 -or $linuxSelection.supersededRecordCount -ne 1) { throw "An unsafe superseded success changed the selected current record." }

$scenario = New-Scenario "same-key-success-failure-conflict"
Write-SyntheticSuccess (Join-Path $scenario "linux-success") "Linux" 1
Write-SyntheticFailure (Join-Path $scenario "linux-failure") "Linux" 1
Write-SyntheticSuccess (Join-Path $scenario "windows") "Windows" 1
$result = Invoke-SyntheticAggregate $scenario 1
Assert-Aggregate $result 1 "protocol-failure" "aggregate-evidence-invalid"
if ($null -ne $result.document.PSObject.Properties["cellSelections"]) { throw "A conflict emitted partial cell selections." }

$scenario = New-Scenario "same-kind-byte-identical-duplicate"
Write-SyntheticFailure (Join-Path $scenario "linux-copy-1") "Linux" 1
Write-SyntheticFailure (Join-Path $scenario "linux-copy-2") "Linux" 1
Write-SyntheticSuccess (Join-Path $scenario "windows") "Windows" 1
$result = Invoke-SyntheticAggregate $scenario 1
Assert-Aggregate $result 1 "protocol-failure" "aggregate-evidence-invalid"

$invalidAttemptVectors = @(
    [pscustomobject]@{ name = "missing"; value = $null; omit = $true },
    [pscustomobject]@{ name = "zero"; value = 0; omit = $false },
    [pscustomobject]@{ name = "negative"; value = -1; omit = $false },
    [pscustomobject]@{ name = "fractional"; value = 1.5; omit = $false },
    [pscustomobject]@{ name = "text"; value = "1"; omit = $false }
)
foreach ($vector in $invalidAttemptVectors) {
    $scenario = New-Scenario ("invalid-attempt-{0}" -f $vector.name)
    Write-SyntheticFailure (Join-Path $scenario "linux") "Linux" $vector.value -OmitRunAttempt:$vector.omit
    Write-SyntheticSuccess (Join-Path $scenario "windows") "Windows" 1
    $result = Invoke-SyntheticAggregate $scenario 1
    Assert-Aggregate $result 1 "protocol-failure" "aggregate-evidence-invalid"
}

$scenario = New-Scenario "future-attempt"
Write-SyntheticSuccess (Join-Path $scenario "linux") "Linux" 2
Write-SyntheticSuccess (Join-Path $scenario "windows") "Windows" 1
$result = Invoke-SyntheticAggregate $scenario 1
Assert-Aggregate $result 1 "protocol-failure" "aggregate-evidence-invalid"

$scenario = New-Scenario "cross-wired-cell"
Write-SyntheticSuccess (Join-Path $scenario "linux") "Linux" 1 -Rid "win-x64"
Write-SyntheticSuccess (Join-Path $scenario "windows") "Windows" 1
$result = Invoke-SyntheticAggregate $scenario 1
Assert-Aggregate $result 1 "protocol-failure" "aggregate-evidence-invalid"

$provenanceVectors = @(
    [pscustomobject]@{ name = "run-id"; arguments = @{ RunId = "101" }; outcome = "protocol-failure"; reason = "aggregate-provenance-mismatch" },
    [pscustomobject]@{ name = "pr-head"; arguments = @{ PrHead = "4444444444444444444444444444444444444444" }; outcome = "protocol-failure"; reason = "aggregate-provenance-mismatch" },
    [pscustomobject]@{ name = "merge"; arguments = @{ ValidationMerge = "4444444444444444444444444444444444444444" }; outcome = "protocol-failure"; reason = "aggregate-provenance-mismatch" },
    [pscustomobject]@{ name = "protocol"; arguments = @{ ProtocolCommit = "4444444444444444444444444444444444444444" }; outcome = "protocol-failure"; reason = "aggregate-provenance-mismatch" },
    [pscustomobject]@{ name = "fixture"; arguments = @{ FixtureCommit = "4444444444444444444444444444444444444444" }; outcome = "protocol-failure"; reason = "aggregate-provenance-mismatch" },
    [pscustomobject]@{ name = "oracle"; arguments = @{ OracleSha256 = "4444444444444444444444444444444444444444444444444444444444444444" }; outcome = "protocol-failure"; reason = "aggregate-provenance-mismatch" },
    [pscustomobject]@{ name = "baseline"; arguments = @{ BaselineCommit = "4444444444444444444444444444444444444444" }; outcome = "baseline-invalidated"; reason = "aggregate-baseline-drift" }
)
foreach ($vector in $provenanceVectors) {
    $scenario = New-Scenario ("mixed-{0}" -f $vector.name)
    $arguments = $vector.arguments
    Write-SyntheticSuccess (Join-Path $scenario "linux") "Linux" 1 @arguments
    Write-SyntheticSuccess (Join-Path $scenario "windows") "Windows" 1
    $result = Invoke-SyntheticAggregate $scenario 1
    Assert-Aggregate $result 1 $vector.outcome $vector.reason
}

$scenario = New-Scenario "missing-required-cell"
Write-SyntheticSuccess (Join-Path $scenario "linux") "Linux" 1
$result = Invoke-SyntheticAggregate $scenario 1
Assert-Aggregate $result 1 "inconclusive" "required-cell-evidence-incomplete"
if ($null -ne $result.document.PSObject.Properties["cellSelections"]) { throw "A missing-cell failure emitted partial selections." }

foreach ($validateResult in @("failure", "cancelled", "skipped")) {
    $scenario = New-Scenario ("workflow-result-{0}" -f $validateResult)
    Write-SyntheticSuccess (Join-Path $scenario "linux") "Linux" 1
    Write-SyntheticSuccess (Join-Path $scenario "windows") "Windows" 1
    $result = Invoke-SyntheticAggregate $scenario 1 $validateResult
    Assert-Aggregate $result 1 "inconclusive" "required-cell-validation-incomplete"
    if (@($result.document.cellSelections).Count -ne 2) { throw "A complete non-success workflow result omitted selections." }
}

$scenario = New-Scenario "selected-payload-missing"
Write-SyntheticSuccess (Join-Path $scenario "linux") "Linux" 1 -OmitPayloads
Write-SyntheticSuccess (Join-Path $scenario "windows") "Windows" 1
$result = Invoke-SyntheticAggregate $scenario 1
Assert-Aggregate $result 1 "protocol-failure" "aggregate-evidence-invalid"

$scenario = New-Scenario "selected-success-public-output-unsafe"
Write-SyntheticSuccess (Join-Path $scenario "linux") "Linux" 1 -UnsafeExtraContent "Authorization: Bearer synthetic-secret"
Write-SyntheticSuccess (Join-Path $scenario "windows") "Windows" 1
$result = Invoke-SyntheticAggregate $scenario 1
Assert-Aggregate $result 1 "protocol-failure" "public-output-safety"
if (@($result.document.cellSelections).Count -ne 2) { throw "A selected unsafe success did not retain the complete selection summary." }

$scenario = New-Scenario "selected-unsafe-success-with-inconclusive-failure"
Write-SyntheticSuccess (Join-Path $scenario "linux") "Linux" 1 -UnsafeExtraContent "Authorization: Bearer synthetic-secret"
Write-SyntheticFailure (Join-Path $scenario "windows") "Windows" 1 "inconclusive" "synthetic-inconclusive"
$result = Invoke-SyntheticAggregate $scenario 1
Assert-Aggregate $result 1 "protocol-failure" "public-output-safety"
if (@($result.document.cellSelections).Count -ne 2) { throw "A mixed unsafe-success failure did not retain the complete selection summary." }

$scenario = New-Scenario "selected-unsafe-success-with-baseline-invalidated-failure"
Write-SyntheticSuccess (Join-Path $scenario "linux") "Linux" 1 -UnsafeExtraContent "Authorization: Bearer synthetic-secret"
Write-SyntheticFailure (Join-Path $scenario "windows") "Windows" 1 "baseline-invalidated" "synthetic-baseline-invalidated"
$result = Invoke-SyntheticAggregate $scenario 1
Assert-Aggregate $result 1 "baseline-invalidated" "required-cell-failure"
if (@($result.document.cellSelections).Count -ne 2) { throw "A mixed baseline-invalidated failure did not retain the complete selection summary." }

$scenario = New-Scenario "selected-invalid-success-with-inconclusive-failure"
Write-SyntheticSuccess (Join-Path $scenario "linux") "Linux" 1 -OmitPayloads
Write-SyntheticFailure (Join-Path $scenario "windows") "Windows" 1 "inconclusive" "synthetic-inconclusive"
$result = Invoke-SyntheticAggregate $scenario 1
Assert-Aggregate $result 1 "protocol-failure" "aggregate-evidence-invalid"

$scenario = New-Scenario "selected-cross-run-declaration-failure"
Write-SyntheticSuccess (Join-Path $scenario "linux") "Linux" 1 -CrossRunEquality $false
Write-SyntheticSuccess (Join-Path $scenario "windows") "Windows" 1
$result = Invoke-SyntheticAggregate $scenario 1
Assert-Aggregate $result 1 "baseline-failure" "cross-cell-byte-mismatch"

$scenario = New-Scenario "selected-run-count-invalid"
Write-SyntheticSuccess (Join-Path $scenario "linux") "Linux" 1 -RunCount 1
Write-SyntheticSuccess (Join-Path $scenario "windows") "Windows" 1
$result = Invoke-SyntheticAggregate $scenario 1
Assert-Aggregate $result 1 "protocol-failure" "aggregate-evidence-invalid"

$scenario = New-Scenario "selected-oracle-mismatch"
Write-SyntheticSuccess (Join-Path $scenario "linux") "Linux" 1 -PayloadContent "wrong"
Write-SyntheticSuccess (Join-Path $scenario "windows") "Windows" 1
$result = Invoke-SyntheticAggregate $scenario 1
Assert-Aggregate $result 1 "baseline-failure" "oracle-mismatch"

$precedenceVectors = @(
    [pscustomobject]@{ name = "baseline-invalidated-over-protocol"; linux = "baseline-invalidated"; windows = "protocol-failure"; expected = "baseline-invalidated" },
    [pscustomobject]@{ name = "protocol-over-baseline-failure"; linux = "protocol-failure"; windows = "baseline-failure"; expected = "protocol-failure" },
    [pscustomobject]@{ name = "baseline-failure-over-inconclusive"; linux = "baseline-failure"; windows = "inconclusive"; expected = "baseline-failure" },
    [pscustomobject]@{ name = "inconclusive-only"; linux = "inconclusive"; windows = "inconclusive"; expected = "inconclusive" }
)
foreach ($vector in $precedenceVectors) {
    $scenario = New-Scenario $vector.name
    Write-SyntheticFailure (Join-Path $scenario "linux") "Linux" 1 $vector.linux "synthetic-linux"
    Write-SyntheticFailure (Join-Path $scenario "windows") "Windows" 1 $vector.windows "synthetic-windows"
    $result = Invoke-SyntheticAggregate $scenario 1
    Assert-Aggregate $result 1 $vector.expected "required-cell-failure"
}

Remove-Item -LiteralPath $root -Recurse -Force
Write-Output "M0.7 aggregate attempt vectors passed: per-cell rerun selection, fail-closed identity, selected-record public safety, bounded supersession, and existing outcome precedence are retained."
