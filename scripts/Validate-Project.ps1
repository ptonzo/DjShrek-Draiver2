$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$xamlPath = Join-Path $repoRoot 'src\DjShrek.Draiver.App\MainWindow.xaml'
$codePath = Join-Path $repoRoot 'src\DjShrek.Draiver.App\MainWindow.xaml.cs'
$licensePath = Join-Path $repoRoot 'LICENSE-TERMS-RU.txt'
$installerPath = Join-Path $repoRoot 'installer\DjShrek-Draiver.iss'
$iconPath = Join-Path $repoRoot 'assets\DjShrek-Draiver.ico'

[xml]$xaml = Get-Content -LiteralPath $xamlPath -Raw
$code = Get-Content -LiteralPath $codePath -Raw
$installer = Get-Content -LiteralPath $installerPath -Raw
$license = Get-Content -LiteralPath $licensePath -Raw
$icon = [System.IO.File]::ReadAllBytes($iconPath)

if ($license.Length -lt 1000) { throw 'License agreement is missing or unexpectedly short.' }
if ($installer -notmatch '(?m)^LicenseFile=') { throw 'Installer must display an explicit license acceptance page.' }
if ($icon.Length -lt 22 -or $icon[0] -ne 0 -or $icon[1] -ne 0 -or $icon[2] -ne 1) { throw 'ICO header validation failed.' }

$eventNames = @()
foreach ($node in $xaml.SelectNodes('//*[@Click or @TextChanged or @Checked or @Unchecked]')) {
    foreach ($attribute in @('Click', 'TextChanged', 'Checked', 'Unchecked')) {
        $value = $node.GetAttribute($attribute)
        if (-not [string]::IsNullOrWhiteSpace($value)) { $eventNames += $value }
    }
}
$missing = @()
foreach ($handler in ($eventNames | Sort-Object -Unique)) {
    if ($code -notmatch ('(?m)\b' + [regex]::Escape($handler) + '\s*\(')) { $missing += $handler }
}
if ($missing.Count -gt 0) { throw ('Missing event handlers: ' + ($missing -join ', ')) }

$requiredHandlers = @('SearchWindowsDriverUpdates_Click','InstallWindowsDriverUpdates_Click','RestoreDrivers_Click','ExportDevices_Click')
foreach ($handler in $requiredHandlers) {
    if ($missing -contains $handler) { throw "Required application handler is not wired: $handler" }
}
Write-Host "XAML parsed successfully. $($eventNames.Count) event bindings checked. License and ICO checks passed."