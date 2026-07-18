# Builds ButlerV5 and copies the DLL into Skua's plugins folder.
# Skua must be closed (or the plugin unloaded) or the copy will fail on a locked DLL.

$ErrorActionPreference = 'Stop'

dotnet build "$PSScriptRoot\ButlerV5\ButlerV5.csproj" -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll  = Join-Path $PSScriptRoot 'ButlerV5\bin\Release\ButlerV5.dll'
$dest = Join-Path $env:APPDATA 'Skua\plugins'

$running = Get-Process -Name 'Skua*' -ErrorAction SilentlyContinue
if ($running) {
    Write-Warning "Skua appears to be running ($(($running | Select-Object -ExpandProperty ProcessName -Unique) -join ', ')). Close it if the copy fails."
}

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $dll $dest -Force
Write-Host "Deployed ButlerV5.dll -> $dest" -ForegroundColor Green
