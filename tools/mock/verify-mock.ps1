# Checks a running mock (tools\mock\start-mock.ps1) the way the app uses a real recorder: curl --digest on every
# ISAPI call, ISAPI download by GET-with-body and by POST, ffprobe (the LGPL one the app ships) on the downloaded PS,
# on live sub/main and on a playback URL with the password percent-encoded. Read-only; prints PASS/FAIL per check.
#
#   powershell -ExecutionPolicy Bypass -File tools\mock\verify-mock.ps1
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$data = Join-Path $PSScriptRoot 'data'
$stateFile = Join-Path $data 'mock-state.json'
if (-not (Test-Path $stateFile)) { throw 'The mock is not running: start tools\mock\start-mock.ps1 first.' }
$s = Get-Content $stateFile -Raw | ConvertFrom-Json
$ffprobe = Join-Path $env:LOCALAPPDATA 'CameraViewer-dev\ffmpeg\bin\ffprobe.exe'
if (-not (Test-Path $ffprobe)) { throw "Missing $ffprobe (build\fetch-dev-tools.ps1)." }
$curl = Join-Path $env:WINDIR 'System32\curl.exe'
$base = "http://127.0.0.1:$($s.httpPort)"
$auth = "$($s.user):$($s.password)"
$enc = [Uri]::EscapeDataString($s.password)
$rtsp = "rtsp://$($s.user):$enc@127.0.0.1:$($s.rtspPort)"
$work = Join-Path $data 'verify'
New-Item -ItemType Directory -Force $work | Out-Null
$script:fail = 0

function Report([bool]$ok, [string]$name, [string]$detail) {
    if (-not $ok) { $script:fail++ }
    '{0} {1,-46} {2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail
}

function Isapi([string]$method, [string]$path, [string]$bodyFile, [string]$outFile) {
    $a = @('-s', '--digest', '-u', $auth, '-X', $method, '-o', $outFile, '-w', '%{http_code}')
    if ($bodyFile) { $a += @('-H', 'Content-Type: application/xml', '--data-binary', "@$bodyFile") }
    $code = & $curl @a "$base$path"
    return [int]$code
}

function Probe([string]$target, [string[]]$extra) {
    $out = & $ffprobe -v error @extra -show_entries 'stream=codec_type,codec_name,width,height,avg_frame_rate:format=format_name,duration' -of compact=p=0 $target 2>&1
    return ($out | Out-String).Trim() -replace "`r?`n", ' | '
}

# ---- ISAPI GETs ----
$checks = @(
    @{ Path = '/ISAPI/System/deviceInfo'; Want = '<model>DS-7732NI-M4</model>' },
    @{ Path = '/ISAPI/System/time'; Want = '<localTime>' },
    @{ Path = '/ISAPI/ContentMgmt/InputProxy/channels'; Want = '<InputProxyChannel ' },
    @{ Path = '/ISAPI/ContentMgmt/InputProxy/channels/status'; Want = '<online>false</online>' },
    @{ Path = '/ISAPI/Streaming/channels'; Want = '<id>1702</id>' },
    @{ Path = '/ISAPI/Security/adminAccesses'; Want = "<portNo>$($s.rtspPort)</portNo>" }
)
foreach ($c in $checks) {
    $f = Join-Path $work 'get.xml'
    $code = Isapi 'GET' $c.Path $null $f
    $text = [IO.File]::ReadAllText($f)
    Report ($code -eq 200 -and $text.Contains($c.Want)) "GET $($c.Path)" "HTTP $code, $($text.Length) chars"
}

# ---- Wrong password: 401 with the failed-login counters (one attempt; the next good login resets them) ----
$code = & $curl -s --digest -u "$($s.user):not-the-password" -o (Join-Path $work 'bad.xml') -w '%{http_code}' "$base/ISAPI/System/deviceInfo"
$bad = [IO.File]::ReadAllText((Join-Path $work 'bad.xml'))
Report ($code -eq '401' -and $bad.Contains('<lockStatus>unlock</lockStatus>') -and $bad.Contains('<retryLoginTime>4</retryLoginTime>')) 'wrong password -> 401 userCheck' "HTTP $code"
Isapi 'GET' '/ISAPI/System/deviceInfo' $null (Join-Path $work 'get.xml') | Out-Null

