# 配布用の zip を作る。
#   powershell -ExecutionPolicy Bypass -File tools\publish.ps1
# 出力: artifacts\BveTsStructureEditor-<バージョン>.zip
# 利用者側に .NET 10 デスクトップ ランタイムが要る形（framework-dependent）で作る。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\BveTsStructureEditor.App\BveTsStructureEditor.App.csproj'

# バージョンは csproj の <Version> から取る
$version = ([xml](Get-Content $proj -Raw -Encoding UTF8)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'csproj に <Version> がありません' }

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }

$artifacts = Join-Path $root 'artifacts'
$name = "BveTsStructureEditor-$version"
$out = Join-Path $artifacts $name
$zip = Join-Path $artifacts "$name.zip"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

# テストが通らないものは配らない
& $dotnet test (Join-Path $root 'BveTsStructureEditor.sln') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'テストが失敗したので中止しました' }

& $dotnet publish $proj -c Release -r win-x64 --self-contained false -p:DebugType=none -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw 'publish に失敗しました' }

# 英語以外のリソース（ライブラリの各国語メッセージ）は要らないので消す
Get-ChildItem $out -Directory | Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z]+)?$' -and $_.Name -ne 'ja' } |
    Remove-Item -Recurse -Force

Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Write-Host ""
Write-Host "作成しました: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
