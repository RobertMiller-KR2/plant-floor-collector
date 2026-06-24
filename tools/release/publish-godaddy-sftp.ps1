param(
    [Parameter(Mandatory=$true)] [string] $HostName,
    [Parameter(Mandatory=$true)] [string] $UserName,
    [string] $RemoteRoot = "/public_html/plant-floor-collector",
    [string] $Version = "1.4.6",
    [string] $OutputDir = "releases"
)

$ErrorActionPreference = "Stop"
$ZipName = "plant_floor_collector_v$($Version.Replace('.', '_'))_docker.zip"
$ZipPath = Join-Path $OutputDir $ZipName
$ManifestPath = Join-Path $OutputDir "version.json"

if (-not (Test-Path $ZipPath)) { throw "Release ZIP not found: $ZipPath" }
if (-not (Test-Path $ManifestPath)) { throw "Manifest not found: $ManifestPath" }

Write-Host "This script uses OpenSSH scp. Make sure your GoDaddy hosting account has SSH/SFTP enabled." -ForegroundColor Yellow
ssh "$UserName@$HostName" "mkdir -p '$RemoteRoot/releases'"
scp $ManifestPath "$UserName@$HostName`:$RemoteRoot/version.json"
scp $ZipPath "$UserName@$HostName`:$RemoteRoot/releases/$ZipName"
Write-Host "Uploaded manifest and release ZIP to GoDaddy."
