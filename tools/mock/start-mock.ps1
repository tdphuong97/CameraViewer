# Starts the test recorder: MediaMTX (RTSP), two GPL ffmpeg publishers (main H.265 1280x720, sub H.264 640x360,
# 15 fps, drawtext clock) and MockNvr (ISAPI over HTTP digest). Stop with tools\mock\stop-mock.ps1.
#
#   powershell -ExecutionPolicy Bypass -File tools\mock\start-mock.ps1 [-HttpPort 8080] [-RtspPort 554]
#       [-TimeMode localz|utc|offset] [-Download honor|whole|xml-error] [-DownloadCodec h265|h264]
#       [-DownloadMbps 0] [-Password Mock@1234] [-Lan] [-NewClips]
#
# Needs build\fetch-dev-tools.ps1 once (MediaMTX and the GPL ffmpeg in %LOCALAPPDATA%\CameraViewer-dev\mock).
# Data, clips, logs and the list of started processes live in tools\mock\data (gitignored).
param(
    [int]$HttpPort = 8080,
    [int]$RtspPort = 554,
    [string]$Password = 'Mock@1234',
    [ValidateSet('localz', 'utc', 'offset')][string]$TimeMode = 'localz',
    [ValidateSet('honor', 'whole', 'xml-error')][string]$Download = 'honor',
    [ValidateSet('h265', 'h264')][string]$DownloadCodec = 'h265',
    [double]$DownloadMbps = 0,
    [switch]$Lan,
    [switch]$NewClips
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$mockDir = $PSScriptRoot
$repo = Split-Path -Parent (Split-Path -Parent $mockDir)
$data = Join-Path $mockDir 'data'
$logs = Join-Path $data 'logs'
$stateFile = Join-Path $data 'mock-state.json'
$dev = Join-Path $env:LOCALAPPDATA 'CameraViewer-dev'
$ffmpeg = Join-Path $dev 'mock\ffmpeg\bin\ffmpeg.exe'
$mediamtx = Join-Path $dev 'mock\mediamtx\mediamtx.exe'
$bind = if ($Lan) { '0.0.0.0' } else { '127.0.0.1' }
$utf8 = New-Object System.Text.UTF8Encoding $false

foreach ($tool in $ffmpeg, $mediamtx) {
    if (-not (Test-Path $tool)) { throw "Missing $tool. Run once: powershell -ExecutionPolicy Bypass -File build\fetch-dev-tools.ps1" }
}
New-Item -ItemType Directory -Force $data, $logs | Out-Null

# Refuse to start twice: stop-mock.ps1 only knows the processes in the state file.
if (Test-Path $stateFile) {
    $old = Get-Content $stateFile -Raw | ConvertFrom-Json
    $alive = @($old.processes | Where-Object {
        $p = Get-Process -Id $_.pid -ErrorAction SilentlyContinue
        $p -and $p.StartTime.ToFileTimeUtc() -eq [long]$_.start
    })
    if ($alive.Count -gt 0) { throw 'The mock is already running. Run tools\mock\stop-mock.ps1 first.' }
    Remove-Item $stateFile
}

function Test-PortFree([int]$port) {
    if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
        if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { return $false }
    }
    try {
        $l = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Parse($bind)), $port
        $l.Start(); $l.Stop()
        return $true
    } catch { return $false }
}

function Wait-Port([int]$port, [int]$seconds, [string]$what, [string]$log) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $c = New-Object System.Net.Sockets.TcpClient
        try { $c.Connect('127.0.0.1', $port); return } catch { Start-Sleep -Milliseconds 250 } finally { $c.Dispose() }
    }
    $tail = if (Test-Path $log) { (Get-Content $log -Tail 15) -join "`n" } else { '' }
    throw "$what did not start on port $port. Last lines of ${log}:`n$tail"
}

# Font for the drawtext overlays. The colon is escaped twice: once for the filter graph, once for the option.
$fontPath = @("$env:WINDIR\Fonts\consola.ttf", "$env:WINDIR\Fonts\arial.ttf") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $fontPath) { throw 'No Consolas or Arial font found for the clock overlay.' }
$font = ($fontPath -replace '\\', '/') -replace ':', '\\:'

