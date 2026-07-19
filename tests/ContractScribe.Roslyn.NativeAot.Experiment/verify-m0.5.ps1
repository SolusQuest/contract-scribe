[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("linux-x64", "win-x64")]
    [string]$RuntimeIdentifier,
    [ValidateSet("Release")]
    [string]$Configuration = "Release",
    [string]$EvidencePath,
    [switch]$EvidenceReproduction
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$fixtureRoot = Join-Path $repositoryRoot "tests\fixtures\roslyn-msbuild\v1"
$manifestPath = Join-Path $fixtureRoot "m0.5-native-aot-manifest.json"
$m04ManifestPath = Join-Path $fixtureRoot "transfer-manifest.json"
$solutionPath = Join-Path $fixtureRoot "Sample.sln"
$nativeProjectPath = Join-Path $repositoryRoot "tests\ContractScribe.Roslyn.NativeAot.Experiment\ContractScribe.Roslyn.NativeAot.Experiment.csproj"
$m04VerifierPath = Join-Path $repositoryRoot "tests\ContractScribe.Roslyn.Experiment\verify-m0.4.ps1"
$protocolRoot = Join-Path $repositoryRoot ("TestResults\m0.5-protocol\" + $RuntimeIdentifier)
$defaultEvidencePath = Join-Path $fixtureRoot ("evidence\m0.5-" + $RuntimeIdentifier + "-evidence-v1.json")
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = $defaultEvidencePath
}
$EvidencePath = [IO.Path]::GetFullPath($EvidencePath)

function Assert-Condition([bool]$condition, [string]$message) {
    if (-not $condition) {
        Write-Output "M0.5 protocol failure: $message"
        exit 1
    }
}

$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $fixtureRoot "evidence")).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
Assert-Condition ($EvidencePath.StartsWith($evidenceRoot, [StringComparison]::OrdinalIgnoreCase)) "The evidence path is outside the controlled evidence directory."
Assert-Condition ([IO.Path]::GetFileName($EvidencePath) -eq ("m0.5-" + $RuntimeIdentifier + "-evidence-v1.json")) "The evidence path is not the closed cell evidence path."

function Get-Sha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-CanonicalJson([string]$path, [object]$value) {
    $directory = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $json = $value | ConvertTo-Json -Depth 20 -Compress
    [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
}

function Invoke-CapturedProcess([string]$fileName, [string[]]$arguments, [string]$workingDirectory) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $fileName
    $startInfo.WorkingDirectory = $workingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    try {
        $process = [Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) {
            return [pscustomobject]@{ ExitCode = 900; Stdout = ""; Stderr = "process-start-failed" }
        }
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout; Stderr = $stderr }
    }
    catch {
        return [pscustomobject]@{ ExitCode = 900; Stdout = ""; Stderr = "process-start-failed" }
    }
}

function Remove-CellOutputs {
    $safeRoot = [IO.Path]::GetFullPath($repositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $target = [IO.Path]::GetFullPath($protocolRoot)
    Assert-Condition ($target.StartsWith($safeRoot, [StringComparison]::OrdinalIgnoreCase)) "The protocol output path is outside the repository."
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
}

function Get-RunnerOs {
    if ($env:RUNNER_OS -eq "Ubuntu" -or $env:RUNNER_OS -eq "Linux") { return "Ubuntu" }
    if ($env:RUNNER_OS -eq "Windows") { return "Windows" }
    if ($IsLinux) { return "Ubuntu" }
    if ($IsWindows) { return "Windows" }
    return "Unknown"
}

function Get-ExpectedRunnerOs([string]$rid) {
    if ($rid -eq "linux-x64") { return "Ubuntu" }
    return "Windows"
}

function Get-ProcessArchitecture {
    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [Runtime.InteropServices.Architecture]::X64) {
        return "X64"
    }
    return "Unknown"
}

function Get-Toolchain([string]$runnerOs, [string]$rid) {
    $sdk = (& dotnet --version).Trim()
    if ($sdk -notmatch "^\d+\.\d+\.\d+$") { $sdk = "0.0.0" }
    $runtime = [Environment]::Version.ToString()
    if ($runtime -notmatch "^\d+\.\d+\.\d+$") { $runtime = "0.0.0" }
    return [ordered]@{
        sdkVersion = $sdk
        runtimeVersion = $runtime
        msbuildVersion = "unknown"
        nativeCompilerId = if ($runnerOs -eq "Windows") { "msvc" } else { "clang" }
        nativeCompilerVersion = "unknown"
        linkerId = if ($runnerOs -eq "Windows") { "link" } else { "lld" }
        linkerVersion = "unknown"
        runnerOs = $runnerOs
        rid = $rid
        processArchitecture = Get-ProcessArchitecture
    }
}

