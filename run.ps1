$ErrorActionPreference = 'Stop'
$probeExe = Join-Path $PSScriptRoot 'build\hdr-probe.exe'
if (-not (Test-Path -LiteralPath $probeExe)) { & "$PSScriptRoot\build.ps1" }
Start-Process -FilePath $probeExe -WorkingDirectory "$PSScriptRoot\build" -WindowStyle Hidden
