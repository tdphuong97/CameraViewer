# Publishes the version built in dist\ as a GitHub release; every copy from 0.9.0 on offers it as an update.
#   .\build.ps1 -Publish      build, then publish
#   .\build\publish.ps1       publish what is already built (for example after a failed upload)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
$version = (Select-Xml -Path src\Cv.App\Cv.App.csproj -XPath '//Version').Node.InnerText
$setup = "dist\CameraViewer-Setup-$version.exe"
if (-not (Test-Path $setup)) { throw "$setup is missing: run .\build.ps1 first." }
$repo = [regex]::Match((Get-Content src\Cv.Core\Update\Updater.cs -Raw), 'const string Repo = "([^"]+)"').Groups[1].Value
if (-not $repo -or $repo -like 'OWNER/*') { throw 'Set Updater.Repo in src\Cv.Core\Update\Updater.cs to the GitHub repository first.' }
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = "$env:ProgramFiles\GitHub CLI\gh.exe" }
if (-not (Test-Path $gh)) { throw 'GitHub CLI is missing: winget install GitHub.cli, then gh auth login' }

# The notes are this version's section of CHANGELOG.md, one change per line.
$section = [regex]::Match((Get-Content CHANGELOG.md -Raw -Encoding utf8), '(?ms)^## ' + [regex]::Escape($version) + '\s*$(.*?)(?=^## |\z)').Groups[1].Value
$notes = (($section -split "`r?`n") | Where-Object { $_.Trim() }) -join "`n"
if (-not $notes) { throw "CHANGELOG.md has no section for $version." }

# latest.json: Updater.CheckAsync reads version, notes, page and windows { url, size, sha256 }.
$latest = [ordered]@{
    version = $version
    date    = (Get-Date -Format 'yyyy-MM-dd')
    notes   = $notes
    page    = "https://github.com/$repo/releases/tag/v$version"
    windows = [ordered]@{
        url    = "https://github.com/$repo/releases/download/v$version/$(Split-Path $setup -Leaf)"
        size   = (Get-Item $setup).Length
        sha256 = (Get-FileHash $setup -Algorithm SHA256).Hash
    }
}
[IO.File]::WriteAllText((Join-Path (Get-Location) 'dist\latest.json'), ($latest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))

$body = @"
## Có gì mới
$notes

## Cài đặt
Lần đầu: tải **CameraViewer-Setup-$version.exe** ở mục Assets bên dưới, chạy rồi làm theo hướng dẫn trong README.
Windows có thể hiện cảnh báo xanh "Windows đã bảo vệ máy tính của bạn" vì bộ cài chưa có chữ ký số: bấm **Thông tin thêm**, rồi **Vẫn chạy**.
Đã cài bản 0.9.0 trở lên: không cần tải, phần mềm tự báo bản mới, bấm **Cập nhật** là xong.

## Kiểm tra file
Bản chính thức chỉ có ở trang này. SHA-256 của CameraViewer-Setup-$version.exe:
``$($latest.windows.sha256)``
"@
& $gh release create "v$version" $setup 'dist\latest.json' --repo $repo --title "Camera Viewer $version" --notes $body
if ($LASTEXITCODE) { throw 'Publishing the release failed.' }
"Published https://github.com/$repo/releases/tag/v$version"
