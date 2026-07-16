$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$project = Join-Path $root 'src\DeviceDebugStudio.App\DeviceDebugStudio.App.csproj'
$releaseRoot = Join-Path $root 'ReleasePackages'
$publishDir = Join-Path $releaseRoot '嵌入式调试台-win-x64'
$zipPath = Join-Path $releaseRoot '嵌入式调试台-win-x64.zip'

if (-not (Test-Path -LiteralPath $dotnet)) {
    throw '未找到项目内 .NET 10 SDK。'
}

Get-Process DeviceDebugStudio -ErrorAction SilentlyContinue | Stop-Process -Force

& $dotnet test (Join-Path $root 'DeviceDebugStudio.sln') -c Release
if ($LASTEXITCODE -ne 0) {
    throw '自动化测试失败，已停止发布。'
}

$resolvedRoot = [IO.Path]::GetFullPath($releaseRoot)
$resolvedPublish = [IO.Path]::GetFullPath($publishDir)
if (-not $resolvedPublish.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw '发布目录不在 ReleasePackages 内。'
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

& $dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir /p:PublishSingleFile=false /p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) {
    throw '发布失败。'
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "发布完成：$publishDir"
Write-Host "压缩包：$zipPath"
