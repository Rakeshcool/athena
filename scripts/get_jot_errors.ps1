# Fetch recent Jot-related error entries from the Windows Application event log.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/get_jot_errors.ps1

$events = Get-WinEvent -LogName Application -MaxEvents 200 -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'Jot' -and $_.LevelDisplayName -in @('Error','Warning','Critical') } |
    Select-Object -First 5

if (-not $events) { Write-Output "no Jot error events found"; exit }

foreach ($e in $events) {
    Write-Output ("=== {0} [{1}] {2}" -f $e.TimeCreated, $e.LevelDisplayName, $e.ProviderName)
    $lines = $e.Message -split "`n"
    $lines | Select-Object -First 20 | ForEach-Object { Write-Output $_ }
    Write-Output ""
}
