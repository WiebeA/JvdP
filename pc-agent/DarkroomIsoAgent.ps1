param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'config.json')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class JvdpDarkroomNative {
    public delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(
        uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(
        IntPtr hWnd, EnumChildProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetDlgCtrlID(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(
        IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(
        IntPtr hWnd, uint message, IntPtr wParam, StringBuilder lParam);

    public const uint MouseLeftDown = 0x0002;
    public const uint MouseLeftUp = 0x0004;
    public const uint WmCommand = 0x0111;
    public const uint CbGetCount = 0x0146;
    public const uint CbGetCurSel = 0x0147;
    public const uint CbGetLbText = 0x0148;
    public const uint CbGetLbTextLen = 0x0149;
    public const uint CbSetCurSel = 0x014E;
    public const int CbnSelChange = 1;
}
'@

$Config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$LogDirectory = Join-Path $PSScriptRoot 'logs'
$StatePath = Join-Path $PSScriptRoot 'state.json'
$PidPath = Join-Path $PSScriptRoot 'agent.pid'
New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
[IO.File]::WriteAllText($PidPath, [string]$PID)

function Write-AgentLog {
    param([string]$Message)
    $line = '{0:yyyy-MM-dd HH:mm:ss.fff}  {1}' -f (Get-Date), $Message
    Add-Content -LiteralPath (Join-Path $LogDirectory 'agent.log') -Value $line
}

function Get-DarkroomProcess {
    foreach ($process in Get-Process | Where-Object MainWindowHandle -ne 0) {
        if ($process.MainWindowTitle -match $Config.darkroomWindowTitleRegex) {
            return $process
        }
    }
    return $null
}

function Get-NativeChildrenById {
    param(
        [IntPtr]$RootHandle,
        [int]$ControlId
    )

    $handles = [Collections.Generic.List[IntPtr]]::new()
    $callback = [JvdpDarkroomNative+EnumChildProc]{
        param([IntPtr]$handle, [IntPtr]$parameter)
        if ([JvdpDarkroomNative]::GetDlgCtrlID($handle) -eq $ControlId) {
            $handles.Add($handle)
        }
        return $true
    }
    [void][JvdpDarkroomNative]::EnumChildWindows(
        $RootHandle, $callback, [IntPtr]::Zero)
    return @($handles)
}

function Get-NativeRect {
    param([IntPtr]$Handle)
    $rect = New-Object JvdpDarkroomNative+RECT
    if (-not [JvdpDarkroomNative]::GetWindowRect($Handle, [ref]$rect)) {
        throw "Could not read bounds for native control $Handle."
    }
    return $rect
}

function Invoke-ScreenClick {
    param(
        [int]$X,
        [int]$Y
    )

    [void][JvdpDarkroomNative]::SetCursorPos($X, $Y)
    [JvdpDarkroomNative]::mouse_event(
        [JvdpDarkroomNative]::MouseLeftDown,
        0, 0, 0, [UIntPtr]::Zero)
    [JvdpDarkroomNative]::mouse_event(
        [JvdpDarkroomNative]::MouseLeftUp,
        0, 0, 0, [UIntPtr]::Zero)
}

function Invoke-NativeControlClick {
    param([IntPtr]$Handle)
    $rect = Get-NativeRect -Handle $Handle
    Invoke-ScreenClick `
        -X ([int](($rect.Left + $rect.Right) / 2)) `
        -Y ([int](($rect.Top + $rect.Bottom) / 2))
}

function Test-BoothMode {
    param([System.Diagnostics.Process]$Process)

    if ([JvdpDarkroomNative]::IsIconic($Process.MainWindowHandle)) {
        return $true
    }

    $window = Get-NativeRect -Handle $Process.MainWindowHandle
    $screen = [System.Windows.Forms.Screen]::FromHandle(
        $Process.MainWindowHandle).Bounds

    return $window.Left -le ($screen.Left + 20) -and
           $window.Top -le ($screen.Top + 20) -and
           $window.Right -ge ($screen.Right - 20) -and
           $window.Bottom -ge ($screen.Bottom - 20)
}

function Get-IsoComboHandle {
    param([System.Diagnostics.Process]$Process)
    $handles = Get-NativeChildrenById `
        -RootHandle $Process.MainWindowHandle `
        -ControlId ([int]$Config.isoControlId)
    if ($handles.Count -eq 0) {
        return [IntPtr]::Zero
    }
    return $handles[0]
}

function Open-CameraSettings {
    param([System.Diagnostics.Process]$Process)

    $combo = Get-IsoComboHandle -Process $Process
    if ($combo -ne [IntPtr]::Zero) {
        return $combo
    }

    $settingsHandles = Get-NativeChildrenById `
        -RootHandle $Process.MainWindowHandle `
        -ControlId ([int]$Config.settingsControlId)
    if ($settingsHandles.Count -eq 0) {
        throw 'Settings navigation was not found.'
    }

    $settingsHandle = $settingsHandles |
        Sort-Object {
            $rect = Get-NativeRect -Handle $_
            $rect.Right - $rect.Left
        } -Descending |
        Select-Object -First 1
    Invoke-NativeControlClick -Handle $settingsHandle
    Start-Sleep -Milliseconds 700

    $tabHandles = Get-NativeChildrenById `
        -RootHandle $Process.MainWindowHandle `
        -ControlId ([int]$Config.cameraTabControlId)
    if ($tabHandles.Count -eq 0) {
        throw 'Camera settings tabs were not found.'
    }

    $tabRect = Get-NativeRect -Handle $tabHandles[0]
    Invoke-ScreenClick `
        -X ([int]($tabRect.Left + (($tabRect.Right - $tabRect.Left) / 4))) `
        -Y ([int]($tabRect.Top + 20))
    Start-Sleep -Milliseconds 700

    $combo = Get-IsoComboHandle -Process $Process
    if ($combo -eq [IntPtr]::Zero) {
        throw 'The ISO dropdown was not found on the Camera page.'
    }
    return $combo
}

function Get-ComboItems {
    param([IntPtr]$ComboHandle)

    $count = [JvdpDarkroomNative]::SendMessage(
        $ComboHandle,
        [JvdpDarkroomNative]::CbGetCount,
        [IntPtr]::Zero,
        [IntPtr]::Zero).ToInt64()
    $selected = [JvdpDarkroomNative]::SendMessage(
        $ComboHandle,
        [JvdpDarkroomNative]::CbGetCurSel,
        [IntPtr]::Zero,
        [IntPtr]::Zero).ToInt64()

    $items = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $count; $index++) {
        $length = [JvdpDarkroomNative]::SendMessage(
            $ComboHandle,
            [JvdpDarkroomNative]::CbGetLbTextLen,
            [IntPtr]$index,
            [IntPtr]::Zero).ToInt64()
        $builder = [Text.StringBuilder]::new([int]$length + 1)
        [void][JvdpDarkroomNative]::SendMessage(
            $ComboHandle,
            [JvdpDarkroomNative]::CbGetLbText,
            [IntPtr]$index,
            $builder)
        $items.Add([PSCustomObject]@{
            Index = $index
            Value = $builder.ToString()
            Selected = $index -eq $selected
        })
    }
    return @($items)
}

function Set-IsoComboValue {
    param(
        [IntPtr]$ComboHandle,
        [int]$Iso
    )

    $items = Get-ComboItems -ComboHandle $ComboHandle
    $target = $items | Where-Object Value -eq ([string]$Iso) |
        Select-Object -First 1
    if ($null -eq $target) {
        throw "ISO $Iso is not present in the connected camera dropdown."
    }

    $current = $items | Where-Object Selected | Select-Object -First 1
    if ($null -ne $current -and $current.Value -eq [string]$Iso) {
        Write-AgentLog "Verified current Darkroom ISO: already $Iso; no setting change needed."
        return $false
    }

    [void][JvdpDarkroomNative]::SendMessage(
        $ComboHandle,
        [JvdpDarkroomNative]::CbSetCurSel,
        [IntPtr]$target.Index,
        [IntPtr]::Zero)

    $parent = [JvdpDarkroomNative]::GetParent($ComboHandle)
    $command = ([JvdpDarkroomNative]::CbnSelChange -shl 16) -bor
               ([int]$Config.isoControlId -band 0xffff)
    [void][JvdpDarkroomNative]::SendMessage(
        $parent,
        [JvdpDarkroomNative]::WmCommand,
        [IntPtr]$command,
        $ComboHandle)
    Start-Sleep -Milliseconds 500

    $selected = Get-ComboItems -ComboHandle $ComboHandle |
        Where-Object Selected |
        Select-Object -First 1
    if ($null -eq $selected -or $selected.Value -ne [string]$Iso) {
        throw "Darkroom did not confirm ISO $Iso."
    }
    return $true
}

function Start-DarkroomBooth {
    param([System.Diagnostics.Process]$Process)

    $handles = Get-NativeChildrenById `
        -RootHandle $Process.MainWindowHandle `
        -ControlId ([int]$Config.startBoothControlId)
    if ($handles.Count -eq 0) {
        throw 'Start Booth control was not found.'
    }
    Invoke-NativeControlClick -Handle $handles[0]

    $deadline = (Get-Date).AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 250
        if (Test-BoothMode -Process $Process) {
            return
        }
    } while ((Get-Date) -lt $deadline)

    throw 'Darkroom did not return to photobooth mode.'
}

function Set-DarkroomIso {
    param([int]$Iso)

    $process = Get-DarkroomProcess
    if ($null -eq $process) {
        Write-AgentLog 'Darkroom Booth is not running.'
        return $false
    }
    if (-not (Test-BoothMode -Process $process)) {
        Write-AgentLog 'Darkroom is running but is not in photobooth mode; no change made.'
        return $false
    }
    if ($Config.dryRun) {
        Write-AgentLog "Dry run: would change Darkroom ISO to $Iso."
        return $true
    }

    $leftBooth = $false
    try {
        [void][JvdpDarkroomNative]::ShowWindowAsync(
            $process.MainWindowHandle, 9)
        [void][JvdpDarkroomNative]::SetForegroundWindow(
            $process.MainWindowHandle)
        Start-Sleep -Milliseconds 250
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        $leftBooth = $true
        Start-Sleep -Milliseconds 900

        $comboHandle = Open-CameraSettings -Process $process
        $isoChanged = Set-IsoComboValue -ComboHandle $comboHandle -Iso $Iso
        Start-DarkroomBooth -Process $process
        $leftBooth = $false
        if ($isoChanged) {
            Write-AgentLog "Darkroom ISO changed to $Iso and booth restarted."
        } else {
            Write-AgentLog "Darkroom ISO $Iso confirmed and booth restarted."
        }
        return $true
    } catch {
        Write-AgentLog "ISO change failed: $($_.Exception.Message)"
        if ($leftBooth) {
            try {
                Start-DarkroomBooth -Process $process
                Write-AgentLog 'Booth restarted after failed ISO change.'
            } catch {
                Write-AgentLog "Could not restart booth: $($_.Exception.Message)"
            }
        }
        return $false
    }
}

function Find-EspSerialPort {
    if ($Config.serialPort -ne 'auto') {
        return [string]$Config.serialPort
    }

    $esp = Get-CimInstance Win32_SerialPort |
        Where-Object {
            $_.PNPDeviceID -match 'VID_303A.PID_1001' -or
            $_.Description -match '(?i)ESP32|USB Serial|Serieel USB'
        } |
        Select-Object -First 1
    if ($null -eq $esp) {
        return $null
    }
    return [string]$esp.DeviceID
}

$lastAppliedIso = $null
if (Test-Path -LiteralPath $StatePath) {
    try {
        $savedState = Get-Content -LiteralPath $StatePath -Raw |
            ConvertFrom-Json
        Write-AgentLog (
            "Previous recorded ISO was {0}; current Darkroom ISO will be verified." -f
            [int]$savedState.lastAppliedIso)
    } catch {
        Write-AgentLog 'Saved agent state could not be read; starting clean.'
    }
}

$serial = $null
$candidateIso = $null
$candidateSince = $null
$lastAttempt = [DateTime]::MinValue
$lastObservedBoothMode = $null

Write-AgentLog 'Darkroom ISO agent started.'
try {
    while ($true) {
        if ($null -eq $serial -or -not $serial.IsOpen) {
            $portName = Find-EspSerialPort
            if ([string]::IsNullOrWhiteSpace($portName)) {
                Start-Sleep -Seconds 3
                continue
            }

            try {
                $serial = [IO.Ports.SerialPort]::new(
                    $portName, 115200, 'None', 8, 'One')
                $serial.DtrEnable = $false
                $serial.RtsEnable = $false
                $serial.ReadTimeout = 1500
                $serial.NewLine = "`n"
                $serial.Open()
                Write-AgentLog "Connected to ESP on $portName."
            } catch {
                Write-AgentLog "Could not open ${portName}: $($_.Exception.Message)"
                if ($null -ne $serial) {
                    $serial.Dispose()
                    $serial = $null
                }
                Start-Sleep -Seconds 3
                continue
            }
        }

        try {
            $line = $serial.ReadLine().Trim()
            if ($line -notmatch '^JVDP\|light=(\d{1,3})\|iso=(\d+)$') {
                continue
            }

            $light = [int]$Matches[1]
            $desiredIso = [int]$Matches[2]
            $now = Get-Date

            $darkroom = Get-DarkroomProcess
            $boothMode = $null -ne $darkroom -and
                         (Test-BoothMode -Process $darkroom)
            if ($null -ne $lastObservedBoothMode -and
                $boothMode -ne $lastObservedBoothMode) {
                $lastAppliedIso = $null
                if ($boothMode) {
                    $candidateSince = $now
                    Write-AgentLog (
                        'Darkroom returned to photobooth mode; current ISO will be reverified after the stability delay.')
                } else {
                    Write-AgentLog (
                        'Darkroom left photobooth mode; cached ISO verification invalidated.')
                }
            }
            $lastObservedBoothMode = $boothMode

            if ($candidateIso -ne $desiredIso) {
                $candidateIso = $desiredIso
                $candidateSince = $now
                Write-AgentLog "Candidate ISO $desiredIso at light $light."
                continue
            }

            $stableFor = ($now - $candidateSince).TotalSeconds
            $retryReady = ($now - $lastAttempt).TotalSeconds -ge
                [double]$Config.retrySeconds

            if ($stableFor -ge [double]$Config.stableSeconds -and
                $desiredIso -ne $lastAppliedIso -and
                $retryReady) {
                $lastAttempt = $now
                if (Set-DarkroomIso -Iso $desiredIso) {
                    $lastAppliedIso = $desiredIso
                    @{
                        lastAppliedIso = $lastAppliedIso
                        updatedAt = (Get-Date).ToString('o')
                    } |
                        ConvertTo-Json |
                        Set-Content -LiteralPath $StatePath
                }
            }
        } catch [System.TimeoutException] {
            continue
        } catch {
            Write-AgentLog "Serial connection lost: $($_.Exception.Message)"
            if ($null -ne $serial) {
                if ($serial.IsOpen) {
                    $serial.Close()
                }
                $serial.Dispose()
                $serial = $null
            }
            Start-Sleep -Seconds 2
        }
    }
} finally {
    if ($null -ne $serial) {
        if ($serial.IsOpen) {
            $serial.Close()
        }
        $serial.Dispose()
    }
    Remove-Item -LiteralPath $PidPath -Force -ErrorAction SilentlyContinue
    Write-AgentLog 'Darkroom ISO agent stopped.'
}
