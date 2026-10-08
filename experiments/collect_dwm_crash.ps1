$ErrorActionPreference='Stop'
$destination=Join-Path $PSScriptRoot '../build/crash-evidence'
New-Item -ItemType Directory -Force $destination | Out-Null
try {
    $archives=Get-ChildItem -LiteralPath 'C:\ProgramData\Microsoft\Windows\WER\ReportArchive' -Directory |
        Where-Object {$_.Name -like '*dwm*'} | Sort-Object LastWriteTime -Descending | Select-Object -First 8
    foreach($archive in $archives) {
        $target=Join-Path $destination $archive.Name
        New-Item -ItemType Directory -Force $target | Out-Null
        Get-ChildItem -LiteralPath $archive.FullName -File -Force |
            ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $target}
    }
    'Collected existing WER reports.' | Set-Content (Join-Path $destination 'collection-status.txt')
} catch {
    $_ | Out-String | Set-Content (Join-Path $destination 'collection-status.txt')
}
