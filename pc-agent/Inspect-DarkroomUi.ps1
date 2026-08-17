$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$process = Get-Process | Where-Object {
    $_.MainWindowHandle -ne 0 -and
    $_.MainWindowTitle -match '(?i)darkroom.*booth|booth.*darkroom'
} | Select-Object -First 1

if ($null -eq $process) {
    throw 'Darkroom Booth window was not found.'
}

$root = [System.Windows.Automation.AutomationElement]::FromHandle(
    $process.MainWindowHandle
)
$elements = $root.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition
)

$result = foreach ($element in $elements) {
    try {
        [PSCustomObject]@{
            Name = $element.Current.Name
            AutomationId = $element.Current.AutomationId
            ControlType = $element.Current.ControlType.ProgrammaticName
            Enabled = $element.Current.IsEnabled
            Bounds = $element.Current.BoundingRectangle.ToString()
        }
    } catch {
        continue
    }
}

$path = Join-Path $PSScriptRoot 'Darkroom-UI-Inspection.csv'
$result | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding UTF8
Write-Host "Inspection saved to $path"
pause
