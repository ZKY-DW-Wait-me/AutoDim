# One-click deploy: copy built DLLs (AutoDim + AutoDim.Core) into
# %APPDATA%\Autodesk\ApplicationPlugins\AutoDim.bundle so AutoCAD/accoreconsole
# auto-load the latest plugin on startup. Close AutoCAD first.
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$src = Join-Path $repo "src\AutoDim\bin\x64\Debug\net8.0-windows"
$bundle = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\AutoDim.bundle\Contents"

New-Item -ItemType Directory -Force -Path $bundle | Out-Null
Copy-Item (Join-Path $src "AutoDim.dll") $bundle -Force
Copy-Item (Join-Path $src "AutoDim.Core.dll") $bundle -Force

Write-Output "Deployed to $bundle"
Get-ChildItem $bundle -File | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