function Get-Profile([string]$rid) {
    return [ordered]@{
        targetFramework = "net10.0"
        configuration = $Configuration
        publishAot = $true
        selfContained = $true
        publishTrimmed = $true
        runtimeIdentifier = $rid
    }
}

function Get-CommandList {
    return @(
        @("dotnet", "restore", "ContractScribe.slnx"),
        @("dotnet", "build", "ContractScribe.slnx", "--configuration", "Release", "--no-restore"),
        @("pwsh", "-NoProfile", "-File", "tests/ContractScribe.Roslyn.Experiment/verify-m0.4.ps1", "-Configuration", "Release", "-M05ManifestPath", "tests/fixtures/roslyn-msbuild/v1/m0.5-native-aot-manifest.json"),
        @("dotnet", "restore", "tests/ContractScribe.Roslyn.NativeAot.Experiment/ContractScribe.Roslyn.NativeAot.Experiment.csproj", "--runtime", $RuntimeIdentifier),
        @("dotnet", "publish", "tests/ContractScribe.Roslyn.NativeAot.Experiment/ContractScribe.Roslyn.NativeAot.Experiment.csproj", "--configuration", "Release", "--runtime", $RuntimeIdentifier, "--no-restore"),
        @("native", "tests/fixtures/roslyn-msbuild/v1/Sample.sln", "cell-output")
    )
}

function Get-ErrorClassification([string]$text) {
    $lower = $text.ToLowerInvariant()
    if ($lower -match "platform linker not found|desktop development for c\+\+|nativeaot-prerequisites|native compiler.*not found") {
        return [pscustomobject]@{ Phase = "preflight"; Cause = "native-toolchain"; Code = "preflight.native-toolchain-unavailable"; Outcome = "inconclusive" }
    }
    if ($lower -match "rid.*asset|runtime pack|nuget.*rid|restore.*failed") {
        return [pscustomobject]@{ Phase = "restore"; Cause = "rid-assets"; Code = "restore.rid-assets-unavailable"; Outcome = "inconclusive" }
    }
    if ($lower -match "trim|reflection|requiresdynamiccode|requiresunreferencedcode") {
        return [pscustomobject]@{ Phase = "publish"; Cause = "trimming-reflection"; Code = "publish.trimming-analysis-failed"; Outcome = "not-feasible" }
    }
    if ($lower -match "aot|ilcompiler|rdxml|dynamic assembly") {
        return [pscustomobject]@{ Phase = "publish"; Cause = "aot-analysis"; Code = "publish.aot-analysis-failed"; Outcome = "not-feasible" }
    }
    return [pscustomobject]@{ Phase = "native-link"; Cause = "unknown"; Code = "native-link.linker-failed"; Outcome = "inconclusive" }
}

