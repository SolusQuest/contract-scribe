[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$packagePath = Join-Path $repositoryRoot "Directory.Packages.props"
$verifierPath = Join-Path $repositoryRoot "tests\ContractScribe.Roslyn.Experiment\verify-m0.4.ps1"
$originalBytes = [IO.File]::ReadAllBytes($packagePath)

try {
    $originalText = [Text.Encoding]::UTF8.GetString($originalBytes)
    $tamperedText = $originalText.Replace(
        '<PackageVersion Include="System.Security.Cryptography.Xml" Version="9.0.18" />',
        '<PackageVersion Include="System.Security.Cryptography.Xml" Version="9.0.17" />'
    )
    if ($tamperedText -eq $originalText) {
        throw "The package declaration tamper was not applied."
    }

    [IO.File]::WriteAllText($packagePath, $tamperedText, [Text.UTF8Encoding]::new($false))
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "pwsh"
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add("-NoProfile")
    $startInfo.ArgumentList.Add("-File")
    $startInfo.ArgumentList.Add($verifierPath)
    $startInfo.ArgumentList.Add("-Configuration")
    $startInfo.ArgumentList.Add($Configuration)

    $process = [Diagnostics.Process]::Start($startInfo)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -eq 0) {
        throw "The M0.4 verifier accepted a manifest-only package baseline tamper."
    }

    Write-Output "M0.4 provenance regression passed: a package-only baseline tamper was rejected."
}
finally {
    [IO.File]::WriteAllBytes($packagePath, $originalBytes)
}
