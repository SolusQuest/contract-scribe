[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$root = Join-Path $repositoryRoot "TestResults\m0.7-aggregate-outcome-vectors"
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Path $root | Out-Null
$syntheticOracleContent = "payload"
$syntheticOraclePath = Join-Path $root "synthetic-oracle.txt"
[IO.File]::WriteAllText($syntheticOraclePath, $syntheticOracleContent, [Text.UTF8Encoding]::new($false))
$syntheticOracleSha256 = (Get-FileHash -LiteralPath $syntheticOraclePath -Algorithm SHA256).Hash.ToLowerInvariant()
$syntheticManifestPath = Join-Path $root "synthetic-manifest.json"
$syntheticManifest = Get-Content -Raw (Join-Path $repositoryRoot "tests/fixtures/roslyn-msbuild/m0.7-independent-validation-manifest.json") | ConvertFrom-Json
$syntheticManifest.fixture.oracleSha256 = $syntheticOracleSha256
[IO.File]::WriteAllText($syntheticManifestPath, ($syntheticManifest | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))

$vectors = @(
    @{ name = "baseline-invalidated-over-protocol"; outcomes = @("baseline-invalidated", "protocol-failure"); expected = "baseline-invalidated" },
    @{ name = "protocol-over-baseline-failure"; outcomes = @("protocol-failure", "baseline-failure"); expected = "protocol-failure" },
    @{ name = "baseline-failure-over-inconclusive"; outcomes = @("baseline-failure", "inconclusive"); expected = "baseline-failure" },
    @{ name = "inconclusive-only"; outcomes = @("inconclusive"); expected = "inconclusive" }
)

foreach ($vector in $vectors) {
    $evidenceRoot = Join-Path $root $vector.name
    New-Item -ItemType Directory -Path $evidenceRoot | Out-Null
    $index = 0
    foreach ($outcome in $vector.outcomes) {
        $index++
        $cellRoot = Join-Path $evidenceRoot ("cell-{0}" -f $index)
        New-Item -ItemType Directory -Path $cellRoot | Out-Null
        [IO.File]::WriteAllText((Join-Path $cellRoot "m0.7-failure-evidence.json"), (@{ formatVersion = "contractscribe-m0.7-failure-evidence-v1"; aggregateOutcome = $outcome; retainedFailure = $true } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    }
    $outputPath = Join-Path $evidenceRoot "aggregate.json"
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "aggregate-m0.7.ps1") -EvidenceRoot $evidenceRoot -OutputPath $outputPath -ManifestPath $syntheticManifestPath 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { throw "Aggregate outcome vector unexpectedly succeeded." }
    $aggregate = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    if ($aggregate.aggregateOutcome -ne $vector.expected -or -not $aggregate.retainedFailure) {
        throw "Aggregate outcome precedence vector did not retain its expected outcome."
    }
}

function Write-SyntheticCellEvidence([string]$cellRoot, [bool]$crossRunEquality, [int]$runCount, [bool]$writePayloads, [string]$payloadContent = "payload") {
    New-Item -ItemType Directory -Path $cellRoot -Force | Out-Null
    $effectivePayloadContent = if ($payloadContent -eq "payload") {
        $syntheticOracleContent
    } else {
        $payloadContent
    }
    $runs = @()
    for ($run = 1; $run -le $runCount; $run++) {
        $payloadPath = Join-Path $cellRoot ("run-{0}\semantic-payload.json" -f $run)
        if ($writePayloads) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $payloadPath) -Force | Out-Null
            [IO.File]::WriteAllText($payloadPath, $effectivePayloadContent, [Text.UTF8Encoding]::new($false))
        }
        $runs += [ordered]@{ run = $run; payloadSha256 = if ($writePayloads) { (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { "0000000000000000000000000000000000000000000000000000000000000000" }; sdkVersion = "10.0.302"; msbuildVersion = "18.6.11.33009"; runtimeVersion = "10.0.10"; processArchitecture = "X64" }
    }
    $document = [ordered]@{
        formatVersion = "contractscribe-m0.7-evidence-v1"
        aggregateOutcome = "succeeded"
        runnerOs = "Linux"
        rid = "linux-x64"
        selectedBaselineCommit = "645c0946b8b811d633b471b232b0654c10e6d7f6"
        protocolCommit = "0000000000000000000000000000000000000000"
        protocolPrHeadCommit = "1111111111111111111111111111111111111111"
        validationMergeCommit = "2222222222222222222222222222222222222222"
        fixtureCommit = "aee85e30a7634fdf6adce7ac8b1a185a68b9698a"
        oracleSha256 = $syntheticOracleSha256
        executionPolicy = @{ networkDependencyDeclared = $false; networkIsolationEnforced = $false }
        comparison = @{ crossRunEquality = $crossRunEquality; oracleEquality = $true }
        observedCommands = @(
            @{ runNumber = 1; executable = "dotnet"; arguments = @("host.dll", "fixture/Sample.sln", "run-1"); workingDirectory = "repository" },
            @{ runNumber = 2; executable = "dotnet"; arguments = @("host.dll", "fixture/Sample.sln", "run-2"); workingDirectory = "repository" }
        )
        runs = $runs
        ci = @{ runId = "synthetic-run"; sha = "2222222222222222222222222222222222222222" }
    }
    [IO.File]::WriteAllText((Join-Path $cellRoot "m0.7-evidence.json"), ($document | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}

$postRunVectors = @(
    @{ name = "fresh-run-byte-mismatch"; crossRunEquality = $false; runCount = 0; writePayloads = $false; expected = "baseline-failure" },
    @{ name = "fresh-run-count-invalid"; crossRunEquality = $true; runCount = 1; writePayloads = $false; expected = "protocol-failure" },
    @{ name = "payload-artifact-missing"; crossRunEquality = $true; runCount = 2; writePayloads = $false; expected = "protocol-failure" }
)
foreach ($vector in $postRunVectors) {
    $evidenceRoot = Join-Path $root $vector.name
    Write-SyntheticCellEvidence (Join-Path $evidenceRoot "cell-1") $vector.crossRunEquality $vector.runCount $vector.writePayloads
    Write-SyntheticCellEvidence (Join-Path $evidenceRoot "cell-2") $vector.crossRunEquality $vector.runCount $vector.writePayloads
    $outputPath = Join-Path $evidenceRoot "aggregate.json"
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "aggregate-m0.7.ps1") -EvidenceRoot $evidenceRoot -OutputPath $outputPath -ManifestPath $syntheticManifestPath 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { throw "Aggregate post-run vector unexpectedly succeeded." }
    $aggregate = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    if ($aggregate.aggregateOutcome -ne $vector.expected -or -not $aggregate.retainedFailure) {
        throw "Aggregate post-run vector did not retain its expected outcome."
    }
}

$freshNondeterminismRoot = Join-Path $root "fresh-run-nondeterminism"
Write-SyntheticCellEvidence (Join-Path $freshNondeterminismRoot "cell-1") $true 2 $true
Write-SyntheticCellEvidence (Join-Path $freshNondeterminismRoot "cell-2") $true 2 $true
$freshRunTwoPath = Join-Path $freshNondeterminismRoot "cell-1\run-2\semantic-payload.json"
[IO.File]::WriteAllText($freshRunTwoPath, "payload-two", [Text.UTF8Encoding]::new($false))
$freshCellDocumentPath = Join-Path $freshNondeterminismRoot "cell-1\m0.7-evidence.json"
$freshCellDocument = Get-Content -LiteralPath $freshCellDocumentPath -Raw | ConvertFrom-Json
$freshCellDocument.comparison.crossRunEquality = $false
$freshCellDocument.runs[1].payloadSha256 = (Get-FileHash -LiteralPath $freshRunTwoPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($freshCellDocumentPath, ($freshCellDocument | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
$freshNondeterminismOutput = Join-Path $freshNondeterminismRoot "aggregate.json"
& pwsh -NoProfile -File (Join-Path $PSScriptRoot "aggregate-m0.7.ps1") -EvidenceRoot $freshNondeterminismRoot -OutputPath $freshNondeterminismOutput -ManifestPath $syntheticManifestPath 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0 -or (Get-Content -Raw $freshNondeterminismOutput | ConvertFrom-Json).aggregateOutcome -ne "baseline-failure") { throw "Fresh-process nondeterminism was not classified as baseline-failure." }

$crossCellRoot = Join-Path $root "cross-cell-byte-mismatch"
Write-SyntheticCellEvidence (Join-Path $crossCellRoot "cell-1") $true 2 $true
Write-SyntheticCellEvidence (Join-Path $crossCellRoot "cell-2") $true 2 $true
$crossCellTwoRunOnePath = Join-Path $crossCellRoot "cell-2\run-1\semantic-payload.json"
$crossCellTwoRunTwoPath = Join-Path $crossCellRoot "cell-2\run-2\semantic-payload.json"
[IO.File]::WriteAllText($crossCellTwoRunOnePath, "payload-two", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($crossCellTwoRunTwoPath, "payload-two", [Text.UTF8Encoding]::new($false))
$crossCellTwoDocumentPath = Join-Path $crossCellRoot "cell-2\m0.7-evidence.json"
$crossCellTwoDocument = Get-Content -LiteralPath $crossCellTwoDocumentPath -Raw | ConvertFrom-Json
$crossCellTwoDocument.runs[0].payloadSha256 = (Get-FileHash -LiteralPath $crossCellTwoRunOnePath -Algorithm SHA256).Hash.ToLowerInvariant()
$crossCellTwoDocument.runs[1].payloadSha256 = (Get-FileHash -LiteralPath $crossCellTwoRunTwoPath -Algorithm SHA256).Hash.ToLowerInvariant()
$crossCellTwoDocument.runnerOs = "Windows"
$crossCellTwoDocument.rid = "win-x64"
[IO.File]::WriteAllText($crossCellTwoDocumentPath, ($crossCellTwoDocument | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
$crossCellOutput = Join-Path $crossCellRoot "aggregate.json"
& pwsh -NoProfile -File (Join-Path $PSScriptRoot "aggregate-m0.7.ps1") -EvidenceRoot $crossCellRoot -OutputPath $crossCellOutput -ManifestPath $syntheticManifestPath 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0 -or (Get-Content -Raw $crossCellOutput | ConvertFrom-Json).aggregateOutcome -ne "baseline-failure") { throw "Cross-cell byte mismatch was not classified as baseline-failure." }

$wrongOracleRoot = Join-Path $root "wrong-but-identical-oracle"
Write-SyntheticCellEvidence (Join-Path $wrongOracleRoot "cell-1") $true 2 $true "wrong-payload"
Write-SyntheticCellEvidence (Join-Path $wrongOracleRoot "cell-2") $true 2 $true "wrong-payload"
$wrongOracleOutput = Join-Path $wrongOracleRoot "aggregate.json"
& pwsh -NoProfile -File (Join-Path $PSScriptRoot "aggregate-m0.7.ps1") -EvidenceRoot $wrongOracleRoot -OutputPath $wrongOracleOutput -ManifestPath $syntheticManifestPath 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0 -or (Get-Content -Raw $wrongOracleOutput | ConvertFrom-Json).aggregateOutcome -ne "baseline-failure") { throw "Identical but wrong oracle payloads were accepted by aggregate." }

foreach ($provenanceField in @("selectedBaselineCommit", "protocolCommit", "fixtureCommit", "oracleSha256")) {
    $provenanceRoot = Join-Path $root ("provenance-mismatch-{0}" -f $provenanceField)
    Write-SyntheticCellEvidence (Join-Path $provenanceRoot "cell-1") $true 2 $true
    Write-SyntheticCellEvidence (Join-Path $provenanceRoot "cell-2") $true 2 $true
    $cellTwoDocumentPath = Join-Path $provenanceRoot "cell-2\m0.7-evidence.json"
    $cellTwoDocument = Get-Content -LiteralPath $cellTwoDocumentPath -Raw | ConvertFrom-Json
    $cellTwoDocument.runnerOs = "Windows"
    $cellTwoDocument.rid = "win-x64"
    $cellTwoDocument.$provenanceField = if ($provenanceField -eq "selectedBaselineCommit") { "1111111111111111111111111111111111111111" } elseif ($provenanceField -eq "protocolCommit") { "2222222222222222222222222222222222222222" } elseif ($provenanceField -eq "fixtureCommit") { "3333333333333333333333333333333333333333" } else { "4444444444444444444444444444444444444444444444444444444444444444" }
    [IO.File]::WriteAllText($cellTwoDocumentPath, ($cellTwoDocument | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    $provenanceOutput = Join-Path $provenanceRoot "aggregate.json"
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "aggregate-m0.7.ps1") -EvidenceRoot $provenanceRoot -OutputPath $provenanceOutput -ManifestPath $syntheticManifestPath 2>&1 | Out-Null
    $expectedProvenanceOutcome = if ($provenanceField -eq "selectedBaselineCommit") { "baseline-invalidated" } else { "protocol-failure" }
    if ($LASTEXITCODE -eq 0 -or (Get-Content -Raw $provenanceOutput | ConvertFrom-Json).aggregateOutcome -ne $expectedProvenanceOutcome) { throw "Aggregate provenance mismatch vector was misclassified." }
}

foreach ($validateResult in @("failure", "cancelled")) {
    $workflowResultRoot = Join-Path $root ("workflow-result-{0}" -f $validateResult)
    Write-SyntheticCellEvidence (Join-Path $workflowResultRoot "cell-1") $true 2 $true
    Write-SyntheticCellEvidence (Join-Path $workflowResultRoot "cell-2") $true 2 $true
    $workflowResultOutput = Join-Path $workflowResultRoot "aggregate.json"
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "aggregate-m0.7.ps1") -EvidenceRoot $workflowResultRoot -OutputPath $workflowResultOutput -ValidateResult $validateResult -ManifestPath $syntheticManifestPath 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { throw "Aggregate workflow-result vector unexpectedly succeeded for $validateResult." }
    $workflowAggregate = Get-Content -Raw $workflowResultOutput | ConvertFrom-Json
    if ($workflowAggregate.aggregateOutcome -eq "succeeded" -or $workflowAggregate.reasonCode -ne "required-cell-validation-incomplete") { throw "Aggregate workflow-result vector produced an invalid result for $validateResult." }
}

Remove-Item -LiteralPath $root -Recurse -Force
Write-Output "M0.7 aggregate outcome vectors passed: baseline-invalidated, protocol-failure, baseline-failure, and inconclusive precedence are retained."
