[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaselineRepositoryPath,
    [Parameter(Mandatory = $true)]
    [string]$FixtureRepositoryPath,
    [Parameter(Mandatory = $true)]
    [string]$BaselineCommit,
    [string]$Configuration = "Release",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$baselineRoot = (Resolve-Path -LiteralPath $BaselineRepositoryPath).Path
$fixtureRoot = (Resolve-Path -LiteralPath $FixtureRepositoryPath).Path
$manifestPath = Join-Path $repositoryRoot "tests\fixtures\roslyn-msbuild\m0.7-independent-validation-manifest.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "TestResults\m0.7-independent-validation"
}

function Write-FailureEvidence([string]$message) {
    $outcome = "protocol-failure"
    $reasonCode = "unexpected-verifier-error"
    if ($message -match "baseline checkout|selected-baseline|semantic source|transfer manifest|SDK policy|roll-forward|package baseline|frozen host") {
        $outcome = "baseline-invalidated"
        $reasonCode = "selected-baseline-drift"
    }
    elseif ($message -match "host was not built|SDK version|toolchain|infrastructure|inconclusive|cancelled|timeout|timed-out|incomplete") {
        $outcome = "inconclusive"
        $reasonCode = "required-cell-inconclusive"
    }
    elseif ($message -match "selected baseline did not complete|result envelope|semantic payload|fresh selected-baseline") {
        $outcome = "baseline-failure"
        $reasonCode = "conforming-baseline-failure"
    }
    elseif ($message -match "fixture|oracle|protected|public|BOM|trailing newline|inventory|manifest|hash") {
        $outcome = "protocol-failure"
        $reasonCode = "protocol-input-invalid"
    }
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
    $failure = [ordered]@{
        formatVersion = "contractscribe-m0.7-failure-evidence-v1"
        aggregateOutcome = $outcome
        reasonCode = $reasonCode
        selectedBaselineCommit = if ($null -ne $manifest) { $manifest.selectedBaseline.commit } else { $BaselineCommit }
        fixtureCommit = if ($null -ne $manifest) { $manifest.fixture.commit } else { $null }
        protocolCommit = $null
        ci = [ordered]@{ runId = $env:GITHUB_RUN_ID; job = $env:GITHUB_JOB; sha = $env:GITHUB_SHA }
        retainedFailure = $true
    }
    $failurePath = Join-Path $OutputRoot "m0.7-failure-evidence.json"
    $utf8NoBom = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($failurePath, ($failure | ConvertTo-Json -Depth 10), $utf8NoBom)
    Write-Output "M0.7 validation failed: $outcome ($reasonCode)."
}

trap {
    Write-FailureEvidence $_.Exception.Message
    exit 1
}

function Assert-Condition([bool]$condition, [string]$message) {
    if (-not $condition) {
        throw $message
    }
}

function Get-FileSha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-GitHead([string]$path) {
    Push-Location $path
    try {
        $head = (& git rev-parse HEAD).Trim()
        Assert-Condition ($LASTEXITCODE -eq 0 -and $head -match "^[0-9a-f]{40}$") "The repository HEAD could not be resolved."
        return $head
    }
    finally {
        Pop-Location
    }
}

