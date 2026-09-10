$ErrorActionPreference = 'Stop'
$taskOutput = Join-Path $PSScriptRoot 'artifacts\reliability-tests'
New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskRunner = Join-Path $taskOutput 'ReliabilityTests.exe'
& $taskCompiler /nologo /target:exe /main:ReliabilityTests /out:$taskRunner `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll `
    (Join-Path $PSScriptRoot '.generated\OverlayBuildInfo.cs') `
    (Join-Path $PSScriptRoot 'shared\*.cs') `
    (Join-Path $PSScriptRoot 'pc-overlay\*.cs') `
    (Join-Path $PSScriptRoot 'installer\UpdateTransaction.cs') `
    (Join-Path $PSScriptRoot 'tools\ReliabilityTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Reliability test compilation failed.' }
$taskCase = Join-Path $taskOutput ([Guid]::NewGuid().ToString('N'))
& $taskRunner $taskCase
if ($LASTEXITCODE -ne 0) { throw 'Reliability regression test failed.' }

# Exercise the real installer failure path in a disposable, explicitly named
# directory. --test-dir bypasses all installed-app, registry and desktop actions.
$taskInstaller = Join-Path $PSScriptRoot 'artifacts\JvdP-Photobooth-Lichtsensor-Installatie.exe'
$taskInstall = Join-Path $taskCase 'installer rollback'
$taskArguments = @('--quiet', ('--test-dir="{0}"' -f $taskInstall))
$taskProcess = Start-Process -FilePath $taskInstaller -ArgumentList $taskArguments -Wait -PassThru -WindowStyle Hidden
if ($taskProcess.ExitCode -ne 0) { throw 'Baseline isolated install failed.' }
$taskApp = Join-Path $taskInstall 'JvdpLightDarkroomOverlay.exe'
[IO.File]::AppendAllText($taskApp, 'previous-version-test-marker')
$taskBefore = (Get-FileHash -Algorithm SHA256 -LiteralPath $taskApp).Hash
$taskProcess = Start-Process -FilePath $taskInstaller -ArgumentList ($taskArguments + '--test-fail-after-files') -Wait -PassThru -WindowStyle Hidden
if ($taskProcess.ExitCode -eq 0) { throw 'Injected installation failure was not reported.' }
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $taskApp).Hash -ne $taskBefore) { throw 'Previous application was not restored.' }
if (Test-Path -LiteralPath (Join-Path $taskInstall 'update-in-progress.txt')) { throw 'Rollback did not complete.' }
Write-Host 'PASS: real installer fault injection restored previous application; isolated directory only.'
