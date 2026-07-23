using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceDebugStudio.Infrastructure.Persistence;

namespace DeviceDebugStudio.Infrastructure.Updates;

public sealed record UpdateManifest
{
    public string Version { get; init; } = string.Empty;
    public string PackageUrl { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long? PackageSize { get; init; }
    public string ReleaseNotes { get; init; } = string.Empty;
    public bool Mandatory { get; init; }
}

public sealed record UpdateCheckResult(
    Version CurrentVersion,
    Version LatestVersion,
    UpdateManifest Manifest)
{
    public bool IsUpdateAvailable => LatestVersion > CurrentVersion;
}

public enum UpdateProgressPhase
{
    Downloading,
    Verifying,
    PreparingToRestart
}

public sealed record UpdateProgressInfo(
    UpdateProgressPhase Phase,
    double Progress,
    long BytesDownloaded,
    long? TotalBytes);

public sealed class OnlineUpdateService : IDisposable
{
    public const string ApplicationMutexName = @"Local\DeviceDebugStudio.Instance";

    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private const string InstallerFailureLogFileName = "last-install-error.log";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly string InstallerScript = """
param(
    [int]$ParentPid,
    [string]$PackagePath,
    [string]$InstallDirectory,
    [string]$ExecutablePath,
    [string]$ScriptPath,
    [string]$ErrorLogPath,
    [string]$InstanceMutexName,
    [string]$FailureLogPath
)

$ErrorActionPreference = 'Stop'
$installerMutex = $null
$ownsInstallerMutex = $false

function Wait-ForProcessExit {
    param(
        [int]$ProcessId,
        [int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Timed out waiting for the application process to exit."
        }

        Start-Sleep -Milliseconds 200
    }
}

function Wait-ForTargetProcesses {
    param(
        [string]$TargetExecutable,
        [int]$TimeoutSeconds
    )

    $expectedPath = [IO.Path]::GetFullPath($TargetExecutable)
    $processName = [IO.Path]::GetFileNameWithoutExtension($TargetExecutable)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $matchingProcesses = @(Get-Process -Name $processName -ErrorAction SilentlyContinue | Where-Object {
            try {
                $_.Path -and [string]::Equals(
                    [IO.Path]::GetFullPath($_.Path),
                    $expectedPath,
                    [StringComparison]::OrdinalIgnoreCase)
            }
            catch {
                $false
            }
        })
        if ($matchingProcesses.Count -eq 0) {
            return
        }

        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Timed out waiting for other application instances to exit."
        }

        Start-Sleep -Milliseconds 200
    }
}

function Copy-StagedPackage {
    param(
        [string]$StagingDirectory,
        [string]$TargetDirectory,
        [int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        try {
            foreach ($item in Get-ChildItem -LiteralPath $StagingDirectory -Force) {
                Copy-Item -LiteralPath $item.FullName -Destination $TargetDirectory -Recurse -Force
            }

            return
        }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw
            }

            Start-Sleep -Milliseconds 500
        }
    }
}

try {
    Wait-ForProcessExit -ProcessId $ParentPid -TimeoutSeconds 90

    $installerMutex = [Threading.Mutex]::new($false, $InstanceMutexName)
    $mutexDeadline = [DateTime]::UtcNow.AddSeconds(90)
    while (-not $ownsInstallerMutex) {
        try {
            $ownsInstallerMutex = $installerMutex.WaitOne(200)
        }
        catch [Threading.AbandonedMutexException] {
            $ownsInstallerMutex = $true
        }

        if (-not $ownsInstallerMutex -and [DateTime]::UtcNow -ge $mutexDeadline) {
            throw "Timed out waiting for the application update lock."
        }
    }

    Wait-ForTargetProcesses -TargetExecutable $ExecutablePath -TimeoutSeconds 90

    $stagingDirectory = Join-Path ([IO.Path]::GetTempPath()) ("DeviceDebugStudio-update-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $stagingDirectory -Force

    Copy-StagedPackage -StagingDirectory $stagingDirectory -TargetDirectory $InstallDirectory -TimeoutSeconds 90

    Remove-Item -LiteralPath $PackagePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $ScriptPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $FailureLogPath -Force -ErrorAction SilentlyContinue

    if ($ownsInstallerMutex) {
        $installerMutex.ReleaseMutex()
        $ownsInstallerMutex = $false
    }
    $installerMutex.Dispose()
    $installerMutex = $null

    Start-Process -FilePath $ExecutablePath -WorkingDirectory $InstallDirectory
}
catch {
    $errorText = $_.Exception.ToString()
    try {
        [IO.File]::WriteAllText($ErrorLogPath, $errorText, [Text.Encoding]::UTF8)
    }
    catch {
    }
    try {
        $failureDirectory = Split-Path -Parent $FailureLogPath
        if (-not [string]::IsNullOrWhiteSpace($failureDirectory)) {
            New-Item -ItemType Directory -Path $failureDirectory -Force | Out-Null
        }
        [IO.File]::WriteAllText($FailureLogPath, $errorText, [Text.Encoding]::UTF8)
    }
    catch {
    }
}
finally {
    if ($ownsInstallerMutex -and $null -ne $installerMutex) {
        try {
            $installerMutex.ReleaseMutex()
        }
        catch {
        }
    }
    if ($null -ne $installerMutex) {
        $installerMutex.Dispose()
    }
}
""";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public OnlineUpdateService(HttpClient? httpClient = null, Version? currentVersion = null)
    {
        _httpClient = httpClient ?? CreateHttpClient();
        _ownsHttpClient = httpClient is null;
        CurrentVersion = NormalizeVersion(currentVersion ?? ReadApplicationVersion());
    }

