using System.Text;
using System.Text.Json;
using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Core.Transports;
using DeviceDebugStudio.Infrastructure.Import;
using DeviceDebugStudio.Infrastructure.Persistence;

namespace DeviceDebugStudio.Tests;

public sealed class ProfileAndImportTests
{
    [Fact]
    public void AppSettingsRoundTripsTerminalColumnWidths()
    {
        AppSettings settings = new()
        {
            TerminalTimeColumnWidth = 96,
            TerminalDirectionColumnWidth = 54,
            TerminalEndpointColumnWidth = 184,
            TerminalSizeColumnWidth = 72,
            TerminalContentColumnWidth = 640
        };

        string json = JsonSerializer.Serialize(settings);
        AppSettings loaded = Assert.IsType<AppSettings>(JsonSerializer.Deserialize<AppSettings>(json));

        Assert.Equal(96, loaded.TerminalTimeColumnWidth);
        Assert.Equal(54, loaded.TerminalDirectionColumnWidth);
        Assert.Equal(184, loaded.TerminalEndpointColumnWidth);
        Assert.Equal(72, loaded.TerminalSizeColumnWidth);
        Assert.Equal(640, loaded.TerminalContentColumnWidth);

        AppSettings defaults = Assert.IsType<AppSettings>(JsonSerializer.Deserialize<AppSettings>("{}"));
        Assert.Equal(AppSettings.DefaultTerminalTimeColumnWidth, defaults.TerminalTimeColumnWidth);
        Assert.Equal(AppSettings.DefaultTerminalContentColumnWidth, defaults.TerminalContentColumnWidth);
    }

    [Fact]
    public async Task ProfileStoreRoundTripsPolymorphicTransportSettings()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            JsonDeviceProfileStore store = new(directory);
            QuickCommandVariableSet low = new()
            {
                Name = "低速",
                Variables =
                [
                    new QuickCommandVariable { Name = "id", Value = "10", Type = "数值" },
                    new QuickCommandVariable { Name = "speed", Value = "14" }
                ]
            };
            QuickCommandVariableSet high = new()
            {
                Name = "高速",
                Variables =
                [
                    new QuickCommandVariable { Name = "id", Value = "20" },
                    new QuickCommandVariable { Name = "speed", Value = "20" }
                ]
            };
            DeviceProfile profile = new()
            {
                Name = "回归设备",
                WorkspaceMode = WorkspaceMode.Bluetooth,
                Transport = new BleGattTransportSettings(),
                Terminal = new TerminalPreferences { QuickCommandDataFormat = "ASCII" },
                CommandGroups =
                [
                    new QuickCommandGroup
                    {
                        Commands =
                        [
                            new QuickCommand
                            {
                                Name = "查询",
                                Payload = "AT",
                                UsageCount = 12,
                                NameColumnWeight = 96,
                                PayloadColumnWeight = 336,
                                VariableSets = [low, high],
                                SelectedVariableSetId = high.Id,
                                LastUsedAt = DateTimeOffset.Parse("2026-07-15T12:00:00+08:00")
                            }
                        ]
                    }
                ]
            };

            await store.SaveAsync(profile);
            DeviceProfile loaded = Assert.Single(await store.LoadAllAsync());

            Assert.Equal(profile.Id, loaded.Id);
            Assert.Equal(WorkspaceMode.Bluetooth, loaded.WorkspaceMode);
            Assert.IsType<BleGattTransportSettings>(loaded.Transport);
            Assert.Equal("ASCII", loaded.Terminal.QuickCommandDataFormat);
            Assert.Equal("查询", loaded.CommandGroups[0].Commands[0].Name);
            Assert.Equal(12, loaded.CommandGroups[0].Commands[0].UsageCount);
            Assert.Equal(96, loaded.CommandGroups[0].Commands[0].NameColumnWeight);
            Assert.Equal(336, loaded.CommandGroups[0].Commands[0].PayloadColumnWeight);
            QuickCommand loadedCommand = loaded.CommandGroups[0].Commands[0];
            Assert.Collection(
                loadedCommand.VariableSets,
                variableSet => Assert.Equal("低速", variableSet.Name),
                variableSet => Assert.Equal("高速", variableSet.Name));
            Assert.Equal(high.Id, loadedCommand.SelectedVariableSetId);
            Assert.Equal("10", loadedCommand.VariableSets[0].Variables[0].Value);
            Assert.Equal("数值", loadedCommand.VariableSets[0].Variables[0].Type);
            Assert.Equal("14", loadedCommand.VariableSets[0].Variables[1].Value);
            Assert.Equal(profile.CommandGroups[0].Commands[0].LastUsedAt, loaded.CommandGroups[0].Commands[0].LastUsedAt);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ImportsSelectedLegacyFiles()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string path = Path.Combine(directory, "sscom51.ini");
            await File.WriteAllTextAsync(
                path,
                "N1=T,AT\r\nN101=0,状态查询,500\r\nN1057=Y\r\nN1080=COM3\r\nN1081=115200\r\n",
                Encoding.GetEncoding(936));

            LegacyImportResult result = await new LegacyConfigImporter().ImportFilesAsync([path]);

