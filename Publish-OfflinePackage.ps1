$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$solution = Join-Path $root 'DeviceDebugStudio.sln'
$project = Join-Path $root 'src\DeviceDebugStudio.App\DeviceDebugStudio.App.csproj'
$releaseRoot = Join-Path $root 'ReleasePackages'
$appDisplayName = -join ([char[]](0x5D4C, 0x5165, 0x5F0F, 0x8C03, 0x8BD5, 0x53F0))
$packageName = $appDisplayName + '-win-x64'
$publishDir = Join-Path $releaseRoot $packageName
$zipPath = Join-Path $releaseRoot ($packageName + '.zip')
$checksumPath = Join-Path $releaseRoot ($packageName + '.zip.sha256')

if (-not (Test-Path -LiteralPath $dotnet)) {
    throw 'The .NET SDK was not found.'
}

Get-Process DeviceDebugStudio -ErrorAction SilentlyContinue | Stop-Process -Force

& $dotnet test $solution -c Release
if ($LASTEXITCODE -ne 0) {
    throw 'Automated tests failed. Publishing was stopped.'
}

$resolvedRoot = [IO.Path]::GetFullPath($releaseRoot)
$resolvedPublish = [IO.Path]::GetFullPath($publishDir)
if (-not $resolvedPublish.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The publish directory is outside ReleasePackages.'
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

& $dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir /p:PublishSingleFile=false /p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed.'
}

$publishedExe = Join-Path $publishDir 'DeviceDebugStudio.exe'
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw 'The published executable was not generated.'
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$checksum = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksumPath -Value $checksum -Encoding ascii

Write-Host "Publish directory: $publishDir"
Write-Host "Package: $zipPath"
Write-Host "SHA-256 file: $checksumPath"
