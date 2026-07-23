using System.Text.Json;

namespace DeviceDebugStudio.Infrastructure.Persistence;

public sealed record AppSettings
{
    public const string DefaultGitHubRepository = "taotao134/EmbeddedAssistant";
    public const double DefaultTerminalTimeColumnWidth = 122;
    public const double DefaultTerminalDirectionColumnWidth = 84;
    public const double DefaultTerminalEndpointColumnWidth = 150;
    public const double DefaultTerminalSizeColumnWidth = 66;
    public const double DefaultTerminalContentColumnWidth = 760;
    public const double DefaultFrameTimeColumnWidth = 100;
    public const double DefaultFrameLengthColumnWidth = 56;
    public const double DefaultFrameHexColumnWidth = 360;
    public const double DefaultFrameSummaryColumnWidth = 360;

    public string ProfileDirectory { get; init; } = AppPaths.ProfilesDirectory;
    public Guid? SelectedProfileId { get; init; }
    public double TerminalFontSize { get; init; } = 12;
    public double TerminalTimeColumnWidth { get; init; } = DefaultTerminalTimeColumnWidth;
    public double TerminalDirectionColumnWidth { get; init; } = DefaultTerminalDirectionColumnWidth;
    public double TerminalEndpointColumnWidth { get; init; } = DefaultTerminalEndpointColumnWidth;
    public double TerminalSizeColumnWidth { get; init; } = DefaultTerminalSizeColumnWidth;
    public double TerminalContentColumnWidth { get; init; } = DefaultTerminalContentColumnWidth;
    public double FrameTimeColumnWidth { get; init; } = DefaultFrameTimeColumnWidth;
    public double FrameLengthColumnWidth { get; init; } = DefaultFrameLengthColumnWidth;
    public double FrameHexColumnWidth { get; init; } = DefaultFrameHexColumnWidth;
    public double FrameSummaryColumnWidth { get; init; } = DefaultFrameSummaryColumnWidth;
    public string TerminalTextColor { get; init; } = "#E6EBE8";
    public string TerminalBackgroundColor { get; init; } = "#141817";
    public string GitHubRepository { get; init; } = DefaultGitHubRepository;
    public bool AutoUpdateEnabled { get; init; } = true;
    public List<string> TerminalTextPalette { get; init; } =
        ["#E6EBE8", "#7FE2B8", "#F7C574", "#9CDCFE", "#DCDCAA", "#FF8F8F", "#C586C0", "#7AA2F7"];
    public List<string> TerminalBackgroundPalette { get; init; } =
        ["#141817", "#000000", "#1E293B", "#173A34", "#312544", "#443125", "#F5F5F5", "#FFFFFF"];
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