function Get-InnerClassification([string]$resultPath) {
    if (-not (Test-Path -LiteralPath $resultPath)) {
        return [pscustomobject]@{ Protocol = $false; Phase = "launch"; Cause = "unknown"; Code = "launch.native-process-failed"; Outcome = "inconclusive" }
    }
    try {
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    }
    catch {
        return [pscustomobject]@{ Protocol = $true; ProtocolCode = "evidence.artifact-malformed" }
    }
    if ($result.status -eq "succeeded" -and $null -eq $result.failurePhase -and $null -eq $result.failureCode) {
        return [pscustomobject]@{ Protocol = $false; Succeeded = $true }
    }
    if ($result.status -eq "internal-error" -and $null -eq $result.failurePhase -and $null -eq $result.failureCode) {
        return [pscustomobject]@{ Protocol = $false; Phase = "semantic-path"; Cause = "unknown"; Code = "semantic-path.inner-internal-error"; Outcome = "inconclusive" }
    }
    if ($result.status -eq "invalid-input" -and $null -ne $result.failurePhase -and $null -ne $result.failureCode) {
        return [pscustomobject]@{ Protocol = $true; ProtocolCode = "evidence.inner-contract-invalid" }
    }
    if ($result.status -ne "classified-failure" -or $null -eq $result.failurePhase -or $null -eq $result.failureCode) {
        return [pscustomobject]@{ Protocol = $true; ProtocolCode = "evidence.inner-contract-invalid" }
    }
    $code = [string]$result.failureCode
    switch -Regex ($code) {
        "^msbuild\.sdk-unavailable$" { return [pscustomobject]@{ Protocol = $false; Phase = "semantic-path"; Cause = "sdk-resolution"; Code = "semantic-path.sdk-resolution-failed"; Outcome = "inconclusive" } }
        "^msbuild\.registration-failed$" { return [pscustomobject]@{ Protocol = $false; Phase = "semantic-path"; Cause = "msbuild-host"; Code = "semantic-path.msbuild-host-incompatible"; Outcome = "not-feasible" } }
        "^(workspace|compilation|symbol|serialization)\." { return [pscustomobject]@{ Protocol = $false; Phase = "semantic-path"; Cause = "unknown"; Code = "semantic-path.inner-classified-failure"; Outcome = "inconclusive" } }
        default { return [pscustomobject]@{ Protocol = $true; ProtocolCode = "evidence.inner-contract-invalid" } }
    }
}

function New-CellRecord([string]$outcome, [string]$phase, [string]$cause, [string]$code, [object]$comparison, [object[]]$observations, [object]$toolchain, [object[]]$commands) {
    $record = [ordered]@{
        evidenceVersion = "m0.5-native-aot-evidence-v1"
        recordType = "cell"
        cell = [ordered]@{ runnerOs = $runnerOs; rid = $RuntimeIdentifier; processArchitecture = $processArchitecture }
        profile = Get-Profile $RuntimeIdentifier
        commands = $commands
        warnings = @()
        toolchain = $toolchain
        dependencies = @("m04-fixture", "m04-manifest", "global-json", "native-toolchain")
        outcome = $outcome
        phase = $phase
        cause = $cause
    }
    if (-not [string]::IsNullOrWhiteSpace($code)) {
        $record.code = $code
    }
    $record.comparison = $comparison
    $record.observations = @($observations)
    return $record
}

function New-ProtocolRecord([string]$code, [object]$toolchain, [object[]]$commands) {
    return [ordered]@{
        evidenceVersion = "m0.5-native-aot-evidence-v1"
        recordType = "protocol-failure"
        cell = [ordered]@{ runnerOs = $runnerOs; rid = $RuntimeIdentifier; processArchitecture = $processArchitecture }
        profile = Get-Profile $RuntimeIdentifier
        commands = $commands
        warnings = @()
        toolchain = $toolchain
        dependencies = @("m04-fixture", "m04-manifest", "global-json", "native-toolchain")
        protocolFailure = [ordered]@{ phase = "evidence"; code = $code }
    }
}

function Save-Record([object]$record) {
    Write-CanonicalJson $EvidencePath $record
    $content = [IO.File]::ReadAllText($EvidencePath)
    Assert-Condition ($content -notmatch "(?i)([A-Z]:\\|/home/|/Users/|authorization|bearer\s|access[_-]?token|api[_-]?key|username|hostname|environment)") "The evidence contains a private path, credential-like value, or environment dump."
    $bytes = [IO.File]::ReadAllBytes($EvidencePath)
    Assert-Condition ($bytes.Length -gt 0 -and $bytes[0] -ne 0xEF) "The evidence is empty or has a UTF-8 BOM."
    Assert-Condition ($bytes[$bytes.Length - 1] -ne 0x0A -and $bytes[$bytes.Length - 1] -ne 0x0D) "The evidence has a trailing newline."
}

function Get-Comparison([string]$frameworkPayloadPath, [string]$aotPayloadPath, [bool]$repeatedEqual, [bool]$frameworkEqual) {
    $comparison = [ordered]@{ status = "not-run" }
    if (Test-Path -LiteralPath $frameworkPayloadPath) {
        $comparison.frameworkPayloadSha256 = Get-Sha256 $frameworkPayloadPath
    }
    if (Test-Path -LiteralPath $aotPayloadPath) {
        $comparison.aotPayloadSha256 = Get-Sha256 $aotPayloadPath
    }
    if ($repeatedEqual -or $frameworkEqual) {
        $comparison.status = "compared"
        $comparison.repeatedAotPayloadByteEqual = $repeatedEqual
        $comparison.frameworkByteEqual = $frameworkEqual
    }
    return $comparison
}

