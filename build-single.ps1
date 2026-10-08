# SPDX-License-Identifier: GPL-3.0-only
param([string]$OutputDirectory='build/single')
$ErrorActionPreference='Stop'
$projectRoot=$PSScriptRoot
# Build into separate directories so a running calibration is not replaced.
& "$projectRoot/build.ps1" -OutputDirectory build/package-native
if($LASTEXITCODE -ne 0){throw 'Camera build failed'}
& "$projectRoot/build-desktop.ps1" -DesktopOutput build/package-gui
if($LASTEXITCODE -ne 0){throw 'Desktop build failed'}
$payloadRoot=Join-Path $projectRoot 'build/package-payload'
New-Item -ItemType Directory -Force "$payloadRoot/build","$payloadRoot/assets/benchmarks","$payloadRoot/licenses" | Out-Null
foreach($name in @('device-info.exe','hook-probe.exe','oled-apl-hook.dll','dbghelp.dll','symsrv.dll')){
 Copy-Item -LiteralPath "$projectRoot/build/$name" -Destination "$payloadRoot/build/$name" -Force
}
Copy-Item -LiteralPath "$projectRoot/build/package-native/hdr-probe.exe" -Destination "$payloadRoot/build/hdr-probe.exe" -Force
Copy-Item -Path "$projectRoot/assets/benchmarks/*" -Destination "$payloadRoot/assets/benchmarks" -Force
Copy-Item -LiteralPath "$projectRoot/THIRD_PARTY_NOTICES.md" -Destination $payloadRoot -Force
Copy-Item -LiteralPath "$projectRoot/LICENSE" -Destination "$payloadRoot/licenses/GPL-3.0.txt" -Force
Copy-Item -LiteralPath "$projectRoot/external/minhook/LICENSE.txt" -Destination "$payloadRoot/licenses/MinHook.txt" -Force
Copy-Item -LiteralPath "$projectRoot/external/dwm_lut/LICENSE-THIRD-PARTY" -Destination "$payloadRoot/licenses/dwm-lut-third-party.txt" -Force
Compress-Archive -Path "$payloadRoot/*" -DestinationPath "$projectRoot/build/standalone-payload.zip" -Force
dotnet publish "$projectRoot/desktop/OledCalibration.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -p:StandalonePackage=true -o "$projectRoot/$OutputDirectory"
if($LASTEXITCODE -ne 0){throw 'Single executable publish failed'}
$files=Get-ChildItem -LiteralPath "$projectRoot/$OutputDirectory" -File
if($files.Count -ne 1 -or $files[0].Name -ne 'OledCalibration.exe'){throw 'Publish output must contain exactly one executable'}
