[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$SkipBuild,
    [switch]$Headless
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$version = (Get-Content -Raw -LiteralPath (
    Join-Path $projectRoot 'VERSION')).Trim()
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $projectRoot (
        'artifacts\layout-matrix-' + $version)
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

if (-not $SkipBuild) {
    & (Join-Path $projectRoot 'build.ps1') -SkipFirmware
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
}

$csc = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw 'The .NET Framework C# compiler was not found.' }

$runner = Join-Path $OutputDirectory 'LayoutMatrixRunner.exe'
& $csc /nologo /target:exe /optimize+ /platform:anycpu `
    /main:Jvdp.LayoutTests.LayoutMatrixRunner `
    /out:$runner `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    (Join-Path $projectRoot '.generated\OverlayBuildInfo.cs') `
    (Join-Path $projectRoot 'shared\StartMenuShortcut.cs') `
    (Join-Path $projectRoot 'pc-overlay\InstanceActivation.cs') `
    (Join-Path $projectRoot 'pc-overlay\LightCheckCycle.cs') `
    (Join-Path $projectRoot 'pc-overlay\DarkroomNavigation.cs') `
    (Join-Path $projectRoot 'pc-overlay\LightDarkroomOverlay.cs') `
    (Join-Path $projectRoot 'tools\LayoutMatrixRunner.cs')
if ($LASTEXITCODE -ne 0) { throw 'Layout matrix runner compilation failed.' }

Copy-Item -LiteralPath (Join-Path $projectRoot `
    'pc-overlay\JvdpLightDarkroomOverlay.exe.config') `
    -Destination ($runner + '.config') -Force
$runnerArguments = @($OutputDirectory)
if ($Headless -or $env:GITHUB_ACTIONS -eq 'true') {
    $runnerArguments += '--headless'
}
& $runner @runnerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Layout matrix found problems. See $OutputDirectory"
}
Write-Host "Layout matrix passed: $OutputDirectory"