    public Version CurrentVersion { get; }

    public async Task<UpdateCheckResult> CheckAsync(Uri manifestUri, CancellationToken cancellationToken = default)
    {
        ValidateHttpsUri(manifestUri, nameof(manifestUri));
        using HttpResponseMessage response = await _httpClient
            .GetAsync(manifestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        UpdateManifest manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("更新清单为空。 ");

        Version latestVersion = ValidateManifest(manifest);
        return new UpdateCheckResult(CurrentVersion, latestVersion, manifest);
    }

    public async Task<UpdateCheckResult> CheckGitHubAsync(
        string repository,
        CancellationToken cancellationToken = default)
    {
        Uri releaseUri = BuildGitHubReleaseUri(repository);
        using HttpRequestMessage request = new(HttpMethod.Get, releaseUri);
        request.Headers.UserAgent.ParseAdd($"DeviceDebugStudio/{CurrentVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        GitHubRelease release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub 发布信息为空。 ");
        if (release.Draft || release.Prerelease)
        {
            throw new InvalidDataException("GitHub 最新发布不是稳定版本。 ");
        }

        Version latestVersion = ParseReleaseVersion(release.TagName);
        GitHubAsset packageAsset = release.Assets
            .Where(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(asset => asset.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset => asset.Name.Contains("windows", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault()
            ?? throw new InvalidDataException("GitHub 发布中没有 ZIP 更新包。 ");
        string hash = await ResolveGitHubHashAsync(release.Assets, packageAsset, cancellationToken).ConfigureAwait(false);

        UpdateManifest manifest = new()
        {
            Version = latestVersion.ToString(3),
            PackageUrl = packageAsset.BrowserDownloadUrl,
            Sha256 = hash,
            PackageSize = packageAsset.Size > 0 ? packageAsset.Size : null,
            ReleaseNotes = release.Body ?? release.Name ?? string.Empty,
            Mandatory = false
        };
        ValidateManifest(manifest);
        return new UpdateCheckResult(CurrentVersion, latestVersion, manifest);
    }

    public async Task<string> DownloadAsync(
        UpdateManifest manifest,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<UpdateProgressInfo>? detailedProgress = null)
    {
        ValidateManifest(manifest);
        Uri packageUri = new(manifest.PackageUrl, UriKind.Absolute);
        string updateDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeviceDebugStudio",
            "Updates",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateDirectory);
        string packagePath = Path.Combine(updateDirectory, "update.zip");

        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? contentLength = response.Content.Headers.ContentLength;
            long? totalBytes = contentLength is > 0 ? contentLength : manifest.PackageSize;
            if (totalBytes is > MaximumPackageBytes)
            {
                throw new InvalidDataException("更新包超过 512 MB 限制。 ");
            }

            detailedProgress?.Report(new(
                UpdateProgressPhase.Downloading,
                0,
                0,
                totalBytes));

            await using Stream source = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using FileStream destination = new(
                packagePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaximumPackageBytes)
                {
                    throw new InvalidDataException("更新包超过 512 MB 限制。 ");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer.AsSpan(0, read));
                if (totalBytes is > 0)
                {
                    double fraction = Math.Clamp((double)total / totalBytes.Value, 0, 1);
                    progress?.Report(fraction);
                    detailedProgress?.Report(new(
                        UpdateProgressPhase.Downloading,
                        fraction,
                        total,
                        totalBytes));
                }
            }

            detailedProgress?.Report(new(
                UpdateProgressPhase.Verifying,
                1,
                total,
                totalBytes));

            if (manifest.PackageSize is long expectedSize && expectedSize != total)
            {
                throw new InvalidDataException("更新包大小与清单不一致。 ");
            }

            string actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新包 SHA-256 校验失败。 ");
            }

            progress?.Report(1);
            detailedProgress?.Report(new(
                UpdateProgressPhase.Verifying,
                1,
                total,
                totalBytes));
            return packagePath;
        }
        catch
        {
            TryDeleteDirectory(updateDirectory);
            throw;
        }
    }

