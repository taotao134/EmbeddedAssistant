using System.Net;
using System.Security.Cryptography;
using System.Text;
using DeviceDebugStudio.Infrastructure.Updates;

namespace DeviceDebugStudio.Tests;

public sealed class OnlineUpdateTests
{
    [Fact]
    public async Task ChecksGitHubReleaseSelectsWinX64AssetAndDownloadsIt()
    {
        byte[] package = CreatePackageBytes();
        string hash = Convert.ToHexString(SHA256.HashData(package));
        string releaseJson = $$"""
            {
              "tag_name": "v1.2.4",
              "name": "1.2.4",
              "body": "修复网络通信稳定性。",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "DeviceDebugStudio-source.zip",
                  "browser_download_url": "https://github.com/acme/device/releases/download/v1.2.4/source.zip",
                  "size": 12,
                  "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000"
                },
                {
                  "name": "DeviceDebugStudio-win-x64.zip",
                  "browser_download_url": "https://github.com/acme/device/releases/download/v1.2.4/DeviceDebugStudio-win-x64.zip",
                  "size": {{package.Length}},
                  "digest": "sha256:{{hash}}"
                }
              ]
            }
            """;
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal) == true)
            {
                return JsonResponse(releaseJson);
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("DeviceDebugStudio-win-x64.zip", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(package)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using OnlineUpdateService service = new(client, new Version(1, 2, 3));

        UpdateCheckResult result = await service.CheckGitHubAsync("https://github.com/acme/device.git");
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(new Version(1, 2, 4, 0), result.LatestVersion);
        Assert.Equal("DeviceDebugStudio-win-x64.zip", Path.GetFileName(result.Manifest.PackageUrl));
        Assert.Equal(hash, result.Manifest.Sha256);
        Assert.Equal("修复网络通信稳定性。", result.Manifest.ReleaseNotes);

        List<double> progressValues = [];
        string packagePath = await service.DownloadAsync(result.Manifest, new Progress<double>(progressValues.Add));
        try
        {
            Assert.Equal(package, await File.ReadAllBytesAsync(packagePath));
            Assert.Contains(progressValues, value => value >= 1);
        }
        finally
        {
            DeleteTemporaryPackage(packagePath);
        }
    }

    [Fact]
    public async Task ChecksGitHubReleaseUsingSha256SidecarWhenDigestIsMissing()
    {
        byte[] package = CreatePackageBytes();
        string hash = Convert.ToHexString(SHA256.HashData(package));
        string releaseJson = $$"""
            {
              "tag_name": "1.4.0",
              "name": "1.4.0",
              "body": "",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "DeviceDebugStudio-win-x64.zip",
                  "browser_download_url": "https://github.com/acme/device/releases/download/1.4.0/DeviceDebugStudio-win-x64.zip",
                  "size": {{package.Length}}
                },
                {
                  "name": "DeviceDebugStudio-win-x64.zip.sha256",
                  "browser_download_url": "https://github.com/acme/device/releases/download/1.4.0/DeviceDebugStudio-win-x64.zip.sha256",
                  "size": 80
                }
              ]
            }
            """;
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal) == true)
            {
                return JsonResponse(releaseJson);
            }

            if (request.RequestUri?.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{hash}\n", Encoding.ASCII)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using OnlineUpdateService service = new(client, new Version(1, 0, 0));

        UpdateCheckResult result = await service.CheckGitHubAsync("acme/device");

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(hash, result.Manifest.Sha256);
    }

    [Fact]
    public async Task OlderGitHubReleaseIsNotOfferedAsAnUpdate()
    {
        byte[] package = CreatePackageBytes();
        string hash = Convert.ToHexString(SHA256.HashData(package));
        string releaseJson = $$"""
            {
              "tag_name": "v1.0.0",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "DeviceDebugStudio-win-x64.zip",
                  "browser_download_url": "https://github.com/acme/device/releases/download/v1.0.0/DeviceDebugStudio-win-x64.zip",
                  "size": {{package.Length}},
                  "digest": "sha256:{{hash}}"
                }
              ]
            }
            """;
        using HttpClient client = new(new StubHttpMessageHandler(_ => JsonResponse(releaseJson)));
        using OnlineUpdateService service = new(client, new Version(1, 0, 1));

        UpdateCheckResult result = await service.CheckGitHubAsync("acme/device");

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(new Version(1, 0, 0, 0), result.LatestVersion);
    }

    [Fact]
    public async Task DownloadRejectsSha256Mismatch()
    {
        byte[] package = CreatePackageBytes();
        UpdateManifest manifest = new()
        {
            Version = "1.1.0",
            PackageUrl = "https://github.com/acme/device/releases/download/v1.1.0/DeviceDebugStudio-win-x64.zip",
            Sha256 = new string('0', 64),
            PackageSize = package.Length
        };
        using HttpClient client = new(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(package)
        }));
        using OnlineUpdateService service = new(client, new Version(1, 0, 0));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(manifest));
    }

    [Fact]
    public async Task RejectsNonHttpsGitHubRepository()
    {
        using OnlineUpdateService service = new(new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))), new Version(1, 0, 0));

        await Assert.ThrowsAsync<ArgumentException>(() => service.CheckGitHubAsync("https://gitlab.com/acme/device"));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static byte[] CreatePackageBytes() => Encoding.UTF8.GetBytes("DeviceDebugStudio update package");

    private static void DeleteTemporaryPackage(string packagePath)
    {
        string? directory = Path.GetDirectoryName(packagePath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
