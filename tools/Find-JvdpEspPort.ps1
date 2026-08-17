[CmdletBinding()]
param(
    [int]$BaudRate = 115200,
    [int]$TimeoutSeconds = 6
)

$ErrorActionPreference = 'Stop'
$ports = [IO.Ports.SerialPort]::GetPortNames() |
    Sort-Object { [int]($_ -replace '\D', '') }

foreach ($portName in $ports) {
    $port = [IO.Ports.SerialPort]::new($portName, $BaudRate)
    $port.DtrEnable = $false
    $port.RtsEnable = $false
    $port.ReadTimeout = 400
    try {
        $port.Open()
        $port.DiscardInBuffer()
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            try {
                $line = $port.ReadLine().Trim()
                if ($line -match '^JVDP\|light=(\d+)\|iso=(\d+)$') {
                    [PSCustomObject]@{
                        Port = $portName
                        Light = [int]$Matches[1]
                        Iso = [int]$Matches[2]
                        Validated = $true
                    }
                    exit 0
                }
            }
            catch [TimeoutException] { }
        }
    }
    catch {
        Write-Verbose "$portName skipped: $($_.Exception.Message)"
    }
    finally {
        if ($port.IsOpen) { $port.Close() }
        $port.Dispose()
    }
}

Write-Error 'No JvdP ESP was found on the available serial ports.'
exit 1
