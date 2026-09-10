$ErrorActionPreference = 'Stop'
$taskOutput = Join-Path $PSScriptRoot ('artifacts\update-shutdown-tests\' + [Guid]::NewGuid().ToString('N'))
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
foreach ($taskVariant in @('legacy', 'maintenance', 'current', 'future')) {
    $taskDirectory = Join-Path $taskOutput $taskVariant
    New-Item -ItemType Directory -Force -Path $taskDirectory | Out-Null
    $taskExe = Join-Path $taskDirectory 'JvdpLightDarkroomOverlay.exe'
    & $taskCompiler /nologo /target:winexe /out:$taskExe /define:$($taskVariant.ToUpperInvariant()) `
        /reference:System.dll /reference:System.Windows.Forms.dll `
        (Join-Path $PSScriptRoot 'tools\UpdateShutdownFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Shutdown fixture compilation failed.' }
}
$taskRunner = Join-Path $taskOutput 'UpdateShutdownTests.exe'
& $taskCompiler /nologo /target:exe /out:$taskRunner /reference:System.dll `
    (Join-Path $PSScriptRoot 'shared\BoothCoordination.cs') `
    (Join-Path $PSScriptRoot 'installer\OverlayShutdown.cs') `
    (Join-Path $PSScriptRoot 'tools\UpdateShutdownTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Update shutdown test compilation failed.' }
& $taskRunner $taskOutput
if ($LASTEXITCODE -ne 0) { throw 'Update shutdown regression test failed.' }
