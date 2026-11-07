$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$xamlPath = Join-Path $repoRoot 'src\DjShrek.Draiver.App\MainWindow.xaml'
$codePath = Join-Path $repoRoot 'src\DjShrek.Draiver.App\MainWindow.xaml.cs'
$themePath = Join-Path $repoRoot 'src\DjShrek.Draiver.App\Services\ThemeManager.cs'
$artworkPath = Join-Path $repoRoot 'src\DjShrek.Draiver.App\Controls\ThemeArtwork.cs'
$registryPath = Join-Path $repoRoot 'src\DjShrek.Draiver.App\Services\RegistryMaintenanceService.cs'
$cleanupPath = Join-Path $repoRoot 'src\DjShrek.Draiver.App\Services\JunkCleanupService.cs'
$licensePath = Join-Path $repoRoot 'LICENSE-TERMS-RU.txt'
$installerPath = Join-Path $repoRoot 'installer\DjShrek-Draiver.iss'
$iconPath = Join-Path $repoRoot 'assets\DjShrek-Draiver.ico'

[xml]$xaml = Get-Content -LiteralPath $xamlPath -Raw
$code = Get-Content -LiteralPath $codePath -Raw
$themeCode = Get-Content -LiteralPath $themePath -Raw
$installer = Get-Content -LiteralPath $installerPath -Raw
$license = Get-Content -LiteralPath $licensePath -Raw
$icon = [System.IO.File]::ReadAllBytes($iconPath)

if ($license.Length -lt 1000) { throw 'License agreement is missing or unexpectedly short.' }
if ($installer -notmatch '(?m)^LicenseFile=') { throw 'Installer must display an explicit license acceptance page.' }
if ($icon.Length -lt 22 -or $icon[0] -ne 0 -or $icon[1] -ne 0 -or $icon[2] -ne 1) { throw 'ICO header validation failed.' }

foreach ($required in @($themePath, $artworkPath, $registryPath, $cleanupPath)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required feature source is missing: $required" }
}

$requiredPanels = @('DashboardPanel','DevicesPanel','DriversPanel','RegistryPanel','CleanupPanel','SettingsPanel')
foreach ($panel in $requiredPanels) {
    if ($xamlPath -and (Get-Content -LiteralPath $xamlPath -Raw) -notmatch ('x:Name="' + [regex]::Escape($panel) + '"')) {
        throw "Required UI panel is missing: $panel"
    }
}

$themeMatches = [regex]::Matches($themeCode, 'new AppTheme\("(?<id>\d{2})"')
$themeIds = @($themeMatches | ForEach-Object { $_.Groups['id'].Value })
if ($themeIds.Count -ne 12 -or ($themeIds | Select-Object -Unique).Count -ne 12) {
    throw "Expected twelve unique visual themes; found $($themeIds.Count)."
}

$languageTags = @('ru','en','de','fr','es','pt_BR','it','pl','tr','uk','zh_CN','ja')
foreach ($tag in $languageTags) {
    if ($xamlText -and (Get-Content -LiteralPath $xamlPath -Raw) -notmatch ('Tag="' + [regex]::Escape($tag) + '"')) {
        throw "Language selector is missing supported language: $tag"
    }
}

$xamlText = Get-Content -LiteralPath $xamlPath -Raw
if ([regex]::Matches($xamlText, 'DisplayMemberPath="Name"').Count -lt 2) {
    throw 'Both theme selectors must display the human-readable Name property.'
}
if ($xamlText -match 'AppTheme\s*\{\s*Name') {
    throw 'Theme selector contains a technical record ToString display instead of a name binding.'
}

$eventAttributes = @('Click','TextChanged','Checked','Unchecked','SelectionChanged')
$xpath = '//*[' + (($eventAttributes | ForEach-Object { '@' + $_ }) -join ' or ') + ']'
$eventNames = @()
foreach ($node in $xaml.SelectNodes($xpath)) {
    foreach ($attribute in $eventAttributes) {
        $value = $node.GetAttribute($attribute)
        if (-not [string]::IsNullOrWhiteSpace($value)) { $eventNames += $value }
    }
}
$missing = @()
foreach ($handler in ($eventNames | Sort-Object -Unique)) {
    if ($code -notmatch ('(?m)\b' + [regex]::Escape($handler) + '\s*\(')) { $missing += $handler }
}
if ($missing.Count -gt 0) { throw ('Missing event handlers: ' + ($missing -join ', ')) }

$requiredHandlers = @('Scan_Click','AutoUpdateDrivers_Click','Backup_Click','RestoreDrivers_Click',
    'RegistryScan_Click','RegistryRepair_Click','JunkScan_Click','JunkClean_Click',
    'ThemeSelector_SelectionChanged','SettingsThemeSelector_SelectionChanged')
foreach ($handler in $requiredHandlers) {
    if ($code -notmatch ('(?m)\b' + [regex]::Escape($handler) + '\s*\(')) {
        throw "Required application handler is missing: $handler"
    }
}
Write-Host "XAML parsed. Six panels, twelve unique themes, readable theme selectors, handlers, license and icon verified."
