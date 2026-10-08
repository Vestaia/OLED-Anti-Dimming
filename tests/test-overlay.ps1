# SPDX-License-Identifier: GPL-3.0-only
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    if(-not (Test-Path build/hook-probe.exe)){throw 'Run test.ps1 first to build native dependencies'}
    & ./build/hook-probe.exe build/overlay-symbols
    if($LASTEXITCODE -ne 0){throw 'Symbol resolution failed'}
    & ./build/hook-probe.exe build/overlay-symbols --inspect | Set-Content -Encoding ascii build/dwm-symbols.txt
    if($LASTEXITCODE -ne 0){throw 'Symbol inspection failed'}
    $vswhere='C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    $vsRoot=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $batch=@"
@echo off
call "$vsRoot\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 >nul
cd /d "$projectRoot"
ml64 /nologo /c /Fobuild\overlay_hook_test.obj tests\native\overlay_hook_test.asm
if errorlevel 1 exit /b 1
cl /nologo /EHsc /std:c++17 /O2 /Iexternal\minhook\include tests\native\overlay_hook_test.cpp build\overlay_hook.obj build\overlay_hook_test.obj /Fe:build\overlay-hook-test.exe /Fo:build\overlay_hook_test_cpp.obj /link build\minhook.lib
if errorlevel 1 exit /b 1
build\overlay-hook-test.exe
"@
    $batch | Set-Content -Encoding ascii build/overlay-test.cmd
    & cmd.exe /c "$projectRoot\build\overlay-test.cmd"
    if($LASTEXITCODE -ne 0){throw 'Overlay regression failed'}
} finally {Pop-Location}