    public void StartInstaller(
        string packagePath,
        string? installDirectory = null,
        string? executablePath = null)
    {
        string resolvedPackagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(resolvedPackagePath))
        {
            throw new FileNotFoundException("更新包不存在。 ", resolvedPackagePath);
        }

        string resolvedInstallDirectory = Path.GetFullPath(installDirectory ?? AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string resolvedExecutablePath = Path.GetFullPath(
            executablePath
            ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径。 "));
        if (!File.Exists(resolvedExecutablePath))
        {
            throw new FileNotFoundException("当前程序不存在。 ", resolvedExecutablePath);
        }

        string updateDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeviceDebugStudio",
            "Updates",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateDirectory);
        string scriptPath = Path.Combine(updateDirectory, "install.ps1");
        string errorLogPath = Path.Combine(updateDirectory, "install-error.log");
        string failureLogPath = GetInstallerFailureLogPath();
        Directory.CreateDirectory(Path.GetDirectoryName(failureLogPath)!);
        TryDeleteFile(failureLogPath);
        File.WriteAllText(scriptPath, InstallerScript, new UTF8Encoding(false));

        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = updateDirectory
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-ParentPid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("-PackagePath");
        startInfo.ArgumentList.Add(resolvedPackagePath);
        startInfo.ArgumentList.Add("-InstallDirectory");
        startInfo.ArgumentList.Add(resolvedInstallDirectory);
        startInfo.ArgumentList.Add("-ExecutablePath");
        startInfo.ArgumentList.Add(resolvedExecutablePath);
        startInfo.ArgumentList.Add("-ScriptPath");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-ErrorLogPath");
        startInfo.ArgumentList.Add(errorLogPath);
        startInfo.ArgumentList.Add("-InstanceMutexName");
        startInfo.ArgumentList.Add(ApplicationMutexName);
        startInfo.ArgumentList.Add("-FailureLogPath");
        startInfo.ArgumentList.Add(failureLogPath);

        if (Process.Start(startInfo) is null)
        {
            TryDeleteDirectory(updateDirectory);
            throw new InvalidOperationException("无法启动更新安装代理。 ");
        }
    }

