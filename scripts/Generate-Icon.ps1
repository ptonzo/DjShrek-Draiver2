$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$icon = Join-Path $root 'assets\DjShrek-Draiver.ico'
if (-not (Test-Path -LiteralPath $icon)) {
    throw "The approved application icon is missing: $icon"
}
$bytes = [System.IO.File]::ReadAllBytes($icon)
if ($bytes.Length -lt 22 -or $bytes[0] -ne 0 -or $bytes[1] -ne 0 -or $bytes[2] -ne 1 -or $bytes[3] -ne 0) {
    throw "The application icon is not a valid ICO file: $icon"
}
# Do not regenerate or overwrite the approved icon during CI.
Write-Host "Using approved DjShrek-Draiver icon: $icon ($($bytes.Length) bytes)"
