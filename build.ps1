[CmdletBinding()]
param(
    [switch]$SkipFirmware
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$artifactDirectory = Join-Path $projectRoot 'artifacts'
$generatedDirectory = Join-Path $projectRoot '.generated'
$version = (Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'VERSION')).Trim()

if ($version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
    throw "VERSION '$version' is not a supported release version."
}

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $generatedDirectory | Out-Null

$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $csc) {
    throw 'The .NET Framework C# compiler was not found.'
}

function Resolve-FrameworkAssembly([string]$name) {
    $gacRoot = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL'
    $assembly = Get-ChildItem -LiteralPath (Join-Path $gacRoot $name) `
        -Recurse -File -Filter "$name.dll" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $assembly) {
        throw "Required .NET Framework assembly was not found: $name"
    }
    return $assembly.FullName
}

$uiAutomationClient = Resolve-FrameworkAssembly 'UIAutomationClient'
$uiAutomationTypes = Resolve-FrameworkAssembly 'UIAutomationTypes'
$windowsBase = Resolve-FrameworkAssembly 'WindowsBase'

function ConvertTo-CSharpLiteral([string]$value) {
    return $value.Replace('\', '\\').Replace('"', '\"')
}

function Write-BuildInfo(
    [string]$path,
    [string]$namespace,
    [bool]$includeBoothDefaults
) {
    $numericVersion = ($version -split '[-+]')[0]
    $parts = @($numericVersion.Split('.'))
    while ($parts.Count -lt 4) { $parts += '0' }
    $assemblyVersion = ($parts[0..3] -join '.')
    $apSsid = if ($env:JVDP_AP_SSID) { $env:JVDP_AP_SSID } else { 'JvdP-LightSensor' }
    $apPassword = if ($env:JVDP_AP_PASSWORD) { $env:JVDP_AP_PASSWORD } else { 'CHANGE-ME' }
    $content = @(
        'using System.Reflection;',
        "[assembly: AssemblyVersion(`"$assemblyVersion`")]",
        "[assembly: AssemblyFileVersion(`"$assemblyVersion`")]",
        "[assembly: AssemblyInformationalVersion(`"$(ConvertTo-CSharpLiteral $version)`")]",
        '',
        "namespace $namespace",
        '{',
        '    internal static class BuildInfo',
        '    {',
        "        internal const string Version = `"$(ConvertTo-CSharpLiteral $version)`";"
    )
    if ($includeBoothDefaults) {
        $content += "        internal const string ApSsid = `"$(ConvertTo-CSharpLiteral $apSsid)`";"
        $content += "        internal const string ApPassword = `"$(ConvertTo-CSharpLiteral $apPassword)`";"
    }
    $content += @('    }', '}')
    [IO.File]::WriteAllLines($path, $content, [Text.UTF8Encoding]::new($false))
}

$overlayBuildInfo = Join-Path $generatedDirectory 'OverlayBuildInfo.cs'
$updaterBuildInfo = Join-Path $generatedDirectory 'UpdaterBuildInfo.cs'
$installerBuildInfo = Join-Path $generatedDirectory 'InstallerBuildInfo.cs'
Write-BuildInfo $overlayBuildInfo 'Jvdp.LightDarkroomOverlay' $true
Write-BuildInfo $updaterBuildInfo 'Jvdp.AutoUpdater' $false
Write-BuildInfo $installerBuildInfo 'Jvdp.LightDarkroomInstaller' $false

$overlayExe = Join-Path $artifactDirectory 'JvdpLightDarkroomOverlay.exe'
$updaterExe = Join-Path $artifactDirectory 'JvdpAutoUpdater.exe'
$installerExe = Join-Path $artifactDirectory 'JvdP-Photobooth-Lichtsensor-Installatie.exe'
$appIcon = Join-Path $projectRoot 'pc-overlay\jvdp-light-bulb.ico'

if (-not (Test-Path -LiteralPath $appIcon)) {
    throw "Application icon was not found: $appIcon"
}

& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    /out:$overlayExe `
    ("/win32icon:{0}" -f $appIcon) `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:$uiAutomationClient `
    /reference:$uiAutomationTypes `
    /reference:$windowsBase `
    $overlayBuildInfo `
    (Join-Path $projectRoot 'pc-overlay\LightDarkroomOverlay.cs')
if ($LASTEXITCODE -ne 0) { throw 'Overlay compilation failed.' }

& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    /out:$updaterExe `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Security.dll `
    /reference:System.Windows.Forms.dll `
    $updaterBuildInfo `
    (Join-Path $projectRoot 'updater\JvdpAutoUpdater.cs')
if ($LASTEXITCODE -ne 0) { throw 'Updater compilation failed.' }

& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    /out:$installerExe `
    ("/win32icon:{0}" -f $appIcon) `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Windows.Forms.dll `
    ("/resource:{0},JvdpLightDarkroomOverlay.exe" -f $overlayExe) `
    ("/resource:{0},JvdpAutoUpdater.exe" -f $updaterExe) `
    $installerBuildInfo `
    (Join-Path $projectRoot 'installer\InstallerProgram.cs') `
    (Join-Path $projectRoot 'installer\InstallOperations.cs') `
    (Join-Path $projectRoot 'installer\RegistryOperations.cs') `
    (Join-Path $projectRoot 'installer\ProcessOperations.cs')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

if (-not $SkipFirmware) {
    $pio = Get-Command pio -ErrorAction SilentlyContinue
    if (-not $pio) { throw 'PlatformIO (pio) is required for the firmware build.' }
    & $pio.Source run --project-dir $projectRoot --environment esp32-c3-devkitm-1
    if ($LASTEXITCODE -ne 0) { throw 'Firmware compilation failed.' }
    $firmware = Join-Path $projectRoot '.pio\build\esp32-c3-devkitm-1\firmware.bin'
    if (-not (Test-Path -LiteralPath $firmware)) {
        throw "Firmware output was not found at $firmware"
    }
    Copy-Item -LiteralPath $firmware -Destination (
        Join-Path $artifactDirectory 'firmware.bin') -Force
}

Write-Host "Build complete: version $version"
Get-ChildItem -LiteralPath $artifactDirectory -File |
    Select-Object Name,Length | Format-Table -AutoSize
