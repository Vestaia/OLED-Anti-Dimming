# SPDX-License-Identifier: GPL-3.0-only
param([string]$OutputDirectory = 'build')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsRoot) { throw 'Visual Studio C++ tools not found' }
New-Item -ItemType Directory -Force -Path "$projectRoot\$OutputDirectory" | Out-Null
$probeOutput = (Resolve-Path -LiteralPath "$projectRoot\$OutputDirectory").Path
$batch = @"
@echo off
call "$vsRoot\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 >nul
if errorlevel 1 exit /b 1
cd /d "$probeOutput"
cl /nologo /EHsc /std:c++17 /W3 /O2 /DUNICODE /D_UNICODE "$projectRoot\native\probe.cpp" /Fe:hdr-probe.exe /link d3d11.lib dxgi.lib d3dcompiler.lib strmiids.lib ole32.lib oleaut32.lib user32.lib gdi32.lib
"@
$batch | Set-Content -LiteralPath "$probeOutput\compile.cmd" -Encoding ascii
& cmd.exe /c "$probeOutput\compile.cmd"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
