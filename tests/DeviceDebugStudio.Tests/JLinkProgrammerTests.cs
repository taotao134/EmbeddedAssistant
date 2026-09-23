using System.Text.Json;
using DeviceDebugStudio.App.ViewModels;
using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Infrastructure.Programming;

namespace DeviceDebugStudio.Tests;

public sealed class JLinkProgrammerTests
{
    [Fact]
    public async Task BinaryScriptUsesFlashAddressAndVerifyBin()
    {
        string directory = CreateTemporaryDirectory("j link");
        try
        {
            string firmware = Path.Combine(directory, "firmware image.bin");
            await File.WriteAllBytesAsync(firmware, [0x01, 0x02, 0x03]);
            string script = JLinkProgrammer.BuildCommanderScript(new JLinkProgrammingRequest
            {
                FirmwareFile = firmware,
                FlashAddress = 0x0800_4000
            });

            Assert.Contains($"loadbin \"{firmware}\", 0x08004000", script);
            Assert.Contains($"verifybin \"{firmware}\", 0x08004000", script);
            Assert.Contains("erase", script);
            Assert.EndsWith("exit\r\n", script);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HexScriptDoesNotAddBinaryAddress()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string firmware = Path.Combine(directory, "firmware.hex");
            await File.WriteAllTextAsync(firmware, ":00000001FF\r\n");
            string script = JLinkProgrammer.BuildCommanderScript(new JLinkProgrammingRequest
            {
                FirmwareFile = firmware,
                EraseBeforeProgramming = false,
                Verify = true,
                ResetAfterProgramming = false,
                RunAfterProgramming = false
            });

            Assert.Contains($"loadfile \"{firmware}\"\r\n", script);
            Assert.Contains($"verify \"{firmware}\"\r\n", script);
            Assert.DoesNotContain("erase", script);
            Assert.DoesNotContain("verifybin", script);
            Assert.DoesNotContain("\nr\n", script);
            Assert.EndsWith("exit\r\n", script);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("0x08000000", 0x08000000u)]
    [InlineData("08004000", 0x08004000u)]
    [InlineData("FFFFFFFF", 0xFFFFFFFFu)]
    public void ParsesHexFlashAddress(string text, uint expected)
    {
        Assert.Equal(expected, JLinkProgrammerViewModel.ParseFlashAddress(text));
    }

    [Fact]
    public void ProfileJsonKeepsJLinkPreferences()
    {
        DeviceProfile profile = new()
        {
            WorkspaceMode = WorkspaceMode.JLink,
            JLink = new JLinkPreferences
            {
                ExecutablePath = @"C:\SEGGER\JLink\JLink.exe",
                Device = "STM32F407VG",
                InterfaceName = "JTAG",
                SpeedKHz = 12000,
                FirmwareFile = @"D:\firmware\app.hex",
                FlashAddress = 0x08010000,
                EraseBeforeProgramming = false,
                Verify = false,
                ResetAfterProgramming = false,
                RunAfterProgramming = false
            }
        };

        string json = JsonSerializer.Serialize(profile);
        DeviceProfile loaded = Assert.IsType<DeviceProfile>(JsonSerializer.Deserialize<DeviceProfile>(json));

        Assert.Equal(WorkspaceMode.JLink, loaded.WorkspaceMode);
        Assert.Equal(profile.JLink, loaded.JLink);
    }

    [Fact]
    public void TargetParserReadsStm32IdFlashSizeAndCore()
    {
        const string output = """
            Cortex-M3 r2p0
            0xE0042000 = 0x10006410
            0x1FFFF7E0 = 0x00000040
            """;

        JLinkTargetInfo info = JLinkTargetInfoParser.Parse(output, "STM32F103C8");

        Assert.Equal(0x410u, info.DeviceId);
        Assert.Equal("STM32F1", info.DeviceFamily);
        Assert.StartsWith("STM32F103x8", info.DetectedDevice);
        Assert.Equal("CORTEX-M3", info.CoreName);
        Assert.Equal(64 * 1024u, info.FlashSizeBytes);
        Assert.Equal(64, info.FlashSectors.Count);
        Assert.Equal(0x08000000u, info.FlashSectors[0].StartAddress);
        Assert.Equal(0x08000400u, info.FlashSectors[0].EndAddressExclusive);
    }

    [Fact]
    public void TargetParserReadsStm32H7RegistersAndSelectedDevice()
    {
        const string output = """
            Cortex-M7 r1p0
            Device "STM32H743VI" selected.
            0x5C001000 = 0x20006483
            0x1FF1E880 = 0x00000800
            """;

        JLinkTargetInfo info = JLinkTargetInfoParser.Parse(output, "STM32H743VIT6");

        Assert.Equal(0x483u, info.DeviceId);
        Assert.Equal("STM32H7", info.DeviceFamily);
        Assert.StartsWith("STM32H743VI", info.DetectedDevice);
        Assert.Equal("CORTEX-M7", info.CoreName);
        Assert.Equal(2048 * 1024u, info.FlashSizeBytes);
        Assert.Equal(16, info.FlashSectors.Count);
        Assert.Equal(0x08000000u, info.FlashSectors[0].StartAddress);
        Assert.Equal(0x08020000u, info.FlashSectors[0].EndAddressExclusive);
    }

    [Theory]
    [InlineData("STM32H743VIT6", "STM32H743VI")]
    [InlineData("stm32f407vgt6", "STM32F407VG")]
    [InlineData("STM32H743VITx", "STM32H743VITx")]
    [InlineData("STM32F103C8", "STM32F103C8")]
    public void ResolvesStm32OrderCodeToJLinkDeviceName(string configured, string expected)
    {
        Assert.Equal(expected, JLinkProgrammer.ResolveDeviceName(configured));
    }

    [Fact]
    public void DoesNotTreatJLinkConnectNoticeAsFailure()
    {
        const string successfulOutput = """
            J-Link connection not established yet but required for command.
            Connecting to J-Link via USB...O.K.
            Found SW-DP with ID 0x6BA02477
            """;

        Assert.False(JLinkProgrammer.ContainsCommanderError(successfulOutput));
        Assert.False(JLinkProgrammer.ContainsCommanderError("ERROR: Could not read memory at 0x1FFF7A22"));
        Assert.True(JLinkProgrammer.ContainsCommanderError("FAILED: Cannot connect to J-Link"));
    }

    [Fact]
    public void F4FlashLayoutUsesStandardVariableSectorSizes()
    {
        IReadOnlyList<JLinkFlashSector> sectors = JLinkTargetInfoParser.BuildFlashSectors("STM32F4", 1024 * 1024u);

        Assert.Equal(12, sectors.Count);
        Assert.Equal(16 * 1024u, sectors[0].SizeBytes);
        Assert.Equal(16 * 1024u, sectors[3].SizeBytes);
        Assert.Equal(64 * 1024u, sectors[4].SizeBytes);
        Assert.Equal(128 * 1024u, sectors[5].SizeBytes);
        Assert.Equal(0x08100000u, sectors[^1].EndAddressExclusive);
    }

    [Fact]
    public async Task ScriptMergesSelectedEraseRanges()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string firmware = Path.Combine(directory, "firmware.bin");
            await File.WriteAllBytesAsync(firmware, [0x01]);
            string script = JLinkProgrammer.BuildCommanderScript(new JLinkProgrammingRequest
            {
                FirmwareFile = firmware,
                EraseRanges =
                [
                    new JLinkEraseRange(0x08001000, 0x08002000),
                    new JLinkEraseRange(0x08000000, 0x08001000),
                    new JLinkEraseRange(0x08004000, 0x08005000)
                ],
                ResetAfterProgramming = false,
                RunAfterProgramming = false
            });

            Assert.Contains("erase 0x08000000 0x08001FFF", script);
            Assert.Contains("erase 0x08004000 0x08004FFF", script);
            Assert.DoesNotContain("erase\r\n", script);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory(string suffix = "")
    {
        string path = Path.Combine(Path.GetTempPath(), $"DeviceDebugStudio-JLinkTests-{Guid.NewGuid():N}{suffix}");
        Directory.CreateDirectory(path);
        return path;
    }
}
