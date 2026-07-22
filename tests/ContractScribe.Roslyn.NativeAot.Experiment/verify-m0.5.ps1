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
Write-Output "M0.5 V1 current-tree entrypoint is disabled. Use reproduce-m0.5-v1.ps1 from the main-only historical reproduction workflow."
exit 1
<#
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$fixtureRoot = Join-Path $repositoryRoot "tests\fixtures\roslyn-msbuild\v1"
$manifestPath = Join-Path $fixtureRoot "m0.5-native-aot-manifest.json"
$m04ManifestPath = Join-Path $fixtureRoot "transfer-manifest.json"
$registryPath = Join-Path $repositoryRoot "docs\20_architecture\experiments\m0.5-native-aot-registry-v1.json"
$solutionPath = Join-Path $fixtureRoot "Sample.sln"
$nativeProjectPath = Join-Path $repositoryRoot "tests\ContractScribe.Roslyn.NativeAot.Experiment\ContractScribe.Roslyn.NativeAot.Experiment.csproj"
$m04VerifierPath = Join-Path $repositoryRoot "tests\ContractScribe.Roslyn.Experiment\verify-m0.4.ps1"
$provenanceVerifierPath = Join-Path $repositoryRoot "tests\ContractScribe.Roslyn.NativeAot.Experiment\verify-m0.5-provenance.ps1"
$protocolRoot = Join-Path $repositoryRoot ("TestResults\m0.5-protocol\" + $RuntimeIdentifier)
$defaultEvidencePath = Join-Path $fixtureRoot ("evidence\m0.5-" + $RuntimeIdentifier + "-evidence-v1.json")
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = $defaultEvidencePath
}
$EvidencePath = [IO.Path]::GetFullPath($EvidencePath)
$committedEvidencePath = $EvidencePath
$recordOutputPath = $EvidencePath

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

