param(
    [string]$ServiceName = "KR2PlantFloorCollector",
    [string]$DisplayName = "KR2 Plant Floor Collector",
    [string]$ExePath = "C:\PlantFloorCollector\PlantFloorCollector.exe"
)
sc.exe create $ServiceName binPath= $ExePath start= auto DisplayName= $DisplayName
sc.exe description $ServiceName "KR2 Plant Floor Collector cross-platform machine data collector."
sc.exe start $ServiceName