function Read-Json([string]$path) {
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Get-CanonicalUtf8Bytes([string]$path) {
    return [IO.File]::ReadAllBytes($path)
}

function Assert-ByteEqual([byte[]]$expected, [byte[]]$actual, [string]$message) {
    Assert-Condition ($expected.Length -eq $actual.Length) $message
    for ($index = 0; $index -lt $expected.Length; $index++) {
        Assert-Condition ($expected[$index] -eq $actual[$index]) $message
    }
}

Assert-Condition ($manifest.formatVersion -eq "contractscribe-m0.7-validation-v1") "The M0.7 validation manifest version is unsupported."
Assert-Condition ($BaselineCommit -eq $manifest.selectedBaseline.commit) "The supplied selected-baseline commit does not match the M0.7 manifest."
Assert-Condition ((Get-GitHead $baselineRoot) -eq $manifest.selectedBaseline.commit) "The baseline checkout is not the selected M0.6 commit."
Assert-Condition ((Get-GitHead $fixtureRoot) -eq $manifest.fixture.commit) "The fixture checkout is not the pinned independent commit."

$baselineTransferManifestPath = Join-Path $baselineRoot "tests\fixtures\roslyn-msbuild\v1\transfer-manifest.json"
Assert-Condition ((Get-FileSha256 $baselineTransferManifestPath) -eq $manifest.selectedBaseline.transferManifestSha256) "The selected-baseline transfer manifest hash does not match."
$baselineTransferManifest = Read-Json $baselineTransferManifestPath
Assert-Condition ($baselineTransferManifest.sourceRevision -eq $manifest.selectedBaseline.semanticSourceRevision) "The selected-baseline semantic source revision does not match."
Assert-Condition ($baselineTransferManifest.sdk.globalJsonVersion -eq $manifest.matrix.sdkVersion) "The selected-baseline SDK policy does not match."
Assert-Condition ($baselineTransferManifest.sdk.rollForward -eq $manifest.matrix.rollForward) "The selected-baseline roll-forward policy does not match."
Assert-Condition ($baselineTransferManifest.packages.'System.Security.Cryptography.Xml' -eq $manifest.matrix.securityXmlPackageVersion) "The selected-baseline package baseline does not match."

$fixtureManifestPath = Join-Path $fixtureRoot "fixture-manifest.json"
$fixtureManifest = Read-Json $fixtureManifestPath
Assert-Condition ($fixtureManifest.formatVersion -eq "contractscribe-m0.7-fixture-v1") "The independent fixture manifest version is unsupported."
Assert-Condition ($fixtureManifest.ownership.owner -eq "Yuee98") "The independent fixture owner is not the reviewed public owner."
Assert-Condition ($fixtureManifest.ownership.publicRepository -eq $true) "The independent fixture is not declared public."
Assert-Condition (-not [string]::IsNullOrWhiteSpace($fixtureManifest.ownership.availabilityExpectation)) "The independent fixture availability expectation is missing."
Assert-Condition ($fixtureManifest.ownership.trackingIssue -eq "https://github.com/Yuee98/contract-scribe-m07-fixture/issues/1") "The independent fixture tracking issue is not pinned."
Assert-Condition ($fixtureManifest.independence.sourceAuthoredFromScratch) "The independent fixture source provenance is not declared."
Assert-Condition ($fixtureManifest.independence.oracleAuthoredBeforeSelectedBaselineExecution) "The independent oracle was not pinned before selected-baseline execution."
Assert-Condition (-not $fixtureManifest.independence.oracleDerivedFromRunnerOutput) "The independent oracle is declared as runner-derived."
Assert-Condition (-not $fixtureManifest.independence.m04SourceCopied -and -not $fixtureManifest.independence.m04OracleCopied) "The fixture declares copied M0.4 material."
Assert-Condition ($fixtureManifest.preRunReview.independenceReviewed -and $fixtureManifest.preRunReview.publicSafetyReviewed -and $fixtureManifest.preRunReview.oracleReviewedBeforeSelectedBaselineExecution) "The fixture pre-run independence/public-safety review is incomplete."
Assert-Condition ($fixtureManifest.compilation.targetFramework -eq "net10.0") "The fixture target framework is outside the M0.7 contract."
Assert-Condition ($fixtureManifest.compilation.langVersion -eq "latest") "The fixture language version is outside the M0.7 contract."
Assert-Condition ($fixtureManifest.compilation.nullable -eq "enable") "The fixture nullable policy is outside the M0.7 contract."
Assert-Condition ($fixtureManifest.compilation.implicitUsings -eq "disable") "The fixture implicit-usings policy is outside the M0.7 contract."
Assert-Condition (@($fixtureManifest.compilation.packageReferences | Where-Object { $null -ne $_ }).Count -eq 0) "The fixture declares an external package reference."
Assert-Condition ($fixtureManifest.oracle.path -eq "expected-payload.json") "The fixture oracle path is not the committed canonical payload."
Assert-Condition ($fixtureManifest.oracle.sha256 -eq $manifest.fixture.oracleSha256) "The fixture manifest oracle hash does not match the ContractScribe manifest."
Assert-Condition (-not [string]::IsNullOrWhiteSpace($fixtureManifest.oracle.correctionRule)) "The fixture oracle correction rule is missing."

$fixtureFiles = @($manifest.fixture.fileSha256.PSObject.Properties)
foreach ($entry in $fixtureFiles) {
    $path = Join-Path $fixtureRoot $entry.Name
    Assert-Condition (Test-Path -LiteralPath $path) "A pinned fixture file is missing."
    Assert-Condition ((Get-FileSha256 $path) -eq $entry.Value) "A pinned fixture file hash does not match."
}

$protectedFiles = @($fixtureManifest.protectedFiles.PSObject.Properties)
Assert-Condition ($protectedFiles.Count -ge 10) "The independent fixture protected file inventory is incomplete."
Assert-Condition ($null -ne ($protectedFiles | Where-Object { $_.Name -eq ".gitattributes" })) "The independent fixture protected file inventory does not include .gitattributes."
foreach ($entry in $protectedFiles) {
    $path = Join-Path $fixtureRoot $entry.Name
    Assert-Condition (Test-Path -LiteralPath $path) "A protected fixture file is missing."
    Assert-Condition ((Get-FileSha256 $path) -eq $entry.Value) "A protected fixture file hash does not match."
}
$allowedFixtureFiles = @($protectedFiles.Name) + "fixture-manifest.json"
$actualFixtureFiles = @(Get-ChildItem -LiteralPath $fixtureRoot -File -Recurse | Where-Object {
    $_.FullName -notmatch "[/\\](?:\.git|bin|obj)(?:[/\\]|$)"
} | ForEach-Object {
    $_.FullName.Substring($fixtureRoot.Length + 1).Replace("\", "/")
})
foreach ($path in $actualFixtureFiles) {
    Assert-Condition ($allowedFixtureFiles -contains $path) "The independent fixture contains an unlisted public file."
}
Assert-Condition ((($actualFixtureFiles | Sort-Object) -join ",") -eq (($allowedFixtureFiles | Sort-Object) -join ",")) "The independent fixture protected file inventory is not closed."

$solutionPath = Join-Path $fixtureRoot $fixtureManifest.solution
$oraclePath = Join-Path $fixtureRoot $fixtureManifest.oracle.path
Assert-Condition (Test-Path -LiteralPath $solutionPath) "The pinned independent solution is missing."
Assert-Condition (Test-Path -LiteralPath $oraclePath) "The pinned independent oracle is missing."
$oracleBytes = Get-CanonicalUtf8Bytes $oraclePath
Assert-Condition ((Get-FileSha256 $oraclePath) -eq $manifest.fixture.oracleSha256) "The independent oracle hash does not match."
Assert-Condition ($oracleBytes.Length -gt 0) "The independent oracle is empty."
Assert-Condition (-not ($oracleBytes[0] -eq 0xEF -and $oracleBytes[1] -eq 0xBB -and $oracleBytes[2] -eq 0xBF)) "The independent oracle has a BOM."
$oracleCanonicalBytes = $oracleBytes
Assert-Condition ($oracleCanonicalBytes[$oracleCanonicalBytes.Length - 1] -ne 0x0A -and $oracleCanonicalBytes[$oracleCanonicalBytes.Length - 1] -ne 0x0D) "The independent oracle has a trailing newline."
$oracle = Read-Json $oraclePath
Assert-Condition (@($oracle.projects).Count -eq 2) "The independent oracle does not contain exactly two projects."
Assert-Condition ((@($oracle.projects | ForEach-Object { $_.projectId }) -join ",") -eq "SampleApp,SampleLibrary") "The independent oracle project order is invalid."

$appProjectXml = [xml](Get-Content -LiteralPath (Join-Path $fixtureRoot "SampleApp\SampleApp.csproj") -Raw)
$libraryProjectXml = [xml](Get-Content -LiteralPath (Join-Path $fixtureRoot "SampleLibrary\SampleLibrary.csproj") -Raw)
Assert-Condition ($appProjectXml.Project.PropertyGroup.TargetFramework -eq "net10.0") "The independent SampleApp target framework is invalid."
Assert-Condition ($libraryProjectXml.Project.PropertyGroup.TargetFramework -eq "net10.0") "The independent SampleLibrary target framework is invalid."
$packageReferences = @($appProjectXml.Project.ItemGroup.PackageReference, $libraryProjectXml.Project.ItemGroup.PackageReference) | Where-Object { $null -ne $_ }
Assert-Condition ($packageReferences.Count -eq 0) "The independent fixture contains a package reference."
$projectReferences = @($appProjectXml.Project.ItemGroup.ProjectReference)
Assert-Condition ($projectReferences.Count -eq 1) "The independent fixture does not contain exactly one project reference."

$hostPath = Join-Path $baselineRoot "tests\ContractScribe.Roslyn.Experiment\bin\$Configuration\net10.0\ContractScribe.Roslyn.Experiment.dll"
Assert-Condition (Test-Path -LiteralPath $hostPath) "The selected-baseline experiment host was not built."
$resolvedSdkVersion = (& dotnet --version).Trim()
Assert-Condition ($resolvedSdkVersion -match "^10\.0\.\d+$") "The selected SDK version is outside the repository policy family."
$rid = if ($IsWindows) { "win-x64" } else { "linux-x64" }
$runnerOs = if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_OS)) { $env:RUNNER_OS } elseif ($IsWindows) { "Windows" } else { "Linux" }
$runDirectories = @()
$runRecords = @()

