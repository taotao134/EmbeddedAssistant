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

$runningProcesses = @(Get-Process DeviceDebugStudio -ErrorAction SilentlyContinue)
if ($runningProcesses.Count -gt 0) {
    try {
        $runningProcesses | Stop-Process -Force -ErrorAction Stop
        Start-Sleep -Milliseconds 500
    }
    catch {
        throw 'DeviceDebugStudio is running and cannot be closed with the current permissions. Close it manually, then run this script again.'
    }
}

& $dotnet test $solution -c Release
if ($LASTEXITCODE -ne 0) {
    throw 'Automated tests failed. Publishing was stopped.'
}

$resolvedRoot = [IO.Path]::GetFullPath($releaseRoot)
$resolvedPublish = [IO.Path]::GetFullPath($publishDir)
if (-not $resolvedPublish.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The publish directory is outside ReleasePackages.'
}

$profilesBackup = Join-Path ([IO.Path]::GetTempPath()) ('DeviceDebugStudio-Profiles-' + [Guid]::NewGuid().ToString('N'))
$profilesPath = Join-Path $publishDir 'Profiles'
$profilesBackedUp = $false

try {
    if (Test-Path -LiteralPath $profilesPath) {
        Move-Item -LiteralPath $profilesPath -Destination $profilesBackup
        $profilesBackedUp = $true
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

    if ($profilesBackedUp) {
        Move-Item -LiteralPath $profilesBackup -Destination $profilesPath
        $profilesBackedUp = $false
    }

    $zipStaging = Join-Path ([IO.Path]::GetTempPath()) ('DeviceDebugStudio-Package-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $zipStaging -Force | Out-Null
        Get-ChildItem -LiteralPath $publishDir -Force |
            Where-Object { $_.Name -ne 'Profiles' } |
            Copy-Item -Destination $zipStaging -Recurse -Force

        if (Test-Path -LiteralPath $zipPath) {
            Remove-Item -LiteralPath $zipPath -Force
        }
        Compress-Archive -Path (Join-Path $zipStaging '*') -DestinationPath $zipPath -CompressionLevel Optimal
    }
    finally {
        if (Test-Path -LiteralPath $zipStaging) {
            Remove-Item -LiteralPath $zipStaging -Recurse -Force
        }
    }

    $checksum = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $checksumPath -Value $checksum -Encoding ascii
}
catch {
    if ($profilesBackedUp -and (Test-Path -LiteralPath $profilesBackup)) {
        if (Test-Path -LiteralPath $profilesPath) {
            Remove-Item -LiteralPath $profilesPath -Recurse -Force
        }
        New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
        Move-Item -LiteralPath $profilesBackup -Destination $profilesPath
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $profilesBackup) {
        Remove-Item -LiteralPath $profilesBackup -Recurse -Force
    }
}

Write-Host "Publish directory: $publishDir"
Write-Host "Package: $zipPath"
Write-Host "SHA-256 file: $checksumPath"