            DeviceProfile profile = Assert.Single(result.Profiles);
            Assert.Empty(result.Warnings);
            Assert.Equal(WorkspaceMode.Serial, profile.WorkspaceMode);
            SerialTransportSettings transport = Assert.IsType<SerialTransportSettings>(profile.Transport);
            Assert.Equal("COM3", transport.PortName);
            QuickCommand command = Assert.Single(profile.CommandGroups[0].Commands);
            Assert.Equal("状态查询", command.Name);
            Assert.Equal("AT", command.Payload);
            Assert.Equal(500, command.RepeatIntervalMs);
            Assert.Equal("CRLF", command.LineEnding);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ImportsAndExportsDeviceProfileJson()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "device.json");
            DeviceProfileFileService service = new();
            DeviceProfile source = new()
            {
                Name = "网络设备",
                WorkspaceMode = WorkspaceMode.Network,
                Transport = new UdpTransportSettings { LocalPort = 6000, RemotePort = 7000 },
                CommandGroups =
                [
                    new QuickCommandGroup
                    {
                        Commands =
                        [
                            new QuickCommand
                            {
                                Name = "启动",
                                UsageCount = 8,
                                NameColumnWeight = 110,
                                PayloadColumnWeight = 322,
                                VariableSets =
                                [
                                    new QuickCommandVariableSet
                                    {
                                        Name = "通道 2",
                                        Variables = [new QuickCommandVariable { Name = "channel", Value = "02" }]
                                    }
                                ]
                            }
                        ]
                    }
                ]
            };

            await service.ExportAsync(source, path);
            DeviceProfile imported = await service.ImportAsync(path);

            Assert.NotEqual(source.Id, imported.Id);
            Assert.Equal(source.Name, imported.Name);
            Assert.Equal(WorkspaceMode.Network, imported.WorkspaceMode);
            Assert.Equal(8, imported.CommandGroups[0].Commands[0].UsageCount);
            Assert.Equal(110, imported.CommandGroups[0].Commands[0].NameColumnWeight);
            Assert.Equal(322, imported.CommandGroups[0].Commands[0].PayloadColumnWeight);
            QuickCommandVariableSet importedSet = Assert.Single(imported.CommandGroups[0].Commands[0].VariableSets);
            QuickCommandVariable importedVariable = Assert.Single(importedSet.Variables);
            Assert.Equal("通道 2", importedSet.Name);
            Assert.Equal("channel", importedVariable.Name);
            Assert.Equal("02", importedVariable.Value);
            Assert.Equal(6000, Assert.IsType<UdpTransportSettings>(imported.Transport).LocalPort);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ConfigurableProfileStoreWritesToSelectedDirectory()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string first = Path.Combine(root, "first");
            string selected = Path.Combine(root, "selected");
            JsonDeviceProfileStore store = new(first);
            store.SetDirectory(selected);

            await store.SaveAsync(new DeviceProfile { Name = "自定义目录" });

            Assert.Equal(Path.GetFullPath(selected), store.DirectoryPath);
            Assert.Single(Directory.EnumerateFiles(selected, "*.json"));
            Assert.Empty(Directory.Exists(first) ? Directory.EnumerateFiles(first, "*.json") : []);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ImportsAllExistingSscomProfilesWithoutChangingSources()
    {
        const string root = @"C:\Users\12087\Desktop\串口";
        if (!Directory.Exists(root))
        {
            return;
        }

        Dictionary<string, int> expectedCounts = new(StringComparer.OrdinalIgnoreCase)
        {
            ["车盒"] = 92,
            ["分布式"] = 8,
            ["富瑞坤"] = 21,
            ["开关矩阵"] = 1,
            ["移相器"] = 61,
            ["C02"] = 89,
            ["E90新模块"] = 6,
            ["ESP32lyrat"] = 34,
            ["ka变频器"] = 32,
            ["ka地球站_ACU"] = 55,
            ["ka地球站_ADU"] = 86,
            ["LRIT"] = 36,
            ["S02"] = 42,
            ["S02_gataway"] = 56,
            ["S05"] = 54,
            ["U70"] = 35
        };
        Dictionary<string, DateTime> sourceTimes = Directory.EnumerateFiles(root, "sscom51.ini", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);

        LegacyImportResult result = await new LegacyConfigImporter().ImportDirectoryAsync(root);
        DeviceProfile[] sscomProfiles = result.Profiles.Where(profile => profile.Description.Contains("SSCOM", StringComparison.Ordinal)).ToArray();

        Assert.Equal(expectedCounts.Count, sscomProfiles.Length);
        foreach ((string name, int expected) in expectedCounts)
        {
            DeviceProfile profile = Assert.Single(sscomProfiles, item => item.Name == name);
            Assert.Equal(expected, profile.CommandGroups.Sum(group => group.Commands.Count));
        }
        DeviceProfile kaProfile = Assert.Single(sscomProfiles, item => item.Name == "ka变频器");
        Assert.Equal("CRLF", kaProfile.Terminal.LineEnding);
        Assert.Equal(FramingMode.IdleGap, kaProfile.FrameTemplate.Mode);
        Assert.Equal(20, kaProfile.FrameTemplate.IdleGapMs);
        foreach ((string path, DateTime timestamp) in sourceTimes)
        {
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "DeviceDebugStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
