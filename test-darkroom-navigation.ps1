$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskOutput = Join-Path $taskRoot 'artifacts\navigation-tests'
New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) {
    $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$taskRunner = Join-Path $taskOutput 'DarkroomNavigationTests.exe'
& $taskCompiler /nologo /target:exe /optimize+ /platform:anycpu `
    /out:$taskRunner /reference:System.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    (Join-Path $taskRoot 'pc-overlay\LightCheckCycle.cs') `
    (Join-Path $taskRoot 'pc-overlay\DarkroomNavigation.cs') `
    (Join-Path $taskRoot 'pc-overlay\DarkroomIsoControl.cs') `
    (Join-Path $taskRoot 'tools\DarkroomNavigationTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Navigation test compilation failed.' }
& $taskRunner
if ($LASTEXITCODE -ne 0) { throw 'Navigation regression test failed.' }

$taskNativeRunner = Join-Path $taskOutput 'NativeCameraTests.exe'
& $taskCompiler /nologo /target:exe /out:$taskNativeRunner `
    /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    (Join-Path $taskRoot 'pc-overlay\DarkroomNavigation.cs') `
    (Join-Path $taskRoot 'pc-overlay\DarkroomIsoControl.cs') `
    (Join-Path $taskRoot 'tools\NativeCameraTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native camera test compilation failed.' }
& $taskNativeRunner (Join-Path $taskOutput ([Guid]::NewGuid().ToString('N')))
if ($LASTEXITCODE -ne 0) { throw 'Native camera navigation/commit test failed.' }
