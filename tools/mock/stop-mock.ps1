# Stops exactly what tools\mock\start-mock.ps1 started (listed in tools\mock\data\mock-state.json), with their
# child processes (MediaMTX's on-demand ffmpeg). A PID reused by another program is left alone (start time differs).
#
#   powershell -ExecutionPolicy Bypass -File tools\mock\stop-mock.ps1
$ErrorActionPreference = 'Stop'
$stateFile = Join-Path $PSScriptRoot 'data\mock-state.json'
if (-not (Test-Path $stateFile)) { 'The mock is not running (no state file).'; return }

$state = Get-Content $stateFile -Raw | ConvertFrom-Json
# Reverse order: MockNvr and the publishers before MediaMTX.
$list = @($state.processes)
[array]::Reverse($list)
foreach ($s in $list) {
    $p = Get-Process -Id $s.pid -ErrorAction SilentlyContinue
    if (-not $p) { "  $($s.name) ($($s.pid)) already gone"; continue }
    if ($p.StartTime.ToFileTimeUtc() -ne [long]$s.start) { "  $($s.name) ($($s.pid)) already gone (PID reused by $($p.ProcessName), left alone)"; continue }
    & taskkill.exe /PID $s.pid /T /F 2>&1 | Out-Null
    "  stopped $($s.name) ($($s.pid))"
}
Remove-Item $stateFile
'Mock stopped.'
