# One-time developer setup: downloads the third-party tools the build and the test recorder need, into
# %LOCALAPPDATA%\CameraViewer-dev (shared by every checkout of the repo; nothing lands in git).
#
#   powershell -ExecutionPolicy Bypass -File build\fetch-dev-tools.ps1 [-Force]
#
#   ffmpeg\bin     FFmpeg 9.0 LGPL shared (BtbN): the DLLs the app ships with, plus ffmpeg/ffprobe for checks
#   mock\ffmpeg    FFmpeg 9.0 GPL static (BtbN): only to encode the test recorder's H.264/H.265 streams, never shipped
#   mock\mediamtx  MediaMTX (MIT): the test recorder's RTSP server
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$dev = Join-Path $env:LOCALAPPDATA 'CameraViewer-dev'
$cache = Join-Path $dev 'cache'
New-Item -ItemType Directory -Force $cache | Out-Null

$items = @(
    @{ Name = 'FFmpeg LGPL shared 9.0'; Dest = 'ffmpeg'
       Url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip' },
    @{ Name = 'FFmpeg GPL 9.0 (test recorder only)'; Dest = 'mock\ffmpeg'
       Url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-9.0.zip' },
    @{ Name = 'MediaMTX 1.21.3'; Dest = 'mock\mediamtx'
       Url = 'https://github.com/bluenviron/mediamtx/releases/download/v1.21.3/mediamtx_v1.21.3_windows_amd64.zip' }
)

foreach ($it in $items) {
    $dest = Join-Path $dev $it.Dest
    if ((Test-Path $dest) -and -not $Force) { "OK   $($it.Name) already in $dest"; continue }
    $zip = Join-Path $cache (Split-Path $it.Url -Leaf)
    if (-not (Test-Path $zip) -or $Force) {
        "GET  $($it.Url)"
        Invoke-WebRequest $it.Url -OutFile "$zip.part" -UseBasicParsing
        Move-Item "$zip.part" $zip -Force
    }
    $tmp = Join-Path $cache 'extract'
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    Expand-Archive $zip $tmp
    Remove-Item -Recurse -Force $dest -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $dest | Out-Null
    # BtbN zips hold one top folder with bin\, mediamtx zips hold the files directly.
    $bin = Get-ChildItem $tmp -Recurse -Directory -Filter bin | Select-Object -First 1
    if ($bin) {
        New-Item -ItemType Directory -Force (Join-Path $dest 'bin') | Out-Null
        Copy-Item (Join-Path $bin.FullName '*') (Join-Path $dest 'bin') -Recurse
        Get-ChildItem $tmp -Recurse -Filter 'LICENSE*' | Select-Object -First 1 | Copy-Item -Destination $dest
    } else {
        Copy-Item (Join-Path $tmp '*') $dest -Recurse
    }
    Remove-Item -Recurse -Force $tmp
    "OK   $($it.Name) -> $dest  (sha256 of zip: $((Get-FileHash $zip -Algorithm SHA256).Hash))"
}

""
"FFmpeg DLLs:"
Get-ChildItem (Join-Path $dev 'ffmpeg\bin\*.dll') | ForEach-Object { "  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB) }
"Done. Builds pick the DLLs up from $dev\ffmpeg\bin."