$runnerOs = Get-RunnerOs
$expectedRunnerOs = Get-ExpectedRunnerOs $RuntimeIdentifier
$processArchitecture = Get-ProcessArchitecture
Assert-Condition ($runnerOs -eq $expectedRunnerOs) "The runtime identifier does not match the runner OS."
Assert-Condition ($processArchitecture -eq "X64") "The process architecture is not X64."
Assert-Condition (Test-Path -LiteralPath $manifestPath) "The M0.5 manifest is missing."
Assert-Condition (Test-Path -LiteralPath $solutionPath) "The M0.4 fixture solution is missing."
Assert-Condition (Test-Path -LiteralPath $nativeProjectPath) "The Native AOT host project is missing."

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$m04Manifest = Get-Content -LiteralPath $m04ManifestPath -Raw | ConvertFrom-Json
Assert-Condition ($manifest.m04ManifestSha256 -eq (Get-Sha256 $m04ManifestPath)) "The M0.5 manifest does not match the M0.4 transfer manifest."
Assert-Condition ($manifest.m04FrozenSourceRevision -eq $m04Manifest.sourceRevision) "The M0.5 manifest does not match the frozen M0.4 source revision."
Assert-Condition ($manifest.publishProfile.targetFramework -eq "net10.0" -and $manifest.publishProfile.configuration -eq "Release" -and $manifest.publishProfile.publishAot -eq $true -and $manifest.publishProfile.selfContained -eq $true -and $manifest.publishProfile.publishTrimmed -eq $true) "The publish profile is not the closed M0.5 profile."
Assert-Condition ($manifest.requiredMatrix.rid -contains $RuntimeIdentifier) "The runtime identifier is outside the required matrix."
Assert-Condition ($manifest.evidenceSchemaSha256 -eq (Get-Sha256 (Join-Path $repositoryRoot $manifest.evidenceSchemaPath))) "The M0.5 evidence schema hash is stale."
Assert-Condition ($manifest.registrySha256 -eq (Get-Sha256 (Join-Path $repositoryRoot $manifest.registryPath))) "The M0.5 registry hash is stale."
foreach ($entry in $manifest.implementationInputHashes.PSObject.Properties) {
    $inputPath = Join-Path $repositoryRoot $entry.Name
    Assert-Condition (Test-Path -LiteralPath $inputPath) "An M0.5 implementation input is missing."
    Assert-Condition ($entry.Value -eq (Get-Sha256 $inputPath)) "An M0.5 implementation input hash is stale."
}

Remove-CellOutputs
$toolchain = Get-Toolchain $runnerOs $RuntimeIdentifier
$commands = Get-CommandList
$frameworkPayloadPath = Join-Path $repositoryRoot "TestResults\m0.4-protocol\run-1\semantic-payload.json"

$restoreArgs = @("restore", "ContractScribe.slnx")
$restoreFirst = Invoke-CapturedProcess "dotnet" $restoreArgs $repositoryRoot
if ($restoreFirst.ExitCode -ne 0) {
    Remove-CellOutputs
    $restoreSecond = Invoke-CapturedProcess "dotnet" $restoreArgs $repositoryRoot
    if ($restoreSecond.ExitCode -ne 0) {
        $classification = Get-ErrorClassification ($restoreFirst.Stdout + $restoreFirst.Stderr + $restoreSecond.Stdout + $restoreSecond.Stderr)
        $record = New-CellRecord "inconclusive" "restore" $classification.Cause $classification.Code (Get-Comparison $frameworkPayloadPath "" $false $false) @() $toolchain $commands
        Save-Record $record
        Write-Output "M0.5 cell inconclusive: restore did not reproduce."
        exit 1
    }
}

$build = Invoke-CapturedProcess "dotnet" @("build", "ContractScribe.slnx", "--configuration", "Release", "--no-restore") $repositoryRoot
if ($build.ExitCode -ne 0) {
    $record = New-CellRecord "inconclusive" "semantic-path" "unknown" "semantic-path.inner-classified-failure" (Get-Comparison $frameworkPayloadPath "" $false $false) @() $toolchain $commands
    Save-Record $record
    Write-Output "M0.5 cell inconclusive: normal solution build failed."
    exit 1
}

