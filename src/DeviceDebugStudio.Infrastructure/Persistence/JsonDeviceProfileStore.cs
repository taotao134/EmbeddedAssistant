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
            List<DeviceProfile> profiles = [];
            foreach (string file in Directory.EnumerateFiles(_directory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    await using FileStream stream = File.OpenRead(file);
                    DeviceProfile? profile = await JsonSerializer.DeserializeAsync<DeviceProfile>(stream, _options, cancellationToken).ConfigureAwait(false);
                    if (profile is not null && profile.SchemaVersion <= DeviceProfile.CurrentSchemaVersion)
                    {
                        profiles.Add(profile);
                    }
                }
                catch (JsonException)
                {
                }
                catch (IOException)
                {
                }
            }

            return profiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(DeviceProfile profile, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        string destination = GetProfilePath(profile.Id);
        string temporary = destination + ".tmp";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, profile with { UpdatedAt = DateTimeOffset.Now }, _options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
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
            string path = GetProfilePath(profileId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetProfilePath(Guid id) => Path.Combine(_directory, $"{id:N}.json");
}
