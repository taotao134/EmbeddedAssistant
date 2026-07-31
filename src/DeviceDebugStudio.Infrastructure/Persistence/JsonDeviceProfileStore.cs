using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceDebugStudio.Core.Profiles;

namespace DeviceDebugStudio.Infrastructure.Persistence;

public sealed class JsonDeviceProfileStore : IConfigurableDeviceProfileStore
{
    private string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonDeviceProfileStore(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? AppPaths.ProfilesDirectory);
    }

    public string DirectoryPath => _directory;

    public void SetDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("配置目录不能为空。", nameof(directory));
        }

        string resolved = Path.GetFullPath(directory);
        Directory.CreateDirectory(resolved);
        _directory = resolved;
    }

    public async Task<IReadOnlyList<DeviceProfile>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<(DeviceProfile Profile, string Path)> profileFiles = [];
            foreach (string file in Directory.EnumerateFiles(_directory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    await using FileStream stream = File.OpenRead(file);
                    DeviceProfile? profile = await JsonSerializer.DeserializeAsync<DeviceProfile>(stream, _options, cancellationToken).ConfigureAwait(false);
                    if (profile is not null && profile.SchemaVersion <= DeviceProfile.CurrentSchemaVersion)
                    {
                        profileFiles.Add((profile, file));
                    }
                }
                catch (JsonException)
                {
                }
                catch (IOException)
                {
                }
            }

            List<(DeviceProfile Profile, string Path)> latestProfiles = profileFiles
                .GroupBy(item => item.Profile.Id)
                .Select(group => group.OrderByDescending(item => item.Profile.UpdatedAt).First())
                .ToList();
            await MigrateProfileFileNamesAsync(profileFiles, latestProfiles, cancellationToken).ConfigureAwait(false);

            return latestProfiles
                .Select(item => item.Profile)
                .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(DeviceProfile profile, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        string? temporary = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string destination = await GetProfilePathAsync(profile, cancellationToken).ConfigureAwait(false);
            temporary = destination + ".tmp";
            await using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, profile with { UpdatedAt = DateTimeOffset.Now }, _options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination, true);
            await RemoveOtherProfileCopiesAsync(profile.Id, destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
            {
                File.Delete(temporary);
            }
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (string path in await FindProfilePathsAsync(profileId, cancellationToken).ConfigureAwait(false))
            {
                File.Delete(path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> GetProfilePathAsync(DeviceProfile profile, CancellationToken cancellationToken)
    {
        string stem = GetSafeFileName(profile.Name);
        string candidate = Path.Combine(_directory, $"{stem}.json");
        int suffix = 2;
        while (File.Exists(candidate))
        {
            Guid? existingId = await TryReadProfileIdAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (existingId == profile.Id)
            {
                return candidate;
            }

            candidate = Path.Combine(_directory, $"{stem}_{suffix}.json");
            suffix++;
        }

        return candidate;
    }

    private async Task MigrateProfileFileNamesAsync(
        IReadOnlyList<(DeviceProfile Profile, string Path)> profileFiles,
        IReadOnlyList<(DeviceProfile Profile, string Path)> latestProfiles,
        CancellationToken cancellationToken)
    {
        foreach ((DeviceProfile profile, string source) in latestProfiles)
        {
            string destination = await GetProfilePathAsync(profile, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(source, destination, true);
            }

            foreach ((DeviceProfile copy, string path) in profileFiles.Where(item => item.Profile.Id == profile.Id))
            {
                if (!string.Equals(path, destination, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private async Task RemoveOtherProfileCopiesAsync(Guid profileId, string destination, CancellationToken cancellationToken)
    {
        foreach (string path in await FindProfilePathsAsync(profileId, cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(path, destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
            }
        }
    }

    private async Task<List<string>> FindProfilePathsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        List<string> paths = [];
        foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            if (await TryReadProfileIdAsync(path, cancellationToken).ConfigureAwait(false) == profileId)
            {
                paths.Add(path);
            }
        }
        return paths;
    }

    private async Task<Guid?> TryReadProfileIdAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = File.OpenRead(path);
            DeviceProfile? profile = await JsonSerializer.DeserializeAsync<DeviceProfile>(stream, _options, cancellationToken).ConfigureAwait(false);
            return profile?.Id;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string GetSafeFileName(string name)
    {
        string value = string.IsNullOrWhiteSpace(name) ? "未命名设备" : name.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        value = value.Trim(' ', '.');
        return string.IsNullOrEmpty(value) ? "未命名设备" : value;
    }
}
