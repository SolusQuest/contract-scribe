[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,
    [Parameter(Mandatory = $true)]
    [string]$PublicRoot
)

$ErrorActionPreference = "Stop"
$allowed = @(
    "m0.7-evidence.json",
    "run-1/semantic-payload.json",
    "run-2/semantic-payload.json"
)
if (Test-Path -LiteralPath $PublicRoot) { Remove-Item -LiteralPath $PublicRoot -Recurse -Force }
New-Item -ItemType Directory -Path $PublicRoot | Out-Null
foreach ($relativePath in $allowed) {
    $sourcePath = Join-Path $SourceRoot ($relativePath -replace "/", [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $sourcePath)) { throw "A required public evidence file is missing." }
    $destinationPath = Join-Path $PublicRoot ($relativePath -replace "/", [IO.Path]::DirectorySeparatorChar)
    New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
}
$actual = @(Get-ChildItem -LiteralPath $PublicRoot -File -Recurse | ForEach-Object { $_.FullName.Substring($PublicRoot.Length + 1).Replace("\", "/") } | Sort-Object)
if ((($actual -join ",") -ne (($allowed | Sort-Object) -join ","))) { throw "The public evidence allowlist was not closed." }
Write-Output "M0.7 public evidence prepared: only bounded evidence and canonical payload files are publishable."