    public static bool TryConsumeInstallerFailure(out string message)
    {
        message = string.Empty;
        string failureLogPath = GetInstallerFailureLogPath();
        try
        {
            if (!File.Exists(failureLogPath))
            {
                return false;
            }

            message = File.ReadAllText(failureLogPath).Trim();
            TryDeleteFile(failureLogPath);
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            if (message.Length > 2000)
            {
                message = message[..2000] + "...";
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static HttpClient CreateHttpClient() => new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private async Task<string> ResolveGitHubHashAsync(
        IReadOnlyList<GitHubAsset> assets,
        GitHubAsset packageAsset,
        CancellationToken cancellationToken)
    {
        string? digest = packageAsset.Digest;
        if (!string.IsNullOrWhiteSpace(digest))
        {
            int separator = digest.IndexOf(':');
            string candidate = separator >= 0 ? digest[(separator + 1)..] : digest;
            if (candidate.Length == 64 && candidate.All(Uri.IsHexDigit))
            {
                return candidate;
            }
        }

        GitHubAsset? checksumAsset = assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, packageAsset.Name + ".sha256", StringComparison.OrdinalIgnoreCase)
            || string.Equals(asset.Name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
        if (checksumAsset is null)
        {
            throw new InvalidDataException("GitHub 发布资产缺少 SHA-256 digest 或校验文件。 ");
        }

        string checksumText = await _httpClient
            .GetStringAsync(new Uri(checksumAsset.BrowserDownloadUrl, UriKind.Absolute), cancellationToken)
            .ConfigureAwait(false);
        foreach (string line in checksumText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split([' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 0
                && fields[0].Length == 64
                && fields[0].All(Uri.IsHexDigit)
                && (fields.Length == 1 || line.Contains(packageAsset.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return fields[0];
            }
        }

        throw new InvalidDataException("GitHub 校验文件中没有匹配更新包的 SHA-256。 ");
    }

    private static Uri BuildGitHubReleaseUri(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            throw new ArgumentException("请配置 GitHub 仓库，例如 owner/repository。 ", nameof(repository));
        }

        string value = repository.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? repositoryUri))
        {
            if (!string.Equals(repositoryUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("GitHub 仓库地址必须指向 github.com。 ", nameof(repository));
            }
            value = repositoryUri.AbsolutePath.Trim('/');
        }

        value = value.TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        string[] parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("GitHub 仓库格式应为 owner/repository。 ", nameof(repository));
        }

        return new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/releases/latest");
    }

    private static Version ReadApplicationVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(OnlineUpdateService).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (TryParseVersion(informational, out Version parsed))
        {
            return parsed;
        }

        return NormalizeVersion(assembly.GetName().Version ?? new Version(1, 0, 0, 0));
    }

    private static Version ValidateManifest(UpdateManifest manifest)
    {
        if (!TryParseVersion(manifest.Version, out Version version))
        {
            throw new InvalidDataException("更新清单中的版本号无效。 ");
        }

        if (!Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out Uri? packageUri))
        {
            throw new InvalidDataException("更新清单中的包地址无效。 ");
        }
        ValidateHttpsUri(packageUri, nameof(manifest.PackageUrl));

        if (manifest.Sha256.Length != 64 || manifest.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("更新清单中的 SHA-256 无效。 ");
        }

        if (manifest.PackageSize is <= 0 or > MaximumPackageBytes)
        {
            throw new InvalidDataException("更新清单中的包大小无效。 ");
        }

        return version;
    }

    private static void ValidateHttpsUri(Uri uri, string parameterName)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("线上更新地址必须使用 HTTPS。 ", parameterName);
        }
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidate = value.Trim();
        if (candidate.StartsWith('v') || candidate.StartsWith('V'))
        {
            candidate = candidate[1..];
        }
        int metadataSeparator = candidate.IndexOfAny(['+', '-']);
        if (metadataSeparator > 0)
        {
            candidate = candidate[..metadataSeparator];
        }

        if (!Version.TryParse(candidate, out Version? parsed) || parsed is null)
        {
            return false;
        }

        version = parsed;
        return true;
    }

    private static Version ParseReleaseVersion(string tagName)
    {
        if (!TryParseVersion(tagName, out Version version))
        {
            throw new InvalidDataException($"GitHub 标签 {tagName} 不是有效版本号。 ");
        }

        return NormalizeVersion(version);
    }

    private static Version NormalizeVersion(Version version) => new(
        Math.Max(0, version.Major),
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string GetInstallerFailureLogPath() => Path.Combine(
        AppPaths.LocalDataDirectory,
        "Updates",
        InstallerFailureLogFileName);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;
        public string? Name { get; init; }
        public string? Body { get; init; }
        public bool Draft { get; init; }
        public bool Prerelease { get; init; }
        public List<GitHubAsset> Assets { get; init; } = [];
    }

    private sealed record GitHubAsset
    {
        public string Name { get; init; } = string.Empty;
        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;
        public long Size { get; init; }
        public string? Digest { get; init; }
    }
}
