using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceDebugStudio.Core.Profiles;

namespace DeviceDebugStudio.Infrastructure.Persistence;

public sealed class DeviceProfileFileService
{
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<DeviceProfile> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(path);
        DeviceProfile profile = await JsonSerializer.DeserializeAsync<DeviceProfile>(stream, _options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("设备配置文件为空。 ");
        if (profile.SchemaVersion > DeviceProfile.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"配置版本 {profile.SchemaVersion} 高于当前支持版本。 ");
        }
        return profile with { Id = Guid.NewGuid(), UpdatedAt = DateTimeOffset.Now };
    }

    public async Task ExportAsync(DeviceProfile profile, string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, profile, _options, cancellationToken).ConfigureAwait(false);
    }
}
