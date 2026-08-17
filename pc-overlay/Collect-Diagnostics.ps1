$ErrorActionPreference = 'Stop'
$ProjectDirectory = $PSScriptRoot
$Timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Destination = Join-Path $ProjectDirectory (Join-Path 'diagnostics' $Timestamp)
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

$LocalRoot = Join-Path $env:LOCALAPPDATA 'JvdP\LightDarkroomOverlay'
$LiveLog = Join-Path $LocalRoot 'overlay.log'
if (Test-Path -LiteralPath $LiveLog) {
    Copy-Item -LiteralPath $LiveLog -Destination (Join-Path $Destination 'overlay-full.log')
    Get-Content -LiteralPath $LiveLog -Tail 250 |
        Set-Content -LiteralPath (Join-Path $Destination 'overlay-tail.log') -Encoding UTF8
}

Get-Process DarkroomBooth,JvdpLightDarkroomOverlay -ErrorAction SilentlyContinue |
    Select-Object ProcessName,Id,StartTime,Responding,MainWindowTitle,MainWindowHandle |
    Format-List |
    Out-File -LiteralPath (Join-Path $Destination 'process-status.txt') -Encoding UTF8

[IO.Ports.SerialPort]::GetPortNames() |
    Sort-Object |
    Out-File -LiteralPath (Join-Path $Destination 'serial-ports.txt') -Encoding UTF8

$Executables = @(
    (Join-Path $ProjectDirectory 'JvdpLightDarkroomOverlay.exe'),
    (Join-Path $LocalRoot 'JvdpLightDarkroomOverlay.exe')
) | Where-Object { Test-Path -LiteralPath $_ }
$Executables |
    ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 } |
    Format-Table -AutoSize |
    Out-File -LiteralPath (Join-Path $Destination 'executable-hashes.txt') -Encoding UTF8

Write-Output $Destination