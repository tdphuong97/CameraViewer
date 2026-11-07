# Builds everything: tests, the app (self-contained single file + FFmpeg DLLs), and the installer.
#   .\build.ps1                 full build, installer in dist\
#   .\build.ps1 -SkipTests      skip the tests
#   .\build.ps1 -SkipInstaller  stop after dist\app
#   .\build.ps1 -Publish        also publish a GitHub release; every copy from 0.9.0 on offers it as an update
param([switch]$SkipTests, [switch]$SkipInstaller, [switch]$Publish)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    dotnet test CameraViewer.sln -c Release
    if ($LASTEXITCODE) { throw 'Tests failed.' }
    # The mock recorder's own tests live outside the solution (dev tool, never shipped).
    if (Test-Path tools\MockNvr.Tests) {
        dotnet test tools\MockNvr.Tests -c Release
        if ($LASTEXITCODE) { throw 'Mock recorder tests failed.' }
    }
}

# The app: self-contained single file. No compression: it would unpack into RAM at every start.
$app = Join-Path $PSScriptRoot 'dist\app'
Remove-Item -Recurse -Force $app -ErrorAction SilentlyContinue
dotnet publish src\Cv.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $app
if ($LASTEXITCODE) { throw 'Publish failed.' }
Get-ChildItem $app -Recurse -Include *.pdb, *.xml | Remove-Item

# FFmpeg DLLs must sit in dist\app\ffmpeg\ (Cv.Media copies them from FFmpegBinDir; check, never assume).
$ffDest = Join-Path $app 'ffmpeg'
$ffSrc = Join-Path $env:LOCALAPPDATA 'CameraViewer-dev\ffmpeg'
$required = 'avcodec-*.dll', 'avformat-*.dll', 'avutil-*.dll', 'swscale-*.dll'
function Get-MissingFFmpeg { $required | Where-Object { -not (Test-Path (Join-Path $ffDest $_)) } }
if (Get-MissingFFmpeg) {
    if (Test-Path (Join-Path $ffSrc 'bin\avcodec-*.dll')) {
        Write-Warning "FFmpeg DLLs did not land in $ffDest after publish; copying them from $ffSrc\bin."
        New-Item -ItemType Directory -Force $ffDest | Out-Null
        Copy-Item (Join-Path $ffSrc 'bin\*.dll') $ffDest -Force
    }
}
$missing = @(Get-MissingFFmpeg)
if ($missing) {
    $msg = "FFmpeg DLLs missing in $ffDest ($($missing -join ', ')): run  powershell -ExecutionPolicy Bypass -File build\fetch-dev-tools.ps1  once on this machine."
    if ($SkipInstaller) { Write-Warning $msg } else { throw "$msg An installer without FFmpeg cannot play video." }
}
else {
    # LGPL: ship the license text with the DLLs.
    Get-ChildItem $ffSrc -Filter 'LICENSE*' -File -ErrorAction SilentlyContinue | Copy-Item -Destination $ffDest -Force
}
Copy-Item THIRD-PARTY-NOTICES.md, LICENSE $app -Force

"App: {0:N1} MB in {1}" -f ((Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), $app
if (Test-Path $ffDest) {
    Get-ChildItem $ffDest -File | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
}

if ($SkipInstaller) { return }
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is missing: winget install JRSoftware.InnoSetup' }
$version = (Select-Xml -Path src\Cv.App\Cv.App.csproj -XPath '//Version').Node.InnerText
$env:CV_VERSION = $version
& $iscc /Q build\CameraViewer.iss
if ($LASTEXITCODE) { throw 'Installer build failed.' }
Get-ChildItem "dist\CameraViewer-Setup-$version.exe" |
    Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize

if ($Publish) { & .\build\publish.ps1 }