$m04 = Invoke-CapturedProcess "pwsh" @("-NoProfile", "-File", $m04VerifierPath, "-Configuration", "Release", "-M05ManifestPath", $manifestPath) $repositoryRoot
if ($m04.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $frameworkPayloadPath)) {
    $record = New-CellRecord "inconclusive" "semantic-path" "unknown" "semantic-path.inner-classified-failure" (Get-Comparison $frameworkPayloadPath "" $false $false) @() $toolchain $commands
    Save-Record $record
    Write-Output "M0.5 cell inconclusive: the frozen M0.4 baseline did not verify."
    exit 1
}

$nativeRestoreArgs = @("restore", $nativeProjectPath, "--runtime", $RuntimeIdentifier)
$nativeRestoreFirst = Invoke-CapturedProcess "dotnet" $nativeRestoreArgs $repositoryRoot
if ($nativeRestoreFirst.ExitCode -ne 0) {
    Remove-Item -LiteralPath (Join-Path $repositoryRoot "tests\ContractScribe.Roslyn.NativeAot.Experiment\obj") -Recurse -Force -ErrorAction SilentlyContinue
    $nativeRestoreSecond = Invoke-CapturedProcess "dotnet" $nativeRestoreArgs $repositoryRoot
    if ($nativeRestoreSecond.ExitCode -ne 0) {
        $classification = Get-ErrorClassification ($nativeRestoreFirst.Stdout + $nativeRestoreFirst.Stderr + $nativeRestoreSecond.Stdout + $nativeRestoreSecond.Stderr)
        $record = New-CellRecord "inconclusive" $classification.Phase $classification.Cause $classification.Code (Get-Comparison $frameworkPayloadPath "" $false $false) @() $toolchain $commands
        Save-Record $record
        Write-Output "M0.5 cell inconclusive: RID-specific restore did not reproduce."
        exit 1
    }
}