function Get-Toolchain([string]$runnerOs, [string]$rid, [string]$m04ResultPath) {
    $sdk = (& dotnet --version).Trim()
    if ($sdk -notmatch "^\d+\.\d+\.\d+$") { $sdk = "0.0.0" }
    $m04Toolchain = $null
    if (Test-Path -LiteralPath $m04ResultPath) {
        try { $m04Toolchain = (Get-Content -LiteralPath $m04ResultPath -Raw | ConvertFrom-Json).toolchain } catch { $m04Toolchain = $null }
    }
    $runtime = if ($null -ne $m04Toolchain -and $m04Toolchain.runtimeVersion) { [string]$m04Toolchain.runtimeVersion } else { [Environment]::Version.ToString() }
    if ($runtime -notmatch "^\d+\.\d+\.\d+$") { $runtime = "0.0.0" }
    $msbuild = if ($null -ne $m04Toolchain -and $m04Toolchain.msbuildVersion) { [string]$m04Toolchain.msbuildVersion } else { "unknown" }
    $nativeCompilerId = if ($runnerOs -eq "Windows") { "msvc" } else { "clang" }
    $linkerId = if ($runnerOs -eq "Windows") { "link" } else { "lld" }
    $nativeCompilerVersion = "unknown"
    $linkerVersion = "unknown"
    $nativeCompilerAvailable = $false
    $linkerAvailable = $false
    if ($runnerOs -eq "Windows") {
        $compiler = Get-Command cl.exe -ErrorAction SilentlyContinue
        $linker = Get-Command link.exe -ErrorAction SilentlyContinue
        $vswhere = Get-Command vswhere.exe -ErrorAction SilentlyContinue
        if ($null -eq $vswhere) {
            $vswherePath = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
            if (Test-Path -LiteralPath $vswherePath) { $vswhere = [pscustomobject]@{ Source = $vswherePath } }
        }
        if (($null -eq $compiler -or $null -eq $linker) -and $null -ne $vswhere) {
            $installationPath = (& $vswhere.Source -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null | Select-Object -First 1)
            if (-not [string]::IsNullOrWhiteSpace($installationPath)) {
                $msvcRoot = Join-Path $installationPath "VC\Tools\MSVC"
                $toolDirectory = Get-ChildItem -LiteralPath $msvcRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
                if ($null -ne $toolDirectory) {
                    $nativeBinDirectory = Join-Path $toolDirectory.FullName "bin\Hostx64\x64"
                    $compilerPath = Join-Path $nativeBinDirectory "cl.exe"
                    $linkerPath = Join-Path $nativeBinDirectory "link.exe"
                    if (Test-Path -LiteralPath $compilerPath) { $compiler = [pscustomobject]@{ Source = $compilerPath } }
                    if (Test-Path -LiteralPath $linkerPath) { $linker = [pscustomobject]@{ Source = $linkerPath } }
                    if ($null -ne $compiler -and $null -ne $linker) { $env:PATH = $nativeBinDirectory + ";" + $env:PATH }
                }
            }
        }
        if ($null -ne $compiler) {
            $nativeCompilerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($compiler.Source).FileVersion
            if ([string]::IsNullOrWhiteSpace($nativeCompilerVersion)) { $nativeCompilerVersion = "unknown" }
            $nativeCompilerAvailable = $true
        }
        if ($null -ne $linker) {
            $linkerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($linker.Source).FileVersion
            if ([string]::IsNullOrWhiteSpace($linkerVersion)) { $linkerVersion = "unknown" }
            $linkerAvailable = $true
        }
    }
    else {
        $compiler = Get-Command clang -ErrorAction SilentlyContinue
        $linker = Get-Command ld.lld -ErrorAction SilentlyContinue
        if ($null -eq $linker) { $linker = Get-Command "ld.lld-*" -ErrorAction SilentlyContinue | Sort-Object Source -Descending | Select-Object -First 1 }
        $compilerCommand = if ($null -ne $compiler) { $compiler.Source } else { "clang" }
        $linkerCommand = if ($null -ne $linker) { $linker.Source } else { "ld.lld" }
        $compilerProbe = Invoke-CapturedProcess $compilerCommand @("--version") $repositoryRoot
        $linkerProbe = Invoke-CapturedProcess $linkerCommand @("--version") $repositoryRoot
        $compilerMatch = [Regex]::Match(($compilerProbe.Stdout + $compilerProbe.Stderr), "(?<!\d)(\d+\.\d+(?:\.\d+)?)(?!\d)")
        $linkerMatch = [Regex]::Match(($linkerProbe.Stdout + $linkerProbe.Stderr), "(?<!\d)(\d+\.\d+(?:\.\d+)?)(?!\d)")
        if ($compilerProbe.ExitCode -eq 0 -and $compilerMatch.Success) { $nativeCompilerVersion = $compilerMatch.Groups[1].Value; $nativeCompilerAvailable = $true }
        if ($linkerProbe.ExitCode -eq 0 -and $linkerMatch.Success) { $linkerVersion = $linkerMatch.Groups[1].Value; $linkerAvailable = $true }
    }
    return [pscustomobject]@{
        Identity = [ordered]@{
        sdkVersion = $sdk
        runtimeVersion = $runtime
        msbuildVersion = $msbuild
        nativeCompilerId = $nativeCompilerId
        nativeCompilerVersion = $nativeCompilerVersion
        linkerId = $linkerId
        linkerVersion = $linkerVersion
        runnerOs = $runnerOs
        rid = $rid
        processArchitecture = Get-ProcessArchitecture
        }
        Available = ($nativeCompilerAvailable -and $linkerAvailable)
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
        return [pscustomobject]@{ Protocol = $true; ProtocolCode = "evidence.artifact-missing" }
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

function New-CellRecord([string]$outcome, [string]$phase, [string]$cause, [string]$code, [object]$comparison, [object[]]$observations, [object]$toolchain, [object[]]$commands, [object[]]$warnings = @()) {
    $record = [ordered]@{
        evidenceVersion = "m0.5-native-aot-evidence-v1"
        recordType = "cell"
        cell = [ordered]@{ runnerOs = $runnerOs; rid = $RuntimeIdentifier; processArchitecture = $processArchitecture }
        profile = Get-Profile $RuntimeIdentifier
        commands = $commands
        warnings = @($warnings | Sort-Object phase, cause, code -Unique)
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

function Assert-RecordContract([object]$record) {
    $common = @("evidenceVersion", "recordType", "cell", "profile", "commands", "warnings", "toolchain", "dependencies")
    foreach ($name in $common) { Assert-Condition ($null -ne $record.$name) "The evidence record is missing '$name'." }
    Assert-Condition ($record.evidenceVersion -eq "m0.5-native-aot-evidence-v1") "The evidence version is invalid."
    Assert-Condition ($record.recordType -in @("cell", "protocol-failure")) "The evidence record type is invalid."
    Assert-Condition ($record.cell.runnerOs -in @("Ubuntu", "Windows") -and $record.cell.rid -in @("linux-x64", "win-x64") -and $record.cell.processArchitecture -eq "X64") "The evidence cell identity is invalid."
    Assert-Condition ($record.profile.targetFramework -eq "net10.0" -and $record.profile.configuration -eq "Release" -and $record.profile.publishAot -eq $true -and $record.profile.selfContained -eq $true -and $record.profile.publishTrimmed -eq $true -and $record.profile.runtimeIdentifier -eq $record.cell.rid) "The evidence publish profile is invalid."
    Assert-Condition ($record.toolchain.runnerOs -eq $record.cell.runnerOs -and $record.toolchain.rid -eq $record.cell.rid -and $record.toolchain.processArchitecture -eq "X64") "The evidence toolchain identity is inconsistent."
    Assert-Condition (@($record.warnings).Count -eq @($record.warnings | ForEach-Object { "$($_.phase)|$($_.cause)|$($_.code)" } | Sort-Object -Unique).Count) "The evidence contains duplicate warnings."
    if ($record.recordType -eq "protocol-failure") {
        Assert-Condition ($null -ne $record.protocolFailure -and $record.protocolFailure.phase -eq "evidence" -and @($registry.protocolFailureCodes) -contains [string]$record.protocolFailure.code) "The protocol-failure record is invalid."
        foreach ($name in @("outcome", "phase", "cause", "code", "comparison", "observations")) { Assert-Condition ($null -eq $record.$name) "A protocol-failure record contains cell field '$name'." }
        return
    }
    Assert-Condition ($null -eq $record.protocolFailure) "A cell record contains a protocol-failure field."
    Assert-Condition ($record.outcome -in @("feasible-clean", "feasible-with-warnings", "not-feasible", "inconclusive")) "The cell outcome is invalid."
    Assert-Condition ($record.phase -in @("preflight", "restore", "publish", "native-link", "launch", "semantic-path", "comparison")) "The cell phase is invalid."
    Assert-Condition ($record.cause -in @("native-toolchain", "rid-assets", "sdk-resolution", "aot-analysis", "trimming-reflection", "dynamic-assembly-loading", "msbuild-host", "roslyn", "semantic-contract", "unknown")) "The cell cause is invalid."
    if ([string]::IsNullOrWhiteSpace([string]$record.code)) {
        Assert-Condition ($record.outcome -in @("feasible-clean", "feasible-with-warnings")) "A negative or inconclusive cell is missing its stable code."
    }
    else {
        Assert-Condition ($registry.cellCodes.PSObject.Properties.Name -contains [string]$record.code) "The cell code is not in the closed registry."
        $definition = $registry.cellCodes.($record.code)
        Assert-Condition ($definition.phase -eq $record.phase -and @($definition.allowedCauses) -contains $record.cause -and @($definition.allowedOutcomes) -contains $record.outcome) "The cell classification is not allowed by the registry."
    }
    Assert-Condition (-not ($record.cause -eq "unknown" -and $record.outcome -ne "inconclusive")) "Unknown cause cannot be conclusive."
    if ($record.outcome -eq "feasible-clean") { Assert-Condition (@($record.warnings).Count -eq 0) "Feasible-clean evidence cannot contain warnings." }
    if ($record.outcome -eq "feasible-with-warnings") { Assert-Condition (@($record.warnings).Count -gt 0 -and (@($record.warnings | ForEach-Object { $registry.warningCodes -contains [string]$_.code }) -notcontains $false)) "Feasible-with-warnings requires only reviewed warning codes." }
    Assert-Condition ($null -ne $record.comparison -and $record.comparison.status -in @("not-run", "compared")) "The comparison status is invalid."
    if ($record.comparison.status -eq "compared") {
        Assert-Condition ($record.comparison.frameworkPayloadSha256 -match "^[0-9a-f]{64}$" -and $record.comparison.aotPayloadSha256 -match "^[0-9a-f]{64}$" -and $null -ne $record.comparison.repeatedAotPayloadByteEqual -and $null -ne $record.comparison.frameworkByteEqual) "Compared evidence is missing payload facts."
    }
    else {
        Assert-Condition ($null -eq $record.comparison.aotPayloadSha256 -and $null -eq $record.comparison.repeatedAotPayloadByteEqual -and $null -eq $record.comparison.frameworkByteEqual) "Not-run evidence contains comparison facts."
    }
    if ($record.code -eq "comparison.payload-nondeterministic") { Assert-Condition ($record.outcome -eq "inconclusive" -and $record.comparison.status -eq "compared" -and $record.comparison.repeatedAotPayloadByteEqual -eq $false) "Nondeterministic evidence is contradictory." }
    if ($record.code -eq "comparison.payload-mismatch") { Assert-Condition ($record.outcome -eq "not-feasible" -and $record.comparison.status -eq "compared" -and $record.comparison.repeatedAotPayloadByteEqual -eq $true -and $record.comparison.frameworkByteEqual -eq $false -and $record.cause -eq "semantic-contract") "Payload-mismatch evidence is contradictory." }
    if ($record.outcome -eq "feasible-clean") { Assert-Condition ($record.comparison.status -eq "compared" -and $record.comparison.repeatedAotPayloadByteEqual -eq $true -and $record.comparison.frameworkByteEqual -eq $true) "Feasible-clean evidence is missing successful comparison facts." }
    if ($record.outcome -eq "feasible-with-warnings") { Assert-Condition ($record.comparison.status -eq "compared" -and $record.comparison.repeatedAotPayloadByteEqual -eq $true -and $record.comparison.frameworkByteEqual -eq $true) "Feasible-with-warnings evidence is missing successful comparison facts." }
    if ($record.comparison.status -eq "not-run") { Assert-Condition ($record.outcome -eq "inconclusive" -or $record.phase -ne "comparison") "A conclusive comparison outcome cannot be not-run." }
}

function Save-Record([object]$record) {
    Assert-RecordContract $record
    Write-CanonicalJson $recordOutputPath $record
    $content = [IO.File]::ReadAllText($recordOutputPath)
    Assert-Condition ($content -notmatch "(?i)([A-Z]:\\|/home/|/Users/|authorization|bearer\s|access[_-]?token|api[_-]?key|username|hostname|environment)") "The evidence contains a private path, credential-like value, or environment dump."
    $bytes = [IO.File]::ReadAllBytes($recordOutputPath)
    Assert-Condition ($bytes.Length -gt 0 -and $bytes[0] -ne 0xEF) "The evidence is empty or has a UTF-8 BOM."
    Assert-Condition ($bytes[$bytes.Length - 1] -ne 0x0A -and $bytes[$bytes.Length - 1] -ne 0x0D) "The evidence has a trailing newline."
    if ($EvidenceReproduction) {
        Assert-Condition ([System.Linq.Enumerable]::SequenceEqual($bytes, [IO.File]::ReadAllBytes($committedEvidencePath))) "The regenerated evidence differs from the committed evidence."
        Remove-Item -LiteralPath $recordOutputPath -Force
    }
}

function Get-Comparison([string]$frameworkPayloadPath, [string]$aotPayloadPath, [bool]$repeatedEqual, [bool]$frameworkEqual) {
    $comparison = [ordered]@{ status = "not-run" }
    if (Test-Path -LiteralPath $frameworkPayloadPath) {
        $comparison.frameworkPayloadSha256 = Get-Sha256 $frameworkPayloadPath
    }
    if (Test-Path -LiteralPath $aotPayloadPath) {
        $comparison.aotPayloadSha256 = Get-Sha256 $aotPayloadPath
    }
    if ((Test-Path -LiteralPath $frameworkPayloadPath) -and (Test-Path -LiteralPath $aotPayloadPath)) {
        $comparison.status = "compared"
        $comparison.repeatedAotPayloadByteEqual = $repeatedEqual
        $comparison.frameworkByteEqual = $frameworkEqual
    }
    return $comparison
}

function Get-WarningObservations([string]$text, [string]$phase) {
    $observations = @()
    foreach ($line in ($text -split "`r?`n")) {
        $match = [Regex]::Match($line, "(?i)\bwarning\s+([A-Z][A-Z0-9_-]*\d+[A-Z0-9_-]*)\b")
        if ($match.Success) {
            $warningCode = "warning." + $match.Groups[1].Value.ToLowerInvariant()
            $cause = if ($line -match "(?i)trim|il20") { "trimming-reflection" } else { "aot-analysis" }
            $observations += [ordered]@{ phase = $phase; cause = $cause; code = $warningCode }
        }
    }
    return @($observations | Sort-Object phase, cause, code -Unique)
}

function Test-SameClassification([object]$first, [object]$second) {
    return ($first.Phase -eq $second.Phase -and $first.Cause -eq $second.Cause -and $first.Code -eq $second.Code -and $first.Outcome -eq $second.Outcome)
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
$registry = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json
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

if ($EvidenceReproduction) {
    Assert-Condition (Test-Path -LiteralPath $committedEvidencePath) "The committed cell evidence is missing for reproduction."
    $currentRevision = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    Assert-Condition ($manifest.implementationRevision -match "^[0-9a-f]{40}$") "The implementation revision is not a full commit hash."
    & git -C $repositoryRoot cat-file -e "$($manifest.implementationRevision)^{commit}"
    $implementationCommitExists = ($LASTEXITCODE -eq 0)
    & git -C $repositoryRoot merge-base --is-ancestor $manifest.implementationRevision $currentRevision
    $implementationRevisionIsAncestor = ($LASTEXITCODE -eq 0)
    if ($implementationCommitExists -and $implementationRevisionIsAncestor) {
        $changedFiles = @(& git -C $repositoryRoot diff --name-only "$($manifest.implementationRevision)..$currentRevision")
        $allowedFiles = @($manifest.allowedPostImplementationFiles)
        Assert-Condition (@($changedFiles | Where-Object { $allowedFiles -notcontains $_ }).Count -eq 0) "The reproduction head contains changes outside the post-implementation evidence allowlist."
    }
    else {
        Assert-Condition (Test-Path -LiteralPath $provenanceVerifierPath) "The M0.5 provenance verifier is missing."
        $allowedHistoryFiles = @(
            ".github/workflows/ci.yml",
            "tests/ContractScribe.Roslyn.Experiment/verify-m0.4.ps1",
            "tests/fixtures/roslyn-msbuild/v1/transfer-manifest.json"
        ) + @($manifest.allowedPostSourceFiles) + @($manifest.allowedPostImplementationFiles)
        $provenanceArguments = @(
            "-NoProfile",
            "-File",
            $provenanceVerifierPath,
            "-RepositoryRoot",
            $repositoryRoot,
            "-FrozenSourceRevision",
            [string]$manifest.m04FrozenSourceRevision,
            "-CurrentRevision",
            $currentRevision,
            "-AllowedFiles"
        ) + ($allowedHistoryFiles -join "|")
        $provenance = Invoke-CapturedProcess "pwsh" $provenanceArguments $repositoryRoot
        Assert-Condition ($provenance.ExitCode -eq 0) "The squashed reproduction tree failed the closed provenance check: $($provenance.Stdout) $($provenance.Stderr)"
    }
    $recordOutputPath = Join-Path $protocolRoot "reproduction-evidence.json"
}

Remove-CellOutputs
$toolchainProbe = Get-Toolchain $runnerOs $RuntimeIdentifier ""
$toolchain = $toolchainProbe.Identity
$commands = Get-CommandList
$frameworkPayloadPath = Join-Path $repositoryRoot "TestResults\m0.4-protocol\run-1\semantic-payload.json"
$capturedWarnings = @()

$restoreArgs = @("restore", "ContractScribe.slnx")
$restoreFirst = Invoke-CapturedProcess "dotnet" $restoreArgs $repositoryRoot
if ($restoreFirst.ExitCode -ne 0) {
    $firstRestoreClassification = Get-ErrorClassification ($restoreFirst.Stdout + $restoreFirst.Stderr)
    Remove-CellOutputs
    $restoreSecond = Invoke-CapturedProcess "dotnet" $restoreArgs $repositoryRoot
    if ($restoreSecond.ExitCode -ne 0) {
        $secondRestoreClassification = Get-ErrorClassification ($restoreSecond.Stdout + $restoreSecond.Stderr)
        $classification = if (Test-SameClassification $firstRestoreClassification $secondRestoreClassification) { $firstRestoreClassification } else { [pscustomobject]@{ Phase = "restore"; Cause = "unknown"; Code = "restore.sdk-resolution-failed"; Outcome = "inconclusive" } }
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

$m04ResultPath = Join-Path $repositoryRoot "TestResults\m0.4-protocol\run-1\result.json"
$toolchainProbe = Get-Toolchain $runnerOs $RuntimeIdentifier $m04ResultPath
$toolchain = $toolchainProbe.Identity
if (-not $toolchainProbe.Available) {
    $record = New-CellRecord "inconclusive" "preflight" "native-toolchain" "preflight.native-toolchain-unavailable" (Get-Comparison $frameworkPayloadPath "" $false $false) @([ordered]@{ phase = "preflight"; cause = "native-toolchain"; code = "preflight.native-toolchain-unavailable" }) $toolchain $commands
    Save-Record $record
    Write-Output "M0.5 cell inconclusive: required native compiler/linker identity is unavailable."
    exit 1
}

$nativeRestoreArgs = @("restore", $nativeProjectPath, "--runtime", $RuntimeIdentifier)
$nativeRestoreFirst = Invoke-CapturedProcess "dotnet" $nativeRestoreArgs $repositoryRoot
if ($nativeRestoreFirst.ExitCode -ne 0) {
    $firstNativeRestoreClassification = Get-ErrorClassification ($nativeRestoreFirst.Stdout + $nativeRestoreFirst.Stderr)
    Remove-Item -LiteralPath (Join-Path $repositoryRoot "tests\ContractScribe.Roslyn.NativeAot.Experiment\obj") -Recurse -Force -ErrorAction SilentlyContinue
    $nativeRestoreSecond = Invoke-CapturedProcess "dotnet" $nativeRestoreArgs $repositoryRoot
    if ($nativeRestoreSecond.ExitCode -ne 0) {
        $secondNativeRestoreClassification = Get-ErrorClassification ($nativeRestoreSecond.Stdout + $nativeRestoreSecond.Stderr)
        $classification = if (Test-SameClassification $firstNativeRestoreClassification $secondNativeRestoreClassification) { $firstNativeRestoreClassification } else { [pscustomobject]@{ Phase = "restore"; Cause = "unknown"; Code = "restore.rid-assets-unavailable"; Outcome = "inconclusive" } }
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
    $capturedWarnings += @(Get-WarningObservations ($publishResults[-1].Stdout + $publishResults[-1].Stderr) "publish")
    if ($publishResults[-1].ExitCode -eq 0) { break }
    if ($attempt -eq 1) {
        $nativeRidIntermediate = Join-Path $repositoryRoot ("tests\ContractScribe.Roslyn.NativeAot.Experiment\obj\Release\net10.0\" + $RuntimeIdentifier)
        $nativeRidBin = Join-Path $repositoryRoot ("tests\ContractScribe.Roslyn.NativeAot.Experiment\bin\Release\net10.0\" + $RuntimeIdentifier)
        foreach ($intermediatePath in @($nativeRidIntermediate, $nativeRidBin)) {
            if (Test-Path -LiteralPath $intermediatePath) { Remove-Item -LiteralPath $intermediatePath -Recurse -Force }
        }
    }
}

if ($publishResults.Count -eq 0 -or $publishResults[-1].ExitCode -ne 0) {
    $classifications = @($publishResults | ForEach-Object { Get-ErrorClassification ($_.Stdout + $_.Stderr) })
    $classification = $classifications[0]
    if ($classifications.Count -ne 2 -or -not (Test-SameClassification $classifications[0] $classifications[1])) {
        $classification = [pscustomobject]@{ Phase = "native-link"; Cause = "unknown"; Code = "native-link.linker-failed"; Outcome = "inconclusive" }
    }
    if (@($capturedWarnings | Where-Object { $registry.warningCodes -notcontains [string]$_.code }).Count -gt 0) {
        $classification = [pscustomobject]@{ Phase = "publish"; Cause = "unknown"; Code = "publish.unreviewed-warning"; Outcome = "inconclusive" }
    }
    $record = New-CellRecord $classification.Outcome $classification.Phase $classification.Cause $classification.Code (Get-Comparison $frameworkPayloadPath "" $false $false) @([ordered]@{ phase = $classification.Phase; cause = $classification.Cause; code = $classification.Code }) $toolchain $commands $capturedWarnings
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
    $capturedWarnings += @(Get-WarningObservations ($nativeRun.Stdout + $nativeRun.Stderr) "launch")
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
    if ($failedRuns.Count -ne 2 -or -not (Test-SameClassification $classification $failedRuns[1].Inner)) {
        $classification = [pscustomobject]@{ Phase = "launch"; Cause = "unknown"; Code = "launch.native-process-failed"; Outcome = "inconclusive" }
    }
    if (@($capturedWarnings | Where-Object { $registry.warningCodes -notcontains [string]$_.code }).Count -gt 0) {
        $classification = [pscustomobject]@{ Phase = "launch"; Cause = "unknown"; Code = "launch.native-process-failed"; Outcome = "inconclusive" }
    }
    $record = New-CellRecord $classification.Outcome $classification.Phase $classification.Cause $classification.Code (Get-Comparison $frameworkPayloadPath "" $false $false) @([ordered]@{ phase = $classification.Phase; cause = $classification.Cause; code = $classification.Code }) $toolchain $commands $capturedWarnings
    Save-Record $record
    if ($classification.Outcome -eq "not-feasible") { Write-Output "M0.5 cell conclusive negative: $($classification.Code)."; exit 0 }
    Write-Output "M0.5 cell inconclusive: native semantic path did not reproduce."
    exit 1
}

$firstAotBytes = [IO.File]::ReadAllBytes($aotPayloadPaths[0])
$secondAotBytes = [IO.File]::ReadAllBytes($aotPayloadPaths[1])
$repeatedEqual = [System.Linq.Enumerable]::SequenceEqual($firstAotBytes, $secondAotBytes)
if (-not $repeatedEqual) {
    $record = New-CellRecord "inconclusive" "comparison" "semantic-contract" "comparison.payload-nondeterministic" (Get-Comparison $frameworkPayloadPath $aotPayloadPaths[0] $false $false) @([ordered]@{ phase = "comparison"; cause = "semantic-contract"; code = "comparison.payload-nondeterministic" }) $toolchain $commands $capturedWarnings
    Save-Record $record
    Write-Output "M0.5 cell inconclusive: native payloads were not byte-identical."
    exit 1
}

$frameworkBytes = [IO.File]::ReadAllBytes($frameworkPayloadPath)
$frameworkEqual = [System.Linq.Enumerable]::SequenceEqual($frameworkBytes, $firstAotBytes)
if (-not $frameworkEqual) {
    $record = New-CellRecord "not-feasible" "comparison" "semantic-contract" "comparison.payload-mismatch" (Get-Comparison $frameworkPayloadPath $aotPayloadPaths[0] $true $false) @([ordered]@{ phase = "comparison"; cause = "semantic-contract"; code = "comparison.payload-mismatch" }) $toolchain $commands $capturedWarnings
    Save-Record $record
    Write-Output "M0.5 cell conclusive negative: Native AOT payload differs from the frozen framework baseline."
    exit 0
}

$unreviewedWarnings = @($capturedWarnings | Where-Object { $registry.warningCodes -notcontains [string]$_.code })
if ($unreviewedWarnings.Count -gt 0) {
    $record = New-CellRecord "inconclusive" "publish" "unknown" "publish.unreviewed-warning" (Get-Comparison $frameworkPayloadPath $aotPayloadPaths[0] $true $true) @([ordered]@{ phase = "publish"; cause = "unknown"; code = "publish.unreviewed-warning" }) $toolchain $commands $capturedWarnings
    Save-Record $record
    Write-Output "M0.5 cell inconclusive: unreviewed Native AOT warnings were observed."
    exit 1
}
$outcome = if (@($capturedWarnings).Count -gt 0) { "feasible-with-warnings" } else { "feasible-clean" }
$record = New-CellRecord $outcome "comparison" "semantic-contract" "" (Get-Comparison $frameworkPayloadPath $aotPayloadPaths[0] $true $true) @() $toolchain $commands $capturedWarnings
Save-Record $record
Write-Output "M0.5 cell conclusive positive: Native AOT payload matched the frozen framework baseline."
exit 0
#>
