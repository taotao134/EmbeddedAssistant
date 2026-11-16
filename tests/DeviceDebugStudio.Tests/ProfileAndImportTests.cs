using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DeviceDebugStudio.App.ViewModels;
using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Core.Transports;
using DeviceDebugStudio.Infrastructure.Import;
using DeviceDebugStudio.Infrastructure.Persistence;
using DeviceDebugStudio.Infrastructure.Transports;
using DeviceDebugStudio.Infrastructure.Updates;

namespace DeviceDebugStudio.Tests;

public sealed class ProfileAndImportTests
{
    [Fact]
    public void ProfilesDirectoryIsUnderSoftwareDirectory()
    {
        Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory), AppPaths.SoftwareDirectory);
        Assert.Equal(Path.Combine(AppPaths.SoftwareDirectory, "Profiles"), AppPaths.ProfilesDirectory);
    }

    [Fact]
    public void TerminalPreferencesDefaultToCrLfLineEnding()
    {
        Assert.Equal("CRLF", new TerminalPreferences().LineEnding);
    }

    [Fact]
    public void AppSettingsRoundTripsTerminalColumnWidths()
    {
        Guid importedProfileId = Guid.NewGuid();
        AppSettings settings = new()
        {
            ImportedProfileSourcePaths = new()
            {
                [importedProfileId] = @"D:\imported\ka.json"
            },
            TerminalTimeColumnWidth = 96,
            TerminalDirectionColumnWidth = 54,
            TerminalEndpointColumnWidth = 184,
            TerminalSizeColumnWidth = 72,
            TerminalContentColumnWidth = 640,
            FrameTimeColumnWidth = 88,
            FrameLengthColumnWidth = 48,
            FrameHexColumnWidth = 280,
            FrameSummaryColumnWidth = 420,
            TerminalSeparatorEnabled = true,
            TerminalSeparatorIntervalMs = 750,
            TerminalSeparatorStyle = "#",
            TerminalSeparatorColor = "#408060",
            GitHubRepository = "acme/device-debug-studio",
            AutoUpdateEnabled = false,
            DebugLoggingEnabled = true
        };

        string json = JsonSerializer.Serialize(settings);
        AppSettings loaded = Assert.IsType<AppSettings>(JsonSerializer.Deserialize<AppSettings>(json));

        Assert.Equal(96, loaded.TerminalTimeColumnWidth);
        Assert.Equal(@"D:\imported\ka.json", loaded.ImportedProfileSourcePaths[importedProfileId]);
        Assert.Equal(54, loaded.TerminalDirectionColumnWidth);
        Assert.Equal(184, loaded.TerminalEndpointColumnWidth);
        Assert.Equal(72, loaded.TerminalSizeColumnWidth);
        Assert.Equal(640, loaded.TerminalContentColumnWidth);
        Assert.Equal(88, loaded.FrameTimeColumnWidth);
        Assert.Equal(48, loaded.FrameLengthColumnWidth);
        Assert.Equal(280, loaded.FrameHexColumnWidth);
        Assert.Equal(420, loaded.FrameSummaryColumnWidth);
        Assert.True(loaded.TerminalSeparatorEnabled);
        Assert.Equal(750, loaded.TerminalSeparatorIntervalMs);
        Assert.Equal("#", loaded.TerminalSeparatorStyle);
        Assert.Equal("#408060", loaded.TerminalSeparatorColor);
        Assert.Equal("acme/device-debug-studio", loaded.GitHubRepository);
        Assert.False(loaded.AutoUpdateEnabled);
        Assert.True(loaded.DebugLoggingEnabled);

        AppSettings defaults = Assert.IsType<AppSettings>(JsonSerializer.Deserialize<AppSettings>("{}"));
        Assert.Empty(defaults.ImportedProfileSourcePaths);
        Assert.Equal(AppSettings.DefaultTerminalTimeColumnWidth, defaults.TerminalTimeColumnWidth);
        Assert.Equal(AppSettings.DefaultTerminalContentColumnWidth, defaults.TerminalContentColumnWidth);
        Assert.Equal(AppSettings.DefaultFrameTimeColumnWidth, defaults.FrameTimeColumnWidth);
        Assert.Equal(AppSettings.DefaultFrameSummaryColumnWidth, defaults.FrameSummaryColumnWidth);
        Assert.Equal("#111111", defaults.TerminalTextColor);
        Assert.Equal("#FFFFFF", defaults.TerminalBackgroundColor);
        Assert.False(defaults.TerminalSeparatorEnabled);
        Assert.Equal(AppSettings.DefaultTerminalSeparatorIntervalMs, defaults.TerminalSeparatorIntervalMs);
        Assert.Equal(AppSettings.DefaultTerminalSeparatorStyle, defaults.TerminalSeparatorStyle);
        Assert.Equal(AppSettings.DefaultTerminalSeparatorColor, defaults.TerminalSeparatorColor);
        Assert.Equal("#111111", defaults.TerminalTextPalette[0]);
        Assert.Equal("#FFFFFF", defaults.TerminalBackgroundPalette[0]);
        Assert.Equal(AppSettings.DefaultGitHubRepository, defaults.GitHubRepository);
        Assert.True(defaults.AutoUpdateEnabled);
        Assert.False(defaults.DebugLoggingEnabled);
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
                Terminal = new TerminalPreferences
                {
                    QuickCommandDataFormat = "ASCII",
                    SendRepeatIntervalMs = 250
                },
                FrameTemplates =
                [
                    new FrameTemplate
                    {
                        Name = "状态",
                        MatchOffset = 3,
                        MatchHex = "01",
                        Fields = [new FrameField { Name = "状态码", Type = FrameFieldType.UInt8, Offset = 12 }]
                    },
                    new FrameTemplate
                    {
                        Name = "心跳",
                        MatchOffset = 3,
                        MatchHex = "00"
                    }
                ],
                CommandGroups =
                [
                    new QuickCommandGroup
                    {
                        Category = QuickCommandCategory.Bluetooth,
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
            Assert.Equal(250, loaded.Terminal.SendRepeatIntervalMs);
            Assert.Equal(2, loaded.FrameTemplates.Count);
            Assert.Equal("状态", loaded.FrameTemplates[0].Name);
            Assert.Equal("01", loaded.FrameTemplates[0].MatchHex);
            Assert.Equal("状态码", loaded.FrameTemplates[0].Fields[0].Name);
            Assert.Equal(QuickCommandCategory.Bluetooth, loaded.CommandGroups[0].Category);
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
            Assert.Equal(QuickCommandCategory.Sscom, profile.CommandGroups[0].Category);
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
    public async Task ImportsSscomCommandPayloadWithEnglishCommas()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string path = Path.Combine(directory, "sscom51.ini");
            await File.WriteAllTextAsync(
                path,
                "N20=A,$SETIP,192,168,0,10\r\nN120=0,设置 IP,1000\r\nN1057=Y\r\nN1080=COM3\r\nN1081=115200\r\n",
                Encoding.GetEncoding(936));

            LegacyImportResult result = await new LegacyConfigImporter().ImportFilesAsync([path]);

            QuickCommand command = Assert.Single(Assert.Single(result.Profiles).CommandGroups[0].Commands);
            Assert.Equal("$SETIP,192,168,0,10", command.Payload);
            Assert.Equal("$SETIP,192,168,0,10", command.Template);
            Assert.Equal("设置 IP", command.Name);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task NormalizesSscomControlSeparatorsWithoutDuplicatingCommas()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string path = Path.Combine(directory, "sscom51.ini");
            await File.WriteAllTextAsync(
                path,
                "N20=A,$SETIP\u0002192\u0002168\u0002\u000210\r\nN21=A,$SETIP,\u0002192\u0002,168,\u00020,\u000210\r\n",
                Encoding.GetEncoding(936));

            LegacyImportResult result = await new LegacyConfigImporter().ImportFilesAsync([path]);
            QuickCommand[] commands = Assert.Single(result.Profiles).CommandGroups[0].Commands.ToArray();

            Assert.Equal("$SETIP,192,168,,10", commands[0].Payload);
            Assert.Equal("$SETIP,192,168,0,10", commands[1].Payload);
            Assert.DoesNotContain('\u0002', commands[0].Payload);
            Assert.DoesNotContain('\u0002', commands[1].Payload);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void NormalizesExistingQuickCommandBeforeEditingOrSending()
    {
        QuickCommandItemViewModel command = new(new QuickCommand
        {
            Payload = "$SETIP,\u0002192\u0002,168,\u00020,\u000210",
            Template = "$SETIP,\u0002192\u0002,168,\u00020,\u000210"
        });

        Assert.Equal("$SETIP,192,168,0,10", command.Payload);
        Assert.Equal("$SETIP,192,168,0,10", command.Template);
        Assert.DoesNotContain('\u0002', command.ToModel().Payload);
    }

    [Fact]
    public void EditingQuickCommandPayloadDoesNotUpdateSchemeTemplate()
    {
        QuickCommandItemViewModel command = new(new QuickCommand
        {
            Payload = "$SETIP,192,168,1,1",
            Template = "$SETIP,${ip_a},${ip_b},${ip_c},${ip_d}"
        });

        command.Payload = "$SETIP,192,168,0,10";

        Assert.Equal("$SETIP,192,168,0,10", command.Payload);
        Assert.Equal("$SETIP,${ip_a},${ip_b},${ip_c},${ip_d}", command.Template);
    }

    [Fact]
    public void AutoGeneratesTemplateAndDefaultValuesFromPayload()
    {
        QuickCommandItemViewModel command = new(new QuickCommand
        {
            Payload = "$TEST2595,9.690000,30"
        });

        Assert.True(command.TryAutoGenerateTemplateFromPayload());
        Assert.Equal("$TEST2595,${param1},${param2}", command.Template);
        Assert.Equal(
            ["param1", "param2"],
            command.SelectedVariableSet!.Variables.Select(variable => variable.Name));
        Assert.Equal(
            ["9.690000", "30"],
            command.SelectedVariableSet.Variables.Select(variable => variable.Value));
        Assert.Equal("$TEST2595,9.690000,30", command.ResolvedPayload);
        Assert.Equal("$TEST2595,9.690000,30", command.Payload);
    }

    [Fact]
    public void AutoGeneratesEmptyAssignmentParameters()
    {
        QuickCommandItemViewModel command = new(new QuickCommand
        {
            Payload = "kas+pid=,,,"
        });

        Assert.True(command.TryAutoGenerateTemplateFromPayload());
        Assert.Equal("kas+pid=${param1},${param2},${param3}", command.Template);
        Assert.Equal(["param1", "param2", "param3"], command.SelectedVariableSet!.Variables.Select(variable => variable.Name));
        Assert.All(command.SelectedVariableSet.Variables, variable => Assert.Empty(variable.Value));
        Assert.Equal("kas+pid=,,,", command.Payload);
    }

    [Fact]
    public void AutoGenerationDoesNotOverwriteHandWrittenTemplate()
    {
        QuickCommandItemViewModel command = new(new QuickCommand
        {
            Payload = "$TEST2595,9.690000,30",
            Template = "$TEST2595,${frequency},${mode}"
        });

        Assert.False(command.TryAutoGenerateTemplateFromPayload());
        Assert.Equal("$TEST2595,${frequency},${mode}", command.Template);
        Assert.Equal(["frequency", "mode"], command.SelectedVariableSet!.Variables.Select(variable => variable.Name));
    }

    [Fact]
    public void PreservesIndependentPayloadAndDirectSchemeTemplate()
    {
        QuickCommandItemViewModel command = new(new QuickCommand
        {
            Payload = "$SETIP,192,168,0,10",
            Template = "$SETIP19216811"
        });

        Assert.Equal("$SETIP,192,168,0,10", command.Payload);
        Assert.Equal("$SETIP19216811", command.Template);
    }

    [Fact]
    public void VariableSchemeChangesOnlyResolvedSchemePayload()
    {
        QuickCommandItemViewModel command = new(new QuickCommand
        {
            Payload = "$SETIP,192,168,1,1",
            Template = "$SETIP,${ip_a},${ip_b},${ip_c},${ip_d}",
            VariableSets =
            [
                new QuickCommandVariableSet
                {
                    Name = "方案一",
                    Variables =
                    [
                        new QuickCommandVariable { Name = "ip_a", Value = "192" },
                        new QuickCommandVariable { Name = "ip_b", Value = "168" },
                        new QuickCommandVariable { Name = "ip_c", Value = "0" },
                        new QuickCommandVariable { Name = "ip_d", Value = "10" }
                    ]
                }
            ]
        });

        QuickCommandVariableItemViewModel variable =
            Assert.Single(command.SelectedVariableSet!.Variables, item => item.Name == "ip_d");
        variable.Value = "20";

        Assert.Equal("$SETIP,192,168,1,1", command.Payload);
        Assert.Equal("$SETIP,192,168,0,20", command.ResolvedPayload);

        command.Payload = "$SETIP,10,0,0,1";

        Assert.Equal("$SETIP,10,0,0,1", command.Payload);
        Assert.Equal("$SETIP,192,168,0,20", command.ResolvedPayload);

        QuickCommand saved = command.ToModel();
        Assert.Equal("$SETIP,10,0,0,1", saved.Payload);
        Assert.Equal("$SETIP,${ip_a},${ip_b},${ip_c},${ip_d}", saved.Template);
        Assert.Equal(command.SelectedVariableSet!.Id, saved.SelectedVariableSetId);
    }

    [Fact]
    public void TemplateEditingKeepsOnlyCurrentDollarBraceVariables()
    {
        QuickCommandItemViewModel command = new(new QuickCommand
        {
            Template = "$SETPOWER,BUCS10V5,${开关}",
            VariableSets =
            [
                new QuickCommandVariableSet
                {
                    Name = "默认",
                    Variables =
                    [
                        new QuickCommandVariable { Name = "开关", Value = "1" },
                        new QuickCommandVariable { Name = "历史参数", Value = "unused" }
                    ]
                }
            ]
        });

        QuickCommandVariableItemViewModel initial = Assert.Single(command.SelectedVariableSet!.Variables);
        Assert.Equal("开关", initial.Name);
        Assert.Equal("1", initial.Value);

        command.Template = "$SETPOWER,BUCS10V5,${开关sfd}";
        command.Template = "$SETPOWER,BUCS10V5,${开关sfdsdf},&{legacy}";

        QuickCommandVariableItemViewModel final = Assert.Single(command.SelectedVariableSet.Variables);
        Assert.Equal("开关sfdsdf", final.Name);
        Assert.Equal(string.Empty, final.Value);
        Assert.Equal("$SETPOWER,BUCS10V5,,&{legacy}", command.ResolvedPayload);
    }

    [Fact]
    public void TemplateSynchronizationPreservesValuesForVariablesThatStillExist()
    {
        QuickCommandItemViewModel command = new(new QuickCommand
        {
            Template = "$SET,${first},${second}",
            VariableSets =
            [
                new QuickCommandVariableSet
                {
                    Variables =
                    [
                        new QuickCommandVariable { Name = "first", Value = "A" },
                        new QuickCommandVariable { Name = "second", Value = "B" }
                    ]
                }
            ]
        });

        command.Template = "$SET,${second},${third}";

        Assert.Equal(["second", "third"], command.SelectedVariableSet!.Variables.Select(variable => variable.Name));
        Assert.Equal("B", command.SelectedVariableSet.Variables[0].Value);
        Assert.Equal(string.Empty, command.SelectedVariableSet.Variables[1].Value);
    }

    [Fact]
    public async Task ImportsAndExportsDeviceProfileJson()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "device.json");
            DeviceProfileFileService service = new();
            QuickCommandVariableSet variableSet = new()
            {
                Name = "通道 2",
                Variables = [new QuickCommandVariable { Name = "channel", Value = "02" }]
            };
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
                                Payload = "$START,1",
                                Template = "$START,${channel}",
                                UsageCount = 8,
                                NameColumnWeight = 110,
                                PayloadColumnWeight = 322,
                                VariableSets = [variableSet],
                                SelectedVariableSetId = variableSet.Id
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
            QuickCommand importedCommand = imported.CommandGroups[0].Commands[0];
            Assert.Equal("$START,1", importedCommand.Payload);
            Assert.Equal("$START,${channel}", importedCommand.Template);
            Assert.Equal(variableSet.Id, importedCommand.SelectedVariableSetId);
            QuickCommandVariableSet importedSet = Assert.Single(importedCommand.VariableSets);
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
    public async Task ProfileStoreUsesWorkspaceNameAndRemovesPreviousFileAfterRename()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            JsonDeviceProfileStore store = new(directory);
            DeviceProfile profile = new() { Name = "串口:测试/设备" };

            await store.SaveAsync(profile);

            string firstPath = Path.Combine(directory, "串口_测试_设备.json");
            Assert.True(File.Exists(firstPath));

            await store.SaveAsync(profile with { Name = "改名后的设备" });

            string renamedPath = Path.Combine(directory, "改名后的设备.json");
            Assert.True(File.Exists(renamedPath));
            Assert.False(File.Exists(firstPath));
            Assert.Single(Directory.EnumerateFiles(directory, "*.json"));
            Assert.Equal("改名后的设备", Assert.Single(await store.LoadAllAsync()).Name);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ProfileStoreIgnoresUnrelatedJsonFiles()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "settings.json"),
                "{\"ProfileDirectory\":\"D:\\\\project\\\\settings\",\"TerminalFontSize\":14}");
            JsonDeviceProfileStore store = new(directory);
            DeviceProfile profile = new() { Name = "设备配置" };
            await store.SaveAsync(profile);

            IReadOnlyList<DeviceProfile> loaded = await store.LoadAllAsync();

            Assert.Equal(profile.Id, Assert.Single(loaded).Id);
            Assert.True(File.Exists(Path.Combine(directory, "settings.json")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ProfileStoreMigratesExistingGuidFileNameWhenLoading()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            JsonDeviceProfileStore store = new(directory);
            DeviceProfile profile = new() { Name = "历史设备" };
            await store.SaveAsync(profile);

            string workspacePath = Path.Combine(directory, "历史设备.json");
            string legacyPath = Path.Combine(directory, $"{profile.Id:N}.json");
            File.Move(workspacePath, legacyPath);

            DeviceProfile loaded = Assert.Single(await store.LoadAllAsync());

            Assert.Equal(profile.Id, loaded.Id);
            Assert.True(File.Exists(workspacePath));
            Assert.False(File.Exists(legacyPath));
        }
        finally
        {
            Directory.Delete(directory, true);
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
            ["移相器"] = 62,
            ["C02"] = 89,
            ["E90新模块"] = 6,
            ["ESP32lyrat"] = 34,
            ["ka变频器"] = 45,
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

    /// <summary>
    /// @brief 验证配置存储与导入服务对空字符串可空 GUID 及时间戳的兼容解析
    /// </summary>
    [Fact]
    public async Task ProfileStoreAndImportTolerateEmptyNullableGuidAndDateTimeOffset()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string filePath = Path.Combine(directory, "容错设备.json");
            string json = """
                {
                  "SchemaVersion": 1,
                  "Id": "7bdb85f2-790a-4f9b-8447-e3048a0f6271",
                  "Name": "容错设备",
                  "WorkspaceMode": "Serial",
                  "Transport": {
                    "$type": "serial",
                    "PortName": "COM1",
                    "BaudRate": 115200,
                    "Kind": "Serial"
                  },
                  "CommandGroups": [
                    {
                      "Name": "串口指令",
                      "Category": "Serial",
                      "Commands": [
                        {
                          "Name": "空GUID引用命令",
                          "Payload": "hmt+voice=info",
                          "Template": "hmt+voice=info",
                          "LastUsedAt": "",
                          "Variables": [],
                          "VariableSets": [],
                          "SelectedVariableSetId": ""
                        }
                      ]
                    }
                  ]
                }
                """;
            await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);

            JsonDeviceProfileStore store = new(directory);
            IReadOnlyList<DeviceProfile> loadedProfiles = await store.LoadAllAsync();

            DeviceProfile loaded = Assert.Single(loadedProfiles);
            Assert.Empty(store.LastLoadWarnings);
            QuickCommand loadedCommand = Assert.Single(loaded.CommandGroups[0].Commands);
            Assert.Equal("空GUID引用命令", loadedCommand.Name);
            Assert.Null(loadedCommand.SelectedVariableSetId);
            Assert.Null(loadedCommand.LastUsedAt);

            DeviceProfile imported = await new DeviceProfileFileService().ImportAsync(filePath);
            QuickCommand importedCommand = Assert.Single(imported.CommandGroups[0].Commands);
            Assert.NotEqual(loaded.Id, imported.Id);
            Assert.Null(importedCommand.SelectedVariableSetId);
            Assert.Null(importedCommand.LastUsedAt);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// @brief 验证损坏的配置 JSON 会在加载后记录警告信息，且不影响无关 JSON 文件
    /// </summary>
    [Fact]
    public async Task ProfileStoreRecordsWarningsForMalformedProfileJson()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "settings.json"),
                "{\"TerminalFontSize\":14}");
            await File.WriteAllTextAsync(
                Path.Combine(directory, "损坏设备.json"),
                """
                {
                  "SchemaVersion": 1,
                  "Id": "7bdb85f2-790a-4f9b-8447-e3048a0f6271",
                  "Name": "损坏设备",
                  "Transport": { "$type": "serial", "Kind": "Serial" },
                  "CommandGroups": [
                    {
                      "Name": "串口指令",
                      "Category": "Serial",
                      "Commands": [
                        {
                          "Name": "非法GUID",
                          "SelectedVariableSetId": "not-a-guid"
                        }
                      ]
                    }
                  ]
                }
                """);

            JsonDeviceProfileStore store = new(directory);
            IReadOnlyList<DeviceProfile> loaded = await store.LoadAllAsync();

            Assert.Empty(loaded);
            string warning = Assert.Single(store.LastLoadWarnings);
            Assert.Contains("损坏设备.json", warning);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// @brief 验证导入设备配置 JSON 后自动切换到新导入的设备
    /// </summary>
    [Fact]
    public async Task ImportDeviceProfilesSelectsLastImportedProfile()
    {
        string directory = CreateTemporaryDirectory();
        string exportPath = Path.Combine(directory, "待导入设备.json");
        string storeDirectory = Path.Combine(directory, "Store");
        Directory.CreateDirectory(storeDirectory);

        DeviceProfileFileService fileService = new();
        DeviceProfile existing = new() { Name = "原有设备" };
        DeviceProfile incoming = new()
        {
            Name = "新导入设备",
            CommandGroups =
            [
                new QuickCommandGroup
                {
                    Category = QuickCommandCategory.Serial,
                    Commands = [new QuickCommand { Name = "状态查询", Payload = "AT", LineEnding = "CRLF" }]
                }
            ]
        };
        JsonDeviceProfileStore store = new(storeDirectory);
        await store.SaveAsync(existing);
        await fileService.ExportAsync(incoming, exportPath);

        using OnlineUpdateService updateService = new(currentVersion: new Version(1, 0, 0));
        MainWindowViewModel viewModel = new(
            store,
            new LegacyConfigImporter(),
            new AppSettingsStore(Path.Combine(directory, "settings.json")),
            fileService,
            new CaptureFileReader(),
            updateService,
            new BleDiscoveryService(),
            new BleGattBrowserService());

        try
        {
            // 先选中原有设备，模拟软件运行中已打开其它设备配置。
            viewModel.Profiles.Add(existing);
            viewModel.SelectedProfile = existing;
            Assert.Equal(existing.Id, viewModel.SelectedProfile.Id);

            await viewModel.ImportDeviceProfilesAsync([exportPath]);

            Assert.NotNull(viewModel.SelectedProfile);
            Assert.Equal("新导入设备", viewModel.SelectedProfile!.Name);
            QuickCommandItemViewModel command = Assert.Single(viewModel.QuickCommands);
            Assert.Equal("状态查询", command.Name);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// @brief 验证打开配置文件夹时能够定位当前选中设备的 JSON 文件并在未选中时回退到目录本身
    /// </summary>
    [Fact]
    public async Task ResolveProfileFolderTargetAndStartInfoMatchesSelectedAndEmptyStates()
    {
        string directory = CreateTemporaryDirectory();
        string storeDirectory = Path.Combine(directory, "Profiles");
        DeviceProfile profile = new() { Name = "测试定位设备" };
        JsonDeviceProfileStore store = new(storeDirectory);
        await store.SaveAsync(profile);

        using OnlineUpdateService updateService = new(currentVersion: new Version(1, 0, 0));
        MainWindowViewModel viewModel = new(
            store,
            new LegacyConfigImporter(),
            new AppSettingsStore(Path.Combine(directory, "settings.json")),
            new DeviceProfileFileService(),
            new CaptureFileReader(),
            updateService,
            new BleDiscoveryService(),
            new BleGattBrowserService());

        try
        {
            // 选中已保存设备时应解析到具体 JSON 文件并使用 /select 定位。
            viewModel.Profiles.Add(profile);
            viewModel.SelectedProfile = profile;
            (string resolvedDir, string? selectedFile) = await viewModel.ResolveProfileFolderTargetAsync();
            Assert.Equal(Path.GetFullPath(storeDirectory), resolvedDir);
            Assert.NotNull(selectedFile);
            Assert.True(File.Exists(selectedFile));
            Assert.Equal("测试定位设备.json", Path.GetFileName(selectedFile));

            ProcessStartInfo selectInfo = MainWindowViewModel.BuildProfileFolderStartInfo(resolvedDir, selectedFile);
            Assert.Equal("explorer.exe", selectInfo.FileName);
            Assert.Equal($"/select,\"{selectedFile}\"", selectInfo.Arguments);
            Assert.True(selectInfo.UseShellExecute);

            // 未选中任何设备且目录被删后，应自动重建目录并直接打开文件夹。
            viewModel.SelectedProfile = null;
            Directory.Delete(storeDirectory, true);
            (string fallbackDir, string? fallbackFile) = await viewModel.ResolveProfileFolderTargetAsync();
            Assert.Equal(Path.GetFullPath(storeDirectory), fallbackDir);
            Assert.True(Directory.Exists(fallbackDir));
            Assert.Null(fallbackFile);

            ProcessStartInfo folderInfo = MainWindowViewModel.BuildProfileFolderStartInfo(fallbackDir, fallbackFile);
            Assert.Equal(fallbackDir, folderInfo.FileName);
            Assert.True(string.IsNullOrEmpty(folderInfo.Arguments));
            Assert.True(folderInfo.UseShellExecute);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "DeviceDebugStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
