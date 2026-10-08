# SPDX-License-Identifier: GPL-3.0-only
param([string]$DesktopOutput = 'build/beta')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsRoot) { throw 'Visual Studio C++ tools not found' }
New-Item -ItemType Directory -Force -Path "$projectRoot\build" | Out-Null
$debugTools = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64'
foreach ($taskDll in @('dbghelp.dll','symsrv.dll')) {
  if (Test-Path -LiteralPath "$debugTools\$taskDll") { Copy-Item -LiteralPath "$debugTools\$taskDll" -Destination "$projectRoot\build\$taskDll" -Force }
}
python "$projectRoot\tools\generate_hook.py"
if ($LASTEXITCODE -ne 0) { throw 'Hook generation failed' }
$batch = @"
@echo off
call "$vsRoot\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 >nul
cd /d "$projectRoot\build"
cl /nologo /c /O2 /I"$projectRoot\external\minhook\include" "$projectRoot\external\minhook\src\*.c" "$projectRoot\external\minhook\src\hde\hde64.c"
if errorlevel 1 exit /b 1
lib /nologo /out:minhook.lib buffer.obj hook.obj trampoline.obj hde64.obj
ml64 /nologo /c /Fooverlay_hook.obj "$projectRoot\native\overlay_hook.asm"
if errorlevel 1 exit /b 1
cl /nologo /LD /EHsc /std:c++17 /O2 /DUNICODE /D_UNICODE "$projectRoot\native\hook_bridge.cpp" overlay_hook.obj /Fe:oled-hook-bridge.dll /link /EXPORT:OledOverlaysEnabledThunk
if errorlevel 1 exit /b 1
cl /nologo /LD /EHsc /std:c++17 /O2 /DUNICODE /D_UNICODE /D_CRT_SECURE_NO_WARNINGS /I"$projectRoot\native" /I"$projectRoot\external\dwm_lut\lutdwm" /I"$projectRoot\external\minhook\include" hook.cpp overlay_hook.obj /Fe:oled-apl-hook.dll /link minhook.lib d3d11.lib d3dcompiler.lib dxgi.lib psapi.lib advapi32.lib user32.lib
if errorlevel 1 exit /b 1
cl /nologo /EHsc /std:c++17 /O2 "$projectRoot\native\hook_probe.cpp" /Fe:hook-probe.exe /link dbghelp.lib
if errorlevel 1 exit /b 1
cl /nologo /EHsc /std:c++17 /O2 /DUNICODE /D_UNICODE "$projectRoot\native\device_info.cpp" /Fe:device-info.exe /link strmiids.lib ole32.lib oleaut32.lib user32.lib dxgi.lib
if errorlevel 1 exit /b 1
"@
$batch | Set-Content -LiteralPath "$projectRoot\build\desktop-compile.cmd" -Encoding ascii
& cmd.exe /c "$projectRoot\build\desktop-compile.cmd"
if ($LASTEXITCODE -ne 0) { throw 'Desktop native build failed' }
dotnet build "$projectRoot\desktop\OledCalibration.csproj" -c Release -o "$projectRoot\$DesktopOutput"
if ($LASTEXITCODE -ne 0) { throw '.NET GUI build failed' }
