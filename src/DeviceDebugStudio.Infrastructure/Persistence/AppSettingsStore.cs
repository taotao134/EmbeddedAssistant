using System.Text.Json;

namespace DeviceDebugStudio.Infrastructure.Persistence;

public sealed record AppSettings
{
    public string ProfileDirectory { get; init; } = AppPaths.ProfilesDirectory;
    public double TerminalFontSize { get; init; } = 12;
    public string TerminalTextColor { get; init; } = "#E6EBE8";
    public string TerminalBackgroundColor { get; init; } = "#141817";
}

public sealed class AppSettingsStore
{
    private readonly string _path = Path.Combine(AppPaths.LocalDataDirectory, "settings.json");
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            await using FileStream stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, _options, cancellationToken).ConfigureAwait(false)
                ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        string temporary = _path + ".tmp";
        await using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, settings, _options, cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, _path, true);
    }
}
