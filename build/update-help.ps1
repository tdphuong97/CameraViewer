# Rebuilds the user guide (docs\, GitBook format) into one offline HTML file and puts it where the app ships it
# (src\Cv.App\Help\huong-dan.html, opened by the guide button and F1). Run after editing anything in docs\.
#   powershell -ExecutionPolicy Bypass -File build\update-help.ps1 [-Guidebook <path to guidebook.py>]
param([string]$Guidebook = (Join-Path $env:USERPROFILE '.claude\skills\guidebook\scripts\guidebook.py'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
if (-not (Test-Path $Guidebook)) { throw "guidebook.py not found at $Guidebook (pass -Guidebook)." }

python $Guidebook check (Join-Path $repo 'docs')
python $Guidebook build (Join-Path $repo 'docs')
if ($LASTEXITCODE) { throw 'Building the guide failed.' }

$dest = Join-Path $repo 'src\Cv.App\Help'
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $repo 'docs\_site\index.html') (Join-Path $dest 'huong-dan.html') -Force
"Guide copied to $dest\huong-dan.html ({0:N1} MB)" -f ((Get-Item (Join-Path $dest 'huong-dan.html')).Length / 1MB)