# ---- Clips (made once, cached): Annex-B streams for ISAPI downloads, an MP4 for RTSP playback. ----
$recText = "drawtext=fontfile=${font}:fontsize=36:fontcolor=white:box=1:boxcolor=black@0.55:boxborderw=10:x=30:y=30:text='MOCK %{pts\:hms}'"
$clips = @(
    @{ File = 'es-h265.hevc'; Args = "-c:v libx265 -preset fast -b:v 1000k -x265-params keyint=30:min-keyint=30:bframes=0:scenecut=0:repeat-headers=1:aud=1:log-level=error -f hevc" },
    @{ File = 'es-h264.h264'; Args = "-c:v libx264 -preset fast -profile:v main -b:v 1000k -g 30 -keyint_min 30 -sc_threshold 0 -bf 0 -x264-params aud=1:repeat-headers=1 -f h264" },
    @{ File = 'playback-h265.mp4'; Args = "-c:v libx265 -preset fast -b:v 1000k -x265-params keyint=30:min-keyint=30:bframes=0:scenecut=0:log-level=error -tag:v hvc1 -movflags +faststart -f mp4" }
)
foreach ($c in $clips) {
    $out = Join-Path $data $c.File
    if ((Test-Path $out) -and -not $NewClips) { continue }
    "Making $($c.File) (once)..."
    $argLine = "-hide_banner -nostdin -loglevel error -y -f lavfi -i testsrc2=size=1280x720:rate=15 -t 60 -vf `"$recText`" -pix_fmt yuv420p $($c.Args) `"$out`""
    $p = Start-Process -FilePath $ffmpeg -ArgumentList $argLine -NoNewWindow -Wait -PassThru -RedirectStandardError (Join-Path $logs 'clips.log')
    if ($p.ExitCode -ne 0 -or -not (Test-Path $out)) { throw "ffmpeg could not make $($c.File); see $(Join-Path $logs 'clips.log')" }
}

# ---- Ports ----
if (-not (Test-PortFree $HttpPort)) { throw "HTTP port $HttpPort is busy; use -HttpPort." }
if (-not (Test-PortFree $RtspPort)) {
    if ($RtspPort -eq 554 -and (Test-PortFree 8554)) { "Port 554 is busy, RTSP uses 8554."; $RtspPort = 8554 }
    else { throw "RTSP port $RtspPort is busy; use -RtspPort." }
}

# ---- MediaMTX config ----
# Readers (the app, ffprobe): the recorder's own user and password. Publishers: a random local-only account.
$pubUser = 'mockpub'
$pubPass = -join ((48..57) + (97..122) | Get-Random -Count 20 | ForEach-Object { [char]$_ })
$src = "rtsp://${pubUser}:${pubPass}@127.0.0.1:$RtspPort"
$ffmpegFwd = $ffmpeg -replace '\\', '/'
$playClip = (Join-Path $data 'playback-h265.mp4') -replace '\\', '/'
$yamlPassword = $Password -replace "'", "''"
$paths = New-Object System.Text.StringBuilder
[void]$paths.AppendLine('  live/main:')
[void]$paths.AppendLine('    source: publisher')
[void]$paths.AppendLine('  live/sub:')
[void]$paths.AppendLine('    source: publisher')
# Channel 5 is offline on the recorder: no live path for it.
foreach ($n in 1..17) {
    if ($n -eq 5) { continue }
    foreach ($s in @(@{ Id = '01'; Src = 'live/main' }, @{ Id = '02'; Src = 'live/sub' })) {
        [void]$paths.AppendLine("  Streaming/Channels/$n$($s.Id):")
        [void]$paths.AppendLine("    source: $src/$($s.Src)")
        [void]$paths.AppendLine('    sourceOnDemand: true')
        [void]$paths.AppendLine('    rtspTransport: tcp')
    }
}
# Recorded playback: any track, started on demand from the looped clip (starttime/endtime are ignored).
[void]$paths.AppendLine('  "~^Streaming/tracks/[0-9]+$":')
[void]$paths.AppendLine("    runOnDemand: '`"$ffmpegFwd`" -hide_banner -nostdin -loglevel error -re -stream_loop -1 -i `"$playClip`" -c copy -f rtsp -rtsp_transport tcp $src/`$MTX_PATH'")
[void]$paths.AppendLine('    runOnDemandRestart: true')
[void]$paths.AppendLine('    runOnDemandStartTimeout: 15s')
[void]$paths.AppendLine('    runOnDemandCloseAfter: 5s')

$yaml = @"
# Generated by tools\mock\start-mock.ps1. Do not edit; it is rewritten at every start.
logLevel: info
logDestinations: [stdout]
readTimeout: 10s
writeTimeout: 10s
authMethod: internal
authInternalUsers:
  - user: admin
    pass: '$yamlPassword'
    ips: []
    permissions:
      - action: read
      - action: playback
  - user: $pubUser
    pass: $pubPass
    ips: ['127.0.0.1', '::1']
    permissions:
      - action: publish
      - action: read
api: false
metrics: false
pprof: false
playback: false
rtsp: true
rtspTransports: [udp, tcp]
rtspEncryption: "no"
rtspAddress: ${bind}:$RtspPort
rtpAddress: ${bind}:18000
rtcpAddress: ${bind}:18001
rtspAuthMethods: [digest]
rtmp: false
hls: false
webrtc: false
srt: false
moq: false
paths:
$($paths.ToString())
"@
$yml = Join-Path $data 'mediamtx.yml'
[System.IO.File]::WriteAllText($yml, $yaml, $utf8)

$started = @()
function Start-Logged([string]$name, [string]$exe, [string]$argLine) {
    # Through cmd.exe launched by the shell (no handle inheritance; cmd writes the logs): with Start-Process
    # -RedirectStandardOutput the background processes would inherit every open handle of this script, and a caller
    # that captures its output would wait until stop-mock.ps1. The recorded PID is cmd's; stop-mock kills the tree.
    $out = Join-Path $logs "$name.log"
    $err = Join-Path $logs "$name.err.log"
    $p = Start-Process -FilePath $env:ComSpec -ArgumentList "/d /c `"`"$exe`" $argLine 1>`"$out`" 2>`"$err`"`"" `
        -WorkingDirectory $data -WindowStyle Hidden -PassThru
    $script:started += [pscustomobject]@{ name = $name; pid = $p.Id; start = $p.StartTime.ToFileTimeUtc() }
    Save-State
    return $p
}
function Save-State {
    $state = [ordered]@{
        processes = $script:started; bind = $bind; httpPort = $HttpPort; rtspPort = $RtspPort; user = 'admin'
        password = $Password; timeMode = $TimeMode; download = $Download; downloadCodec = $DownloadCodec
    }
    [System.IO.File]::WriteAllText($stateFile, ($state | ConvertTo-Json -Depth 4), $utf8)
}

