# Regenerates the tiny committed test clips with the GPL ffmpeg fetched by build\fetch-dev-tools.ps1.
# 5 s, 320x180, 15 fps, a keyframe every second, no B-frames, an access unit delimiter before every frame (the PS
# test helper splits frames on it). The Hikvision-like PS files are written at test time by HikPsWriter.
#   powershell -ExecutionPolicy Bypass -File tests\Cv.Media.Tests\Assets\make-assets.ps1
$ErrorActionPreference = 'Stop'
$ff = Join-Path $env:LOCALAPPDATA 'CameraViewer-dev\mock\ffmpeg\bin\ffmpeg.exe'
if (-not (Test-Path $ff)) { throw "GPL ffmpeg missing: $ff (run build\fetch-dev-tools.ps1)" }
Set-Location $PSScriptRoot

function Run([string[]] $a) {
    & $ff -hide_banner -loglevel error -y @a
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed: $a" }
}

$src = @('-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=15:duration=5', '-pix_fmt', 'yuv420p')
Run ($src + @('-c:v', 'libx264', '-preset', 'veryfast', '-crf', '30', '-g', '15', '-keyint_min', '15', '-sc_threshold', '0',
    '-bf', '0', '-x264-params', 'aud=1', '-f', 'h264', 'h264_320x180_15fps.h264'))
Run ($src + @('-c:v', 'libx265', '-preset', 'fast', '-crf', '32',
    '-x265-params', 'keyint=15:min-keyint=15:scenecut=0:bframes=0:open-gop=0:aud=1:repeat-headers=1:log-level=error',
    '-f', 'hevc', 'h265_320x180_15fps.h265'))
Run @('-framerate', '15', '-i', 'h264_320x180_15fps.h264', '-c', 'copy', '-movflags', '+faststart', 'h264.mp4')
Run @('-framerate', '15', '-i', 'h265_320x180_15fps.h265', '-c', 'copy', '-tag:v', 'hvc1', '-movflags', '+faststart', 'h265.mp4')
Run @('-framerate', '15', '-i', 'h264_320x180_15fps.h264', '-f', 'lavfi', '-i', 'sine=frequency=1000:sample_rate=16000:duration=5',
    '-map', '0:v', '-map', '1:a', '-c:v', 'copy', '-c:a', 'aac', '-b:a', '24k', '-f', 'mpegts', 'h264_aac.ts')

Get-ChildItem *.h264, *.h265, *.mp4, *.ts | ForEach-Object { '{0,-28} {1,8:N0} bytes' -f $_.Name, $_.Length }
