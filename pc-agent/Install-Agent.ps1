$ErrorActionPreference = 'Stop'

$destination = Join-Path $env:LOCALAPPDATA 'JvdP\DarkroomIsoAgent'
$startupFolder = [Environment]::GetFolderPath('Startup')
$startupFile = Join-Path $startupFolder 'JvdP-Darkroom-Iso-Agent.cmd'

New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DarkroomIsoAgent.ps1') `
    -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'config.json') `
    -Destination $destination -Force

$startupCommand = @"
@echo off
start "" powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\JvdP\DarkroomIsoAgent\DarkroomIsoAgent.ps1"
"@
[IO.File]::WriteAllText($startupFile, $startupCommand)

$installedScript = Join-Path $destination 'DarkroomIsoAgent.ps1'
Start-Process -FilePath 'powershell.exe' `
    -ArgumentList @(
        '-NoProfile',
        '-WindowStyle', 'Hidden',
        '-ExecutionPolicy', 'Bypass',
        '-File', "`"$installedScript`""
    ) `
    -WindowStyle Hidden

Write-Host 'Darkroom ISO agent installed and started.'
Write-Host "Location: $destination"
Write-Host 'It will start automatically when this Windows user signs in.'
pause