try {
    # ---- MediaMTX ----
    Start-Logged 'mediamtx' $mediamtx "`"$yml`"" | Out-Null
    Wait-Port $RtspPort 15 'MediaMTX' (Join-Path $logs 'mediamtx.log')

    # ---- Live publishers: one encode each, every channel path proxies them. ----
    $clock = "drawtext=fontfile=${font}:fontsize={0}:fontcolor=white:box=1:boxcolor=black@0.55:boxborderw={1}:x={1}:y={1}:text='{2} %{{localtime\:%Y-%m-%d %T}}'"
    $mainVf = [string]::Format($clock, 40, 12, 'MAIN')
    $subVf = [string]::Format($clock, 22, 6, 'SUB')
    Start-Logged 'publisher-main' $ffmpeg ("-hide_banner -nostdin -loglevel warning -re -f lavfi -i testsrc2=size=1280x720:rate=15 -vf `"$mainVf`" -pix_fmt yuv420p " +
        "-c:v libx265 -preset ultrafast -tune zerolatency -b:v 1000k -x265-params keyint=30:min-keyint=30:bframes=0:repeat-headers=1:log-level=error " +
        "-f rtsp -rtsp_transport tcp $src/live/main") | Out-Null
    Start-Logged 'publisher-sub' $ffmpeg ("-hide_banner -nostdin -loglevel warning -re -f lavfi -i testsrc2=size=640x360:rate=15 -vf `"$subVf`" -pix_fmt yuv420p " +
        "-c:v libx264 -preset ultrafast -tune zerolatency -profile:v main -b:v 384k -g 30 -keyint_min 30 -bf 0 " +
        "-f rtsp -rtsp_transport tcp $src/live/sub") | Out-Null

    # ---- MockNvr ----
    $mockBin = Join-Path $mockDir 'bin\MockNvr'
    'Building MockNvr...'
    # No build servers: they outlive this script and would keep a piped caller's output open.
    $buildLog = & dotnet publish (Join-Path $repo 'tools\MockNvr\MockNvr.csproj') -c Release -o $mockBin --nologo -v q --disable-build-servers 2>&1
    if ($LASTEXITCODE) { throw "MockNvr build failed:`n$($buildLog -join "`n")" }
    # The password goes through the environment: never on a command line (cmd would also expand % in it).
    $mockArgs = "--bind $bind --port $HttpPort --time-mode $TimeMode --download $Download " +
        "--download-codec $DownloadCodec --download-mbps $DownloadMbps --rtsp-port $RtspPort --data `"$data`""
    $env:MOCKNVR_PASSWORD = $Password
    try { Start-Logged 'mocknvr' (Join-Path $mockBin 'MockNvr.exe') $mockArgs | Out-Null }
    finally { Remove-Item Env:MOCKNVR_PASSWORD -ErrorAction SilentlyContinue }
    Wait-Port $HttpPort 20 'MockNvr' (Join-Path $logs 'mocknvr.log')

    # The publishers need a moment to connect; a dead one means a bad ffmpeg build or filter.
    Start-Sleep -Seconds 2
    foreach ($s in $started | Where-Object { $_.name -like 'publisher-*' }) {
        if (-not (Get-Process -Id $s.pid -ErrorAction SilentlyContinue)) {
            throw "$($s.name) stopped: $((Get-Content (Join-Path $logs "$($s.name).err.log") -Tail 10) -join "`n")"
        }
    }
} catch {
    Write-Warning 'Start failed, stopping what was started.'
    & (Join-Path $mockDir 'stop-mock.ps1')
    throw
}

$hostShown = if ($Lan) { 'địa chỉ IP của máy này' } else { '127.0.0.1' }
''
'Đầu ghi giả đang chạy. Nhập vào Camera Viewer (Cài đặt > Đầu ghi):'
"  Địa chỉ IP : $hostShown"
"  Cổng HTTP  : $HttpPort"
"  Cổng RTSP  : $RtspPort"
'  Tài khoản  : admin'
"  Mật khẩu   : $Password"
"Chế độ: thời gian $TimeMode, tải video $Download ($DownloadCodec)$(if ($DownloadMbps -gt 0) { ", $DownloadMbps Mbit/s" })"
"Nhật ký   : $logs"
"Dừng      : powershell -ExecutionPolicy Bypass -File tools\mock\stop-mock.ps1"
