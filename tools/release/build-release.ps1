param(
    [Parameter(Mandatory=$true)] [string] $Version,
    [string] $Registry = "ghcr.io/robertmiller-kr2",
    [string] $ImageName = "plant-floor-collector",
    [string] $ManifestBaseUrl = "https://yourdomain.com/plant-floor-collector",
    [string] $ReleaseBaseUrl = "https://yourdomain.com/plant-floor-collector/releases",
    [switch] $Push,
    [switch] $NoDockerBuild,
    [string] $OutputDir = "releases"
)

$ErrorActionPreference = "Stop"

function Write-Step($Text) {
    Write-Host "`n==> $Text" -ForegroundColor Cyan
}

function Require-Command($Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found in PATH."
    }
}

$Root = Resolve-Path (Join-Path $PSScriptRoot "../..")
Set-Location $Root

$ImageRef = "$Registry/$ImageName`:$Version".ToLowerInvariant()
$LatestRef = "$Registry/$ImageName`:latest".ToLowerInvariant()
$ZipName = "plant_floor_collector_v$($Version.Replace('.', '_'))_docker.zip"
$ZipPath = Join-Path $OutputDir $ZipName
$ManifestPath = Join-Path $OutputDir "version.json"

Write-Step "Validating tools"
Require-Command dotnet
Require-Command docker
if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }

Write-Step "Updating version metadata"
$csproj = "src/PlantFloorCollector/PlantFloorCollector.csproj"
[xml]$proj = Get-Content $csproj
$pg = $proj.Project.PropertyGroup[0]
foreach ($nodeName in @("Version", "AssemblyVersion", "FileVersion")) {
    $existing = $pg.SelectSingleNode($nodeName)
    if ($null -eq $existing) {
        $existing = $proj.CreateElement($nodeName)
        [void]$pg.AppendChild($existing)
    }
    if ($nodeName -eq "AssemblyVersion" -or $nodeName -eq "FileVersion") {
        $existing.InnerText = "$Version.0"
    } else {
        $existing.InnerText = $Version
    }
}
$proj.Save((Join-Path $Root $csproj))

Write-Step "Building .NET project"
dotnet restore PlantFloorCollector.sln
dotnet build PlantFloorCollector.sln -c Release --no-restore

if (-not $NoDockerBuild) {
    Write-Step "Building Docker image $ImageRef"
    docker build -t $ImageRef -t $LatestRef .
    if ($Push) {
        Write-Step "Pushing Docker image"
        docker push $ImageRef
        docker push $LatestRef
    } else {
        Write-Host "Skipping docker push. Add -Push after logging in to your registry."
    }
}

Write-Step "Generating update manifest"
$manifest = [ordered]@{
    product = "Plant Floor Collector"
    version = $Version
    image = $ImageRef
    latestImage = $LatestRef
    releaseDate = (Get-Date -Format "yyyy-MM-dd")
    downloadUrl = "$ReleaseBaseUrl/$ZipName"
    notes = @(
        "Automated release package",
        "Docker image: $ImageRef",
        "Persistent Docker volumes are preserved during update"
    )
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $ManifestPath
Copy-Item $ManifestPath "version.json" -Force

Write-Step "Creating release ZIP"
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
$exclude = @(".git", "bin", "obj", $OutputDir.TrimEnd('/','\\'))
$items = Get-ChildItem -Force | Where-Object { $exclude -notcontains $_.Name }
Compress-Archive -Path $items.FullName -DestinationPath $ZipPath -Force

Write-Step "Release complete"
Write-Host "Image:    $ImageRef"
Write-Host "ZIP:      $ZipPath"
Write-Host "Manifest: $ManifestPath"
Write-Host "Upload $ManifestPath to: $ManifestBaseUrl/version.json"
Write-Host "Upload $ZipPath to: $ReleaseBaseUrl/$ZipName"
