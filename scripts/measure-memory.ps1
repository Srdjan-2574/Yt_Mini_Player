# Shows how much RAM YtMiniPlayer and YtMiniLite really use, including every child process they started
# (WebView2 processes for YtMiniPlayer; short-lived yt-dlp/deno for YtMiniLite).
# "MB" is the private working set - the same number Task Manager shows as "Memory (active private working set)".
# Usage: powershell -ExecutionPolicy Bypass -File .\scripts\measure-memory.ps1
$all = Get-CimInstance Win32_Process
$perf = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process

foreach ($name in 'YtMiniPlayer', 'YtMiniLite') {
    $root = Get-Process $name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $root) {
        Write-Host "$name is not running"
        continue
    }
    # The app plus all of its children, grandchildren, ...
    $tree = @($root.Id)
    $level = @($root.Id)
    while ($level.Count) {
        $level = @($all | Where-Object { $level -contains $_.ParentProcessId } | ForEach-Object { $_.ProcessId })
        $tree += $level
    }
    $rows = $perf | Where-Object { $tree -contains $_.IDProcess } | Sort-Object WorkingSetPrivate -Descending
    $total = [math]::Round(($rows | Measure-Object WorkingSetPrivate -Sum).Sum / 1MB)
    Write-Host ("{0}: {1} MB in {2} process(es)" -f $name, $total, @($rows).Count)
    foreach ($row in $rows) {
        $process = $all | Where-Object ProcessId -eq $row.IDProcess
        $kind = if ($process.CommandLine -match '--type=([\w-]+)') { $Matches[1] } else { '' }
        Write-Host ("    {0,-20} {1,-14} {2,6} MB" -f $process.Name, $kind, [math]::Round($row.WorkingSetPrivate / 1MB))
    }
}
