[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$SkipBuild
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

function Resolve-FrameworkAssembly([string]$name) {
    $gacRoot = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL'
    $assembly = Get-ChildItem -LiteralPath (Join-Path $gacRoot $name) `
        -Recurse -File -Filter "$name.dll" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $assembly) {
        throw "Required framework assembly was not found: $name"
    }
    return $assembly.FullName
}

$runner = Join-Path $OutputDirectory 'LayoutMatrixRunner.exe'
$uiAutomationClient = Resolve-FrameworkAssembly 'UIAutomationClient'
$uiAutomationTypes = Resolve-FrameworkAssembly 'UIAutomationTypes'
$windowsBase = Resolve-FrameworkAssembly 'WindowsBase'
& $csc /nologo /target:exe /optimize+ /platform:anycpu `
    /main:Jvdp.LayoutTests.LayoutMatrixRunner `
    /out:$runner `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:$uiAutomationClient `
    /reference:$uiAutomationTypes `
    /reference:$windowsBase `
    (Join-Path $projectRoot '.generated\OverlayBuildInfo.cs') `
    (Join-Path $projectRoot 'pc-overlay\LightDarkroomOverlay.cs') `
    (Join-Path $projectRoot 'tools\LayoutMatrixRunner.cs')
if ($LASTEXITCODE -ne 0) { throw 'Layout matrix runner compilation failed.' }

Copy-Item -LiteralPath (Join-Path $projectRoot `
    'pc-overlay\JvdpLightDarkroomOverlay.exe.config') `
    -Destination ($runner + '.config') -Force
& $runner $OutputDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Layout matrix found problems. See $OutputDirectory"
}
Write-Host "Layout matrix passed: $OutputDirectory"
