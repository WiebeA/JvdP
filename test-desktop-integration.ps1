$ErrorActionPreference = 'Stop'
$taskOutput = Join-Path $PSScriptRoot 'artifacts\desktop-integration-tests'
New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) {
    $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$taskRunner = Join-Path $taskOutput 'DesktopIntegrationTests.exe'
& $taskCompiler /nologo /target:exe /optimize+ /platform:anycpu `
    /out:$taskRunner /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll `
    (Join-Path $PSScriptRoot 'shared\StartMenuShortcut.cs') `
    (Join-Path $PSScriptRoot 'pc-overlay\InstanceActivation.cs') `
    (Join-Path $PSScriptRoot 'tools\DesktopIntegrationTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Desktop integration test compilation failed.' }
$taskInstall = Join-Path $taskOutput 'isolated installed app'
$taskInstaller = Join-Path $PSScriptRoot 'artifacts\JvdP-Photobooth-Lichtsensor-Installatie.exe'
$taskInstallRun = Start-Process -FilePath $taskInstaller -ArgumentList @(
    '--quiet', ('--test-dir="{0}"' -f $taskInstall)) -Wait -PassThru -WindowStyle Hidden
if ($taskInstallRun.ExitCode -ne 0) { throw 'Isolated installer test failed.' }
& $taskRunner $taskOutput (Join-Path $PSScriptRoot 'artifacts\JvdpLightDarkroomOverlay.exe') $taskInstall
if ($LASTEXITCODE -ne 0) { throw 'Desktop integration regression test failed.' }
