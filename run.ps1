# SPDX-License-Identifier: GPL-3.0-only
$ErrorActionPreference='Stop'
$application=Join-Path $PSScriptRoot 'build/single/OledCalibration.exe'
if(-not (Test-Path -LiteralPath $application)) {
    & "$PSScriptRoot/build-single.ps1"
    if($LASTEXITCODE -ne 0){throw 'Application build failed'}
}
Start-Process -FilePath $application -WorkingDirectory $PSScriptRoot -WindowStyle Hidden