if (Test-Path -LiteralPath $OutputRoot) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputRoot | Out-Null

for ($run = 1; $run -le $manifest.comparison.freshProcessCount; $run++) {
    $runDirectory = Join-Path $OutputRoot "run-$run"
    New-Item -ItemType Directory -Path $runDirectory | Out-Null
    $stdoutPath = Join-Path $runDirectory "stdout.txt"
    $stderrPath = Join-Path $runDirectory "stderr.txt"
    & dotnet $hostPath $solutionPath $runDirectory 1> $stdoutPath 2> $stderrPath
    $exitCode = $LASTEXITCODE
    Assert-Condition ($exitCode -eq 0) "The selected baseline did not complete the independent fixture smoke."
    $resultPath = Join-Path $runDirectory "result.json"
    $payloadPath = Join-Path $runDirectory "semantic-payload.json"
    Assert-Condition (Test-Path -LiteralPath $resultPath) "The selected baseline result envelope is missing."
    Assert-Condition (Test-Path -LiteralPath $payloadPath) "The selected baseline semantic payload is missing."
    $result = Read-Json $resultPath
    Assert-Condition ($result.status -eq "succeeded") "The selected baseline result envelope did not report success."
    Assert-Condition ($result.toolchain.sdkVersion -eq $resolvedSdkVersion) "The selected baseline result does not report the actually selected SDK."
    Assert-Condition ($result.toolchain.processArchitecture -eq "X64") "The selected baseline process architecture is not X64."
    Assert-Condition ($result.toolchain.discoveryType -eq "DotNetSdk") "The selected baseline did not use DotNetSdk discovery."
    $payloadBytes = Get-CanonicalUtf8Bytes $payloadPath
    Assert-ByteEqual $oracleCanonicalBytes $payloadBytes "The selected baseline output does not match the independent oracle canonical bytes byte-for-byte."
    Assert-Condition ($payloadBytes.Length -gt 0) "The selected baseline semantic payload is empty."
    Assert-Condition (-not ($payloadBytes[0] -eq 0xEF -and $payloadBytes[1] -eq 0xBB -and $payloadBytes[2] -eq 0xBF)) "The selected baseline semantic payload has a BOM."
    Assert-Condition ($payloadBytes[$payloadBytes.Length - 1] -ne 0x0A -and $payloadBytes[$payloadBytes.Length - 1] -ne 0x0D) "The selected baseline semantic payload has a trailing newline."
    $publicOutput = (Get-Content -LiteralPath $stdoutPath -Raw) + (Get-Content -LiteralPath $stderrPath -Raw) + (Get-Content -LiteralPath $resultPath -Raw)
    Assert-Condition ($publicOutput -notmatch "(?i)([A-Z]:\\|/home/|/Users/|authorization|bearer\s|access[_-]?token|api[_-]?key|password\s*=)") "The selected baseline public output contains a path or credential-like value."
    $runDirectories += $runDirectory
    $runRecords += [pscustomobject]@{
        run = $run
        exitCode = $exitCode
        payloadSha256 = Get-FileSha256 $payloadPath
        sdkVersion = $result.toolchain.sdkVersion
        msbuildVersion = $result.toolchain.msbuildVersion
        runtimeVersion = $result.toolchain.runtimeVersion
        processArchitecture = $result.toolchain.processArchitecture
        oracleMatch = $true
    }
}