$publishDirectories = @()
$publishResults = @()
for ($attempt = 1; $attempt -le 2; $attempt++) {
    $publishDirectory = Join-Path $protocolRoot ("publish-" + $attempt)
    $publishDirectories += $publishDirectory
    if (Test-Path -LiteralPath $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    $publishArgs = @("publish", $nativeProjectPath, "--configuration", "Release", "--runtime", $RuntimeIdentifier, "--no-restore", "--output", $publishDirectory)
    $publishResults += Invoke-CapturedProcess "dotnet" $publishArgs $repositoryRoot
    if ($publishResults[-1].ExitCode -eq 0) { break }
}

if ($publishResults.Count -eq 0 -or $publishResults[-1].ExitCode -ne 0) {
    $classifications = @($publishResults | ForEach-Object { Get-ErrorClassification ($_.Stdout + $_.Stderr) })
    $classification = $classifications[0]
    if ($classifications.Count -ne 2 -or $classifications[0].Code -ne $classifications[1].Code -or $classifications[0].Cause -ne $classifications[1].Cause) {
        $classification = [pscustomobject]@{ Phase = "native-link"; Cause = "unknown"; Code = "native-link.linker-failed"; Outcome = "inconclusive" }
    }
    $record = New-CellRecord $classification.Outcome $classification.Phase $classification.Cause $classification.Code (Get-Comparison $frameworkPayloadPath "" $false $false) @([ordered]@{ phase = $classification.Phase; cause = $classification.Cause; code = $classification.Code }) $toolchain $commands
    Save-Record $record
    if ($classification.Outcome -eq "not-feasible") { Write-Output "M0.5 cell conclusive negative: $($classification.Code)."; exit 0 }
    Write-Output "M0.5 cell inconclusive: publish did not produce a reproducible native artifact."
    exit 1
}

$nativeBinaryName = "ContractScribe.Roslyn.NativeAot.Experiment" + $(if ($RuntimeIdentifier -eq "win-x64") { ".exe" } else { "" })
$nativeBinaryPath = Join-Path $publishDirectories[-1] $nativeBinaryName
if (-not (Test-Path -LiteralPath $nativeBinaryPath)) {
    $record = New-ProtocolRecord "evidence.artifact-missing" $toolchain $commands
    Save-Record $record
    Write-Output "M0.5 protocol failure: native executable artifact is missing."
    exit 1
}
$aotPayloadPaths = @()
$aotResults = @()
for ($run = 1; $run -le 2; $run++) {
    $runDirectory = Join-Path $protocolRoot ("native-run-" + $run)
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    $nativeRun = Invoke-CapturedProcess $nativeBinaryPath @($solutionPath, $runDirectory) $repositoryRoot
    $inner = Get-InnerClassification (Join-Path $runDirectory "result.json")
    if ($inner.Protocol) {
        $record = New-ProtocolRecord $inner.ProtocolCode $toolchain $commands
        Save-Record $record
        Write-Output "M0.5 protocol failure: $($inner.ProtocolCode)."
        exit 1
    }
    $aotResults += [pscustomobject]@{ Process = $nativeRun; Inner = $inner; RunDirectory = $runDirectory }
    if ($nativeRun.ExitCode -eq 0 -and $inner.Succeeded) {
        $payloadPath = Join-Path $runDirectory "semantic-payload.json"
        if (-not (Test-Path -LiteralPath $payloadPath)) {
            $record = New-ProtocolRecord "evidence.artifact-status-contradiction" $toolchain $commands
            Save-Record $record
            Write-Output "M0.5 protocol failure: successful native result is missing its semantic payload."
            exit 1
        }
        $aotPayloadPaths += $payloadPath
    }
}

$failedRuns = @($aotResults | Where-Object { $_.Process.ExitCode -ne 0 -or -not $_.Inner.Succeeded })
if ($failedRuns.Count -gt 0) {
    $classification = $failedRuns[0].Inner
    if ($failedRuns.Count -ne 2 -or $classification.Code -ne $failedRuns[1].Inner.Code -or $classification.Cause -ne $failedRuns[1].Inner.Cause) {
        $classification = [pscustomobject]@{ Phase = "launch"; Cause = "unknown"; Code = "launch.native-process-failed"; Outcome = "inconclusive" }
    }
    $record = New-CellRecord $classification.Outcome $classification.Phase $classification.Cause $classification.Code (Get-Comparison $frameworkPayloadPath "" $false $false) @([ordered]@{ phase = $classification.Phase; cause = $classification.Cause; code = $classification.Code }) $toolchain $commands
    Save-Record $record
    if ($classification.Outcome -eq "not-feasible") { Write-Output "M0.5 cell conclusive negative: $($classification.Code)."; exit 0 }
    Write-Output "M0.5 cell inconclusive: native semantic path did not reproduce."
    exit 1
}

$firstAotBytes = [IO.File]::ReadAllBytes($aotPayloadPaths[0])
$secondAotBytes = [IO.File]::ReadAllBytes($aotPayloadPaths[1])
$repeatedEqual = [System.Linq.Enumerable]::SequenceEqual($firstAotBytes, $secondAotBytes)
if (-not $repeatedEqual) {
    $record = New-CellRecord "inconclusive" "comparison" "semantic-contract" "comparison.payload-nondeterministic" (Get-Comparison $frameworkPayloadPath $aotPayloadPaths[0] $false $false) @([ordered]@{ phase = "comparison"; cause = "semantic-contract"; code = "comparison.payload-nondeterministic" }) $toolchain $commands
    Save-Record $record
    Write-Output "M0.5 cell inconclusive: native payloads were not byte-identical."
    exit 1
}

$frameworkBytes = [IO.File]::ReadAllBytes($frameworkPayloadPath)
$frameworkEqual = [System.Linq.Enumerable]::SequenceEqual($frameworkBytes, $firstAotBytes)
if (-not $frameworkEqual) {
    $record = New-CellRecord "not-feasible" "comparison" "semantic-contract" "comparison.payload-mismatch" (Get-Comparison $frameworkPayloadPath $aotPayloadPaths[0] $true $false) @([ordered]@{ phase = "comparison"; cause = "semantic-contract"; code = "comparison.payload-mismatch" }) $toolchain $commands
    Save-Record $record
    Write-Output "M0.5 cell conclusive negative: Native AOT payload differs from the frozen framework baseline."
    exit 0
}

$record = New-CellRecord "feasible-clean" "comparison" "semantic-contract" "" (Get-Comparison $frameworkPayloadPath $aotPayloadPaths[0] $true $true) @() $toolchain $commands
Save-Record $record
Write-Output "M0.5 cell conclusive positive: Native AOT payload matched the frozen framework baseline."
exit 0
