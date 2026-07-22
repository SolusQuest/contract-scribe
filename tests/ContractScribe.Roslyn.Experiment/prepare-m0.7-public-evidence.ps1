[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,
    [Parameter(Mandatory = $true)]
    [string]$PublicRoot
)

$ErrorActionPreference = "Stop"
$sourceRootPath = [IO.Path]::GetFullPath($SourceRoot)
$publicRootPath = [IO.Path]::GetFullPath($PublicRoot)
$allowed = @(
    "m0.7-evidence.json",
    "run-1/semantic-payload.json",
    "run-2/semantic-payload.json"
)
if (Test-Path -LiteralPath $publicRootPath) { Remove-Item -LiteralPath $publicRootPath -Recurse -Force }
New-Item -ItemType Directory -Path $publicRootPath | Out-Null
foreach ($relativePath in $allowed) {
    $sourcePath = Join-Path $sourceRootPath ($relativePath -replace "/", [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $sourcePath)) { throw "A required public evidence file is missing." }
    $destinationPath = Join-Path $publicRootPath ($relativePath -replace "/", [IO.Path]::DirectorySeparatorChar)
    New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
}
$actual = @(Get-ChildItem -LiteralPath $publicRootPath -File -Recurse | ForEach-Object { $_.FullName.Substring($publicRootPath.Length + 1).Replace("\", "/") } | Sort-Object)
if ((($actual -join ",") -ne (($allowed | Sort-Object) -join ","))) { throw "The public evidence allowlist was not closed." }
Write-Output "M0.7 public evidence prepared: only bounded evidence and canonical payload files are publishable."
