using System.Text;
using System.Text.RegularExpressions;
using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Core.Protocol;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.Infrastructure.Import;

public sealed record LegacyImportResult(IReadOnlyList<DeviceProfile> Profiles, IReadOnlyList<string> Warnings);

public sealed class LegacyConfigImporter
{
    private static readonly Regex KeyLineRegex = new("^N(?<key>\\d+)=(?<value>.*)\\r?$", RegexOptions.Multiline | RegexOptions.Compiled);
    private readonly Encoding _gbk;

    public LegacyConfigImporter()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _gbk = Encoding.GetEncoding(936);
    }

    public async Task<LegacyImportResult> ImportDirectoryAsync(string rootDirectory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException(rootDirectory);
        }

        List<DeviceProfile> profiles = [];
        List<string> warnings = [];
        foreach (string path in Directory.EnumerateFiles(rootDirectory, "sscom51.ini", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                profiles.Add(await ImportSscomAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                warnings.Add($"{path}：{exception.Message}");
            }
        }

        foreach (string path in Directory.EnumerateFiles(rootDirectory, "netassist.cfg", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DeviceProfile? profile = await ImportNetAssistAsync(path, cancellationToken).ConfigureAwait(false);
                if (profile is not null)
                {
                    profiles.Add(profile);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                warnings.Add($"{path}：{exception.Message}");
            }
        }

        return new LegacyImportResult(profiles, warnings);
    }

    public async Task<LegacyImportResult> ImportFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        List<DeviceProfile> profiles = [];
        List<string> warnings = [];
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.Equals(Path.GetFileName(path), "sscom51.ini", StringComparison.OrdinalIgnoreCase))
                {
                    profiles.Add(await ImportSscomAsync(path, cancellationToken).ConfigureAwait(false));
                }
                else if (string.Equals(Path.GetFileName(path), "netassist.cfg", StringComparison.OrdinalIgnoreCase))
                {
                    DeviceProfile? profile = await ImportNetAssistAsync(path, cancellationToken).ConfigureAwait(false);
                    if (profile is not null)
                    {
                        profiles.Add(profile);
                    }
                }
                else
                {
                    warnings.Add($"{path}：不是支持的 SSCOM 或 NetAssist 配置文件。 ");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                warnings.Add($"{path}：{exception.Message}");
            }
        }
        return new LegacyImportResult(profiles, warnings);
    }

    public async Task<DeviceProfile> ImportSscomAsync(string path, CancellationToken cancellationToken = default)
    {
        string text = _gbk.GetString(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        Dictionary<int, string> values = ParseNumericKeys(text);
        string profileName = Directory.GetParent(path)?.Name ?? Path.GetFileNameWithoutExtension(path);
        ChecksumKind checksum = ParseChecksum(GetSetting(values, 1060));
        string lineEnding = string.Equals(GetSetting(values, 1057), "Y", StringComparison.OrdinalIgnoreCase)
            ? "CRLF"
            : "None";
        List<QuickCommand> commands = [];
        for (int index = 1; index <= 100; index++)
        {
            if (!values.TryGetValue(index, out string? commandValue)
                || !TryParseSscomCommand(commandValue, out char mode, out string payload))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(payload))
            {
                continue;
            }

            string label = $"命令 {index}";
            bool repeat = false;
            int interval = 1000;
            if (values.TryGetValue(100 + index, out string? metadata))
            {
                string[] parts = metadata.Split(',', 3);
                repeat = parts.ElementAtOrDefault(0) == "1";
                if (!string.IsNullOrWhiteSpace(parts.ElementAtOrDefault(1)))
                {
                    label = parts[1].Trim();
                }
                if (int.TryParse(parts.ElementAtOrDefault(2), out int parsedInterval) && parsedInterval > 0)
                {
                    interval = parsedInterval;
                }
            }

            commands.Add(new QuickCommand
            {
                Name = label,
                Payload = payload,
                Template = payload,
                IsHex = char.ToUpperInvariant(mode) == 'H',
                LineEnding = lineEnding,
                Checksum = checksum,
                RepeatEnabled = repeat,
                RepeatIntervalMs = interval
            });
        }

        TransportSettings transport = BuildSscomTransport(values);
        return new DeviceProfile
        {
            Name = profileName,
            Description = $"从 SSCOM 导入：{path}",
            WorkspaceMode = transport.Kind == TransportKind.Serial ? WorkspaceMode.Serial : WorkspaceMode.Network,
            Transport = transport,
            Terminal = new TerminalPreferences
            {
                EncodingName = "GBK",
                SendAsHex = string.Equals(GetSetting(values, 1055), "H", StringComparison.OrdinalIgnoreCase),
                ReceiveAsHex = string.Equals(GetSetting(values, 1059), "Y", StringComparison.OrdinalIgnoreCase),
                ShowTimestamp = true,
                LineEnding = lineEnding
            },
            FrameTemplate = string.Equals(GetSetting(values, 1064), "Y", StringComparison.OrdinalIgnoreCase)
                ? new FrameTemplate
                {
                    Name = "SSCOM 分包",
                    Mode = FramingMode.IdleGap,
                    IdleGapMs = ParsePositiveInt(GetSetting(values, 1065), 20)
                }
                : new FrameTemplate(),
            CommandGroups =
            [
                new QuickCommandGroup
                {
                    Name = "SSCOM 导入",
                    Commands = commands
                }
            ]
        };
    }

    public async Task<DeviceProfile?> ImportNetAssistAsync(string path, CancellationToken cancellationToken = default)
    {
        string text = _gbk.GetString(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        bool inShortcut = false;
        List<QuickCommand> commands = [];
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.StartsWith('['))
            {
                inShortcut = string.Equals(line, "[SHORTCUT]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inShortcut)
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string name = line[..separator].Trim();
            string payload = line[(separator + 1)..].Trim();
            if (payload.Length > 0)
            {
                commands.Add(new QuickCommand { Name = name, Payload = payload, IsHex = true });
            }
        }

        if (commands.Count == 0)
        {
            return null;
        }

        string parentName = Directory.GetParent(Directory.GetParent(path)?.FullName ?? string.Empty)?.Name
            ?? Directory.GetParent(path)?.Name
            ?? "NetAssist";
        return new DeviceProfile
        {
            Name = $"{parentName}（NetAssist）",
            Description = $"从 NetAssist 导入：{path}",
            WorkspaceMode = WorkspaceMode.Network,
            Transport = new TcpClientTransportSettings(),
            Terminal = new TerminalPreferences { ReceiveAsHex = true },
            CommandGroups = [new QuickCommandGroup { Name = "NetAssist 导入", Commands = commands }]
        };
    }

    private static Dictionary<int, string> ParseNumericKeys(string text)
    {
        Dictionary<int, string> values = [];
        foreach (Match match in KeyLineRegex.Matches(text))
        {
            if (int.TryParse(match.Groups["key"].Value, out int key))
            {
                values[key] = match.Groups["value"].Value.TrimEnd('\r');
            }
        }
        return values;
    }

    private static bool TryParseSscomCommand(string value, out char mode, out string payload)
    {
        mode = default;
        payload = string.Empty;
        int separator = value.IndexOf(',');
        if (separator <= 0)
        {
            return false;
        }

        mode = value[0];
        payload = value[(separator + 1)..];
        return true;
    }

    private static TransportSettings BuildSscomTransport(IReadOnlyDictionary<int, string> values)
    {
        string mode = GetSetting(values, 1080);
        string host = GetSetting(values, 1068);
        int remotePort = ParsePositiveInt(GetSetting(values, 1069), 80);
        int localPort = ParsePositiveInt(GetSetting(values, 1070), 777);
        return mode switch
        {
            "1" => new TcpClientTransportSettings { Host = host, Port = remotePort },
            "2" => new TcpServerTransportSettings { Port = localPort },
            "3" => new UdpTransportSettings { LocalPort = localPort, RemoteAddress = host, RemotePort = remotePort },
            _ => new SerialTransportSettings
            {
                PortName = mode.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ? mode : string.Empty,
                BaudRate = ParsePositiveInt(GetSetting(values, 1081), 115200),
                DtrEnable = string.Equals(GetSetting(values, 1061), "Y", StringComparison.OrdinalIgnoreCase),
                RtsEnable = string.Equals(GetSetting(values, 1062), "Y", StringComparison.OrdinalIgnoreCase)
            }
        };
    }

    private static string GetSetting(IReadOnlyDictionary<int, string> values, int key)
    {
        if (!values.TryGetValue(key, out string? value))
        {
            return string.Empty;
        }
        return value.StartsWith(',') ? value[1..].Trim() : value.Trim();
    }

    private static int ParsePositiveInt(string value, int fallback) => int.TryParse(value, out int result) && result > 0 ? result : fallback;

    private static ChecksumKind ParseChecksum(string value) => value switch
    {
        "1" => ChecksumKind.Crc16Modbus,
        "2" => ChecksumKind.Add8,
        "3" => ChecksumKind.Xor8,
        _ => ChecksumKind.None
    };
}