$firstPayload = Get-CanonicalUtf8Bytes (Join-Path $runDirectories[0] "semantic-payload.json")
foreach ($directory in $runDirectories[1..($runDirectories.Count - 1)]) {
    Assert-ByteEqual $firstPayload (Get-CanonicalUtf8Bytes (Join-Path $directory "semantic-payload.json")) "Fresh selected-baseline processes are not byte-identical."
}

$evidence = [ordered]@{
    formatVersion = "contractscribe-m0.7-evidence-v1"
    aggregateOutcome = "succeeded"
    runnerOs = $runnerOs
    rid = $rid
    selectedBaselineCommit = Get-GitHead $baselineRoot
    protocolCommit = Get-GitHead $repositoryRoot
    semanticSourceRevision = $manifest.selectedBaseline.semanticSourceRevision
    transferManifestSha256 = $manifest.selectedBaseline.transferManifestSha256
    fixtureRepository = $manifest.fixture.repository
    fixtureCommit = Get-GitHead $fixtureRoot
    oracleSha256 = $manifest.fixture.oracleSha256
    comparison = [ordered]@{
        artifact = "semantic-payload.json"
        encoding = "utf-8-no-bom"
        trailingNewline = $false
        equality = "byte-equality"
        freshProcessCount = $manifest.comparison.freshProcessCount
        crossRunEquality = $true
        oracleEquality = $true
    }
    commands = $manifest.commands
    runs = $runRecords
    ci = [ordered]@{
        runId = $env:GITHUB_RUN_ID
        job = $env:GITHUB_JOB
        sha = $env:GITHUB_SHA
    }
    unresolvedRisks = @($manifest.unresolvedRisks)
}
$evidencePath = Join-Path $OutputRoot "m0.7-evidence.json"
$utf8NoBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($evidencePath, ($evidence | ConvertTo-Json -Depth 10), $utf8NoBom)
Write-Output "M0.7 independent validation succeeded: pinned baseline, independent oracle, two fresh processes, byte equality, and public-safety checks passed."
