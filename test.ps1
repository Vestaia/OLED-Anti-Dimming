# SPDX-License-Identifier: GPL-3.0-only
$ErrorActionPreference='Stop'
$projectRoot=$PSScriptRoot
& "$projectRoot/build.ps1"
if($LASTEXITCODE -ne 0){throw 'Camera build failed'}
& "$projectRoot/build-desktop.ps1"
if($LASTEXITCODE -ne 0){throw 'Desktop build failed'}
dotnet run --project "$projectRoot/tests/managed" -c Release -- --smooth-hsv-checks
if($LASTEXITCODE -ne 0){throw 'Smoothed HSV checks failed'}
dotnet run --project "$projectRoot/tests/managed" -c Release
if($LASTEXITCODE -ne 0){throw 'Managed regressions failed'}
$vswhere='C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$batch=@"
@echo off
call "$vsRoot\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 >nul
cd /d "$projectRoot\build"
cl /nologo /EHsc /std:c++17 /O2 /DOLED_TESTING /DUNICODE /D_UNICODE "$projectRoot\native\probe.cpp" /Fe:camera-tests.exe /link d3d11.lib dxgi.lib d3dcompiler.lib strmiids.lib ole32.lib oleaut32.lib user32.lib gdi32.lib
if errorlevel 1 exit /b 1
camera-tests.exe --self-test
if errorlevel 1 exit /b 1
cl /nologo /EHsc /std:c++17 /O2 /I"$projectRoot\native" "$projectRoot\tests\native\filter_test.cpp" /Fe:filter-test.exe /link d3d11.lib d3dcompiler.lib dxgi.lib
if errorlevel 1 exit /b 1
cl /nologo /EHsc /std:c++17 /O2 /I"$projectRoot\native" "$projectRoot\tests\native\smooth_hsv_test.cpp" /Fe:smooth-hsv-test.exe /link d3d11.lib d3dcompiler.lib dxgi.lib
if errorlevel 1 exit /b 1
smooth-hsv-test.exe
if errorlevel 1 exit /b 1
filter-test.exe histogram-test-config
if errorlevel 1 exit /b 1
filter-test.exe histogram-test-legacy
if errorlevel 1 exit /b 1
filter-test.exe histogram-test-768
if errorlevel 1 exit /b 1
filter-test.exe histogram-test-5120
if errorlevel 1 exit /b 1
cl /nologo /EHsc /std:c++17 /O2 /I"$projectRoot\native" "$projectRoot\tests\native\cluster_test.cpp" /Fe:cluster-test.exe /link d3d11.lib d3dcompiler.lib dxgi.lib
if errorlevel 1 exit /b 1
cluster-test.exe
if errorlevel 1 exit /b 1
cluster-test.exe scaled-cluster-test-config
if errorlevel 1 exit /b 1
cluster-test.exe gaussian-cluster-test-config
if errorlevel 1 exit /b 1
cluster-test.exe cube-transport-test-config
if errorlevel 1 exit /b 1
cl /nologo /LD /EHsc /std:c++17 /O2 /DUNLOAD_PAYLOAD "$projectRoot\tests\native\unload_test.cpp" /Fe:unload-payload.dll
if errorlevel 1 exit /b 1
cl /nologo /EHsc /std:c++17 /O2 /I"$projectRoot\external\minhook\include" "$projectRoot\tests\native\unload_test.cpp" /Fe:unload-test.exe /link minhook.lib
if errorlevel 1 exit /b 1
unload-test.exe
"@
$batch | Set-Content -LiteralPath "$projectRoot/build/test-compile.cmd" -Encoding ascii
& cmd.exe /c "$projectRoot/build/test-compile.cmd"
if($LASTEXITCODE -ne 0){throw 'Native regressions failed'}