# ---- Search: yesterday, paged by 10 ----
$day = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd')
$segments = @()
$pos = 0
do {
    $req = Join-Path $work 'search.xml'
    [IO.File]::WriteAllText($req, @"
<?xml version="1.0" encoding="utf-8"?>
<CMSearchDescription><searchID>C9A2C7B1-6E2D-4F1A-9B0E-3D7C2A1F5E44</searchID>
<trackList><trackID>101</trackID></trackList>
<timeSpanList><timeSpan><startTime>${day}T00:00:00Z</startTime><endTime>${day}T23:59:59Z</endTime></timeSpan></timeSpanList>
<maxResults>10</maxResults><searchResultPostion>$pos</searchResultPostion>
<metadataList><metadataDescriptor>//recordType.meta.std-cgi.com</metadataDescriptor></metadataList></CMSearchDescription>
"@)
    $out = Join-Path $work 'result.xml'
    $code = Isapi 'POST' '/ISAPI/ContentMgmt/search' $req $out
    [xml]$x = [IO.File]::ReadAllText($out)
    $r = $x.DocumentElement
    $page = @($r.GetElementsByTagName('playbackURI') | ForEach-Object { $_.InnerText })
    $segments += $page
    $pos += [int]$r.numOfMatches
    $status = $r.responseStatusStrg
} while ($status -eq 'MORE' -and $code -eq 200)
Report ($code -eq 200 -and $segments.Count -gt 10 -and $status -eq 'OK') 'POST search (paged by 10)' "$($segments.Count) segments, last page $status"

# ---- Download: a 10 s range inside a long segment of yesterday ----
$seg = $segments | Where-Object {
    $m = [regex]::Match($_, 'starttime=(\d{8}T\d{6})Z&endtime=(\d{8}T\d{6})Z')
    $m.Success -and ([datetime]::ParseExact($m.Groups[2].Value, 'yyyyMMddTHHmmss', $null) - [datetime]::ParseExact($m.Groups[1].Value, 'yyyyMMddTHHmmss', $null)).TotalMinutes -ge 40
} | Select-Object -First 1
$m = [regex]::Match($seg, 'starttime=(\d{8}T\d{6})Z')
$t0 = [datetime]::ParseExact($m.Groups[1].Value, 'yyyyMMddTHHmmss', $null).AddMinutes(10)
$st = $t0.ToString('yyyyMMddTHHmmss') + 'Z'
$et = $t0.AddSeconds(10).ToString('yyyyMMddTHHmmss') + 'Z'
$timeUri = "rtsp://127.0.0.1/Streaming/tracks/101/?starttime=$st&amp;endtime=$et"
$rangeUri = ([regex]::Replace([regex]::Replace($seg, 'starttime=[^&]+', "starttime=$st"), 'endtime=[^&]+', "endtime=$et")) -replace '&', '&amp;'
$wantCodec = if ($s.downloadCodec -eq 'h264') { 'h264' } else { 'hevc' }
foreach ($d in @(
        @{ Name = 'download by time, GET with body'; Verb = 'GET'; Uri = $timeUri },
        @{ Name = 'download by time, POST'; Verb = 'POST'; Uri = $timeUri },
        @{ Name = 'download by segment range, GET'; Verb = 'GET'; Uri = $rangeUri })) {
    $req = Join-Path $work 'download.xml'
    [IO.File]::WriteAllText($req, "<?xml version=`"1.0`" encoding=`"UTF-8`"?>`n<downloadRequest version=`"1.0`" xmlns=`"http://www.isapi.org/ver20/XMLSchema`">`n<playbackURI>$($d.Uri)</playbackURI>`n</downloadRequest>")
    $ps = Join-Path $work "$($d.Verb)-$($d.Name.Split(' ')[2]).ps"
    $code = Isapi $d.Verb '/ISAPI/ContentMgmt/download' $req $ps
    $head = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($ps), 0, [Math]::Min(4, (Get-Item $ps).Length))
    if ($s.download -ne 'honor') { Report ($head -ne 'IMKH') "$($d.Name) ($($s.download) mode)" "HTTP $code, starts with '$head'"; continue }
    $info = Probe $ps @()
    $dur = [regex]::Match($info, 'duration=([\d.]+)').Groups[1].Value
    $ok = $code -eq 200 -and $head -eq 'IMKH' -and $info -match "codec_name=$wantCodec" -and $info -match 'format_name=mpeg' -and [double]$dur -ge 9 -and [double]$dur -le 11
    Report $ok $d.Name "HTTP $code, $([Math]::Round((Get-Item $ps).Length / 1KB)) KB, $info"
}

# ---- RTSP: live sub, live main, playback; password percent-encoded in the URL ----
$pb = (Get-Date).AddMinutes(-30).ToString('yyyyMMddTHHmmss') + 'Z'
foreach ($r in @(
        @{ Name = 'RTSP live sub (ch 17)'; Url = "$rtsp/Streaming/Channels/1702"; Want = 'codec_name=h264|width=640' },
        @{ Name = 'RTSP live main (ch 1)'; Url = "$rtsp/Streaming/Channels/101"; Want = 'codec_name=hevc|width=1280' },
        @{ Name = 'RTSP playback (ch 3)'; Url = "$rtsp/Streaming/tracks/301?starttime=$pb"; Want = 'codec_name=hevc|width=1280' })) {
    $info = Probe $r.Url @('-rtsp_transport', 'tcp', '-rw_timeout', '15000000')
    $ok = ($r.Want.Split('|') | Where-Object { $info -notmatch [regex]::Escape($_) }).Count -eq 0
    Report $ok $r.Name ($info -replace [regex]::Escape($enc), '***')
}
$info = Probe "$rtsp/Streaming/Channels/501" @('-rtsp_transport', 'tcp', '-rw_timeout', '5000000')
Report ($info -notmatch 'codec_name') 'RTSP offline channel 5 refused' (($info -replace [regex]::Escape($enc), '***') -replace '\s+', ' ')

Remove-Item -Recurse -Force $work
''
if ($script:fail) { "$($script:fail) check(s) FAILED"; exit 1 } else { 'All checks passed.' }
