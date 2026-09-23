using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeviceDebugStudio.Infrastructure.Programming;

public sealed record JLinkConnectionRequest
{
    public string ExecutablePath { get; init; } = string.Empty;
    public string Device { get; init; } = "STM32F103C8";
    public bool AutoDetectTarget { get; init; } = true;
    public string InterfaceName { get; init; } = "SWD";
    public int SpeedKHz { get; init; } = 4000;
}

public sealed record JLinkEraseRange(uint StartAddress, uint EndAddressExclusive);

public sealed record JLinkFlashSector(
    int Index,
    uint StartAddress,
    uint EndAddressExclusive,
    string Label)
{
    public uint SizeBytes => EndAddressExclusive > StartAddress
        ? EndAddressExclusive - StartAddress
        : 0;
}

public sealed record JLinkTargetInfo
{
    public string RequestedDevice { get; init; } = string.Empty;
    public string DetectedDevice { get; init; } = "未识别";
    public string DeviceFamily { get; init; } = string.Empty;
    public string CoreName { get; init; } = string.Empty;
    public uint? DeviceId { get; init; }
    public uint? FlashSizeBytes { get; init; }
    public IReadOnlyList<JLinkFlashSector> FlashSectors { get; init; } = [];
}

public sealed record JLinkConnectionResult(
    int ExitCode,
    bool Connected,
    JLinkTargetInfo? TargetInfo,
    string Output,
    TimeSpan Duration);

public sealed record JLinkProgrammingRequest
{
    public string ExecutablePath { get; init; } = string.Empty;
    public string Device { get; init; } = "STM32F103C8";
    public string InterfaceName { get; init; } = "SWD";
    public int SpeedKHz { get; init; } = 4000;
    public string FirmwareFile { get; init; } = string.Empty;
    public uint FlashAddress { get; init; } = 0x0800_0000;
    public bool EraseBeforeProgramming { get; init; } = true;
    public bool EraseRangeSelectionEnabled { get; init; }
    public IReadOnlyList<JLinkEraseRange> EraseRanges { get; init; } = [];
    public bool Verify { get; init; } = true;
    public bool ResetAfterProgramming { get; init; } = true;
    public bool RunAfterProgramming { get; init; } = true;
}

public sealed record JLinkProgrammingResult(int ExitCode, string Output, TimeSpan Duration);

public static class JLinkTargetInfoParser
{
    private static readonly IReadOnlyDictionary<uint, string> DeviceFamilies = new Dictionary<uint, string>
    {
        [0x410] = "STM32F1", [0x411] = "STM32F2", [0x412] = "STM32F1", [0x413] = "STM32F4",
        [0x414] = "STM32F1", [0x417] = "STM32L0", [0x418] = "STM32F1", [0x419] = "STM32F4",
        [0x420] = "STM32F1", [0x421] = "STM32F4", [0x423] = "STM32F4", [0x425] = "STM32F1",
        [0x427] = "STM32F1", [0x428] = "STM32F1", [0x429] = "STM32L1", [0x431] = "STM32F4",
        [0x432] = "STM32F3", [0x433] = "STM32F4", [0x435] = "STM32L4", [0x437] = "STM32F1",
        [0x438] = "STM32F1", [0x440] = "STM32F0", [0x442] = "STM32F0", [0x444] = "STM32F0",
        [0x445] = "STM32F0", [0x448] = "STM32F0", [0x450] = "STM32H7", [0x458] = "STM32F4",
        [0x460] = "STM32G0", [0x461] = "STM32G0", [0x463] = "STM32F4", [0x468] = "STM32G4",
        [0x470] = "STM32L5", [0x480] = "STM32G0", [0x481] = "STM32G0", [0x482] = "STM32U5",
        [0x483] = "STM32H7", [0x490] = "STM32G0", [0x491] = "STM32G0", [0x494] = "STM32WL",
        [0x495] = "STM32WB"
    };

    public static JLinkTargetInfo Parse(string output, string requestedDevice = "")
    {
        uint? deviceId = TryReadDeviceId(output);
        uint? flashSizeBytes = TryReadFlashSize(output);
        string family = deviceId is uint id && DeviceFamilies.TryGetValue(id, out string? knownFamily)
            ? knownFamily
            : string.Empty;
        string selectedDevice = ParseSelectedDevice(output);

        return new JLinkTargetInfo
        {
            RequestedDevice = requestedDevice?.Trim() ?? string.Empty,
            DetectedDevice = BuildDetectedDeviceName(family, deviceId, flashSizeBytes, selectedDevice),
            DeviceFamily = family,
            CoreName = ParseCoreName(output),
            DeviceId = deviceId,
            FlashSizeBytes = flashSizeBytes,
            FlashSectors = BuildFlashSectors(family, flashSizeBytes)
        };
    }

    public static IReadOnlyList<JLinkFlashSector> BuildFlashSectors(
        string family,
        uint? flashSizeBytes,
        uint flashBaseAddress = 0x0800_0000)
    {
        if (flashSizeBytes is not uint flashSize || flashSize == 0)
        {
            return [];
        }

        string normalizedFamily = family?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalizedFamily is "STM32F2" or "STM32F4" or "STM32F7")
        {
            return BuildStm32F4StyleSectors(flashBaseAddress, flashSize);
        }

        uint? pageSize = normalizedFamily switch
        {
            "STM32F0" => 1024,
            "STM32F1" when flashSize <= 128 * 1024 => 1024,
            "STM32F1" => 2048,
            "STM32G0" => 2048,
            "STM32G4" => 2048,
            "STM32H7" => 128 * 1024,
            "STM32L0" => 128,
            "STM32L1" => 256,
            "STM32L4" => 2048,
            "STM32L5" => 2048,
            "STM32U5" => 8192,
            "STM32WB" => 4096,
            "STM32WL" => 4096,
            _ => null
        };

        if (pageSize is not uint uniformPageSize || uniformPageSize == 0)
        {
            return [new JLinkFlashSector(0, flashBaseAddress, AddAddress(flashBaseAddress, flashSize), "整片 Flash")];
        }

        return BuildUniformSectors(flashBaseAddress, flashSize, uniformPageSize);
    }

    private static IReadOnlyList<JLinkFlashSector> BuildUniformSectors(
        uint flashBaseAddress,
        uint flashSize,
        uint sectorSize)
    {
        List<JLinkFlashSector> sectors = [];
        uint start = flashBaseAddress;
        uint end = AddAddress(flashBaseAddress, flashSize);
        int index = 0;
        while (start < end)
        {
            uint next = Math.Min(end, AddAddress(start, sectorSize));
            sectors.Add(new JLinkFlashSector(index, start, next, $"Sector {index}"));
            start = next;
            index++;
        }
        return sectors;
    }

    private static IReadOnlyList<JLinkFlashSector> BuildStm32F4StyleSectors(
        uint flashBaseAddress,
        uint flashSize)
    {
        List<JLinkFlashSector> sectors = [];
        uint start = flashBaseAddress;
        uint end = AddAddress(flashBaseAddress, flashSize);
        int index = 0;
        while (start < end)
        {
            uint sectorSize = index switch
            {
                < 4 => 16 * 1024u,
                4 => 64 * 1024u,
                _ => 128 * 1024u
            };
            uint next = Math.Min(end, AddAddress(start, sectorSize));
            sectors.Add(new JLinkFlashSector(index, start, next, $"Sector {index}"));
            start = next;
            index++;
        }
        return sectors;
    }

    private static string BuildDetectedDeviceName(
        string family,
        uint? deviceId,
        uint? flashSizeBytes,
        string selectedDevice)
    {
        if (deviceId is not uint id)
        {
            return "未识别（未读到 DBGMCU_IDCODE）";
        }

        string idText = $"ID 0x{id:X3}";
        string flashText = flashSizeBytes is uint bytes ? $"，Flash {bytes / 1024} KB" : string.Empty;
        string model = string.IsNullOrWhiteSpace(selectedDevice)
            ? ResolveModelName(family, id, flashSizeBytes)
            : selectedDevice;
        return string.IsNullOrWhiteSpace(family)
            ? $"STM32 未知系列（{idText}{flashText}）"
            : $"{model}（{idText}{flashText}）";
    }

    private static string ResolveModelName(string family, uint deviceId, uint? flashSizeBytes)
    {
        uint flashKilobytes = flashSizeBytes is uint bytes ? bytes / 1024 : 0;
        return (family, deviceId) switch
        {
            ("STM32F1", 0x410) when flashKilobytes <= 64 => "STM32F103x8",
            ("STM32F1", 0x410) => "STM32F103xB",
            ("STM32F1", 0x414) when flashKilobytes <= 64 => "STM32F101x8/F103x8",
            ("STM32F1", 0x414) => "STM32F101xB/F103xB",
            ("STM32F4", 0x413) => "STM32F405/407xx",
            ("STM32F4", 0x423) => "STM32F401xx",
            ("STM32F4", 0x431) => "STM32F411xx",
            ("STM32F4", 0x421) => "STM32F446xx",
            _ => family
        };
    }

    private static string ParseCoreName(string output)
    {
        Match match = Regex.Match(output ?? string.Empty, @"Cortex-[A-Za-z0-9+]+", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : string.Empty;
    }

    private static string ParseSelectedDevice(string output)
    {
        Match match = Regex.Match(
            output ?? string.Empty,
            @"(?im)\bDevice\s+""(?<device>[^""]+)""\s+selected\.");
        return match.Success ? match.Groups["device"].Value.Trim() : string.Empty;
    }

    private static uint? TryReadDeviceId(string output)
    {
        uint? fallback = null;
        foreach (uint address in new[] { 0x5C00_1000u, 0xE004_2000u })
        {
            if (!TryReadRegister(output, address, out uint raw))
            {
                continue;
            }

            uint id = raw & 0x0FFF;
            if (DeviceFamilies.ContainsKey(id))
            {
                return id;
            }

            fallback ??= id;
        }
        return fallback;
    }

    private static uint? TryReadFlashSize(string output)
    {
        foreach (uint address in new[]
        {
            0x1FF1_E880u,
            0x1FFF_7A22u,
            0x1FFF_75E0u,
            0x1FFF_F7E0u,
            0x1FFF_F7CCu
        })
        {
            if (!TryReadRegister(output, address, out uint raw))
            {
                continue;
            }

            uint kilobytes = raw & 0xFFFF;
            if (kilobytes is > 0 and <= 8192)
            {
                return kilobytes * 1024;
            }
        }
        return null;
    }

    private static bool TryReadRegister(string output, uint address, out uint value)
    {
        value = 0;
        string addressText = address.ToString("X8", CultureInfo.InvariantCulture);
        Match match = Regex.Match(
            output ?? string.Empty,
            $@"(?im)(?:0x)?{addressText}\s*(?:=|:)\s*(?:0x)?([0-9a-f]+)");
        return match.Success
            && uint.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static uint AddAddress(uint address, uint offset)
    {
        ulong result = (ulong)address + offset;
        return result > uint.MaxValue ? uint.MaxValue : (uint)result;
    }
}

public sealed class JLinkProgrammer : IAsyncDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private JLinkConnectionRequest? _connectionRequest;
    private bool _connected;
    private int _disposed;

    public bool IsConnected => _connected;

    public async Task<JLinkConnectionResult> ConnectAsync(
        JLinkConnectionRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionRequest(request);
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        try
        {
            string script = request.AutoDetectTarget
                ? BuildTargetInfoScript(request.Device)
                : "connect" + Environment.NewLine + "exit" + Environment.NewLine;
            CommanderRunResult run = await RunCommanderAsync(
                request,
                script,
                progress,
                cancellationToken,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            bool connected = run.ExitCode == 0 && !ContainsCommanderError(run.Output);
            if (!connected)
            {
                _connected = false;
                _connectionRequest = null;
                throw new InvalidOperationException("J-Link 无法连接目标，请检查探针、供电和 SWD/JTAG 接线。 ");
            }

            JLinkTargetInfo targetInfo = JLinkTargetInfoParser.Parse(run.Output, request.Device);
            _connected = true;
            _connectionRequest = request;
            return new JLinkConnectionResult(
                run.ExitCode,
                true,
                targetInfo,
                run.Output,
                DateTimeOffset.UtcNow - startedAt);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<JLinkTargetInfo> ReadTargetInfoAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_connected || _connectionRequest is null)
            {
                throw new InvalidOperationException("请先连接 J-Link。 ");
            }

            CommanderRunResult run = await RunCommanderAsync(
                _connectionRequest,
                BuildTargetInfoScript(_connectionRequest.Device),
                progress,
                cancellationToken,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (run.ExitCode != 0 || ContainsCommanderError(run.Output))
            {
                _connected = false;
                throw new InvalidOperationException("读取目标信息失败，目标连接已断开。 ");
            }
            return JLinkTargetInfoParser.Parse(run.Output, _connectionRequest.Device);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _connected = false;
            _connectionRequest = null;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<JLinkProgrammingResult> ProgramAsync(
        JLinkProgrammingRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ProgramOneShotAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public static string BuildCommanderScript(JLinkProgrammingRequest request)
    {
        ValidateRequest(request);
        string firmwarePath = request.FirmwareFile.Trim();
        string firmware = QuoteScriptValue(Path.GetFullPath(firmwarePath));
        bool binary = string.Equals(Path.GetExtension(firmwarePath), ".bin", StringComparison.OrdinalIgnoreCase);
        StringBuilder script = new();
        script.AppendLine("r");
        script.AppendLine("h");
        if (request.EraseBeforeProgramming)
        {
            IReadOnlyList<JLinkEraseRange> ranges = NormalizeEraseRanges(request.EraseRanges);
            if (request.EraseRangeSelectionEnabled && ranges.Count == 0)
            {
                throw new ArgumentException("已启用扇区擦除，但没有选择任何扇区。 ", nameof(request));
            }
            if (ranges.Count == 0)
            {
                script.AppendLine("erase");
            }
            else
            {
                foreach (JLinkEraseRange range in ranges)
                {
                    script.Append("erase ")
                        .Append(FormatAddress(range.StartAddress))
                        .Append(' ')
                        .Append(FormatAddress(range.EndAddressExclusive - 1))
                        .AppendLine();
                }
            }
        }

        script.Append(binary ? "loadbin " : "loadfile ").Append(firmware);
        if (binary)
        {
            script.Append(", ").Append(FormatAddress(request.FlashAddress));
        }
        script.AppendLine();
        if (request.Verify)
        {
            script.Append(binary ? "verifybin " : "verify ").Append(firmware);
            if (binary)
            {
                script.Append(", ").Append(FormatAddress(request.FlashAddress));
            }
            script.AppendLine();
        }
        if (request.ResetAfterProgramming)
        {
            script.AppendLine("r");
        }
        if (request.RunAfterProgramming)
        {
            script.AppendLine("g");
        }
        script.AppendLine("exit");
        return script.ToString();
    }

    public static IReadOnlyList<JLinkEraseRange> NormalizeEraseRanges(IEnumerable<JLinkEraseRange> ranges)
    {
        List<JLinkEraseRange> normalized = ranges
            .Where(range => range.EndAddressExclusive > range.StartAddress)
            .OrderBy(range => range.StartAddress)
            .ThenBy(range => range.EndAddressExclusive)
            .ToList();
        if (normalized.Count == 0)
        {
            return [];
        }

        List<JLinkEraseRange> merged = [normalized[0]];
        foreach (JLinkEraseRange range in normalized.Skip(1))
        {
            JLinkEraseRange previous = merged[^1];
            if (range.StartAddress <= previous.EndAddressExclusive)
            {
                merged[^1] = previous with
                {
                    EndAddressExclusive = Math.Max(previous.EndAddressExclusive, range.EndAddressExclusive)
                };
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }

    public static string ResolveExecutablePath(string configuredPath)
    {
        string value = configuredPath?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (File.Exists(value))
            {
                return Path.GetFullPath(value);
            }
            if (Path.IsPathRooted(value))
            {
                throw new FileNotFoundException("找不到指定的 J-Link 工具。 ", value);
            }
            return value;
        }

        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (string root in new[] { programFiles, programFilesX86 }.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            string seggerRoot = Path.Combine(root, "SEGGER");
            IEnumerable<string> directories = Directory.Exists(seggerRoot)
                ? Directory.EnumerateDirectories(seggerRoot, "JLink*")
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                : [];
            foreach (string directory in directories.Prepend(Path.Combine(seggerRoot, "JLink")))
            {
                string candidate = Path.Combine(directory, "JLink.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return "JLink.exe";
    }

    public static string ResolveDeviceName(string configuredDevice)
    {
        string value = configuredDevice?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return "STM32F103C8";
        }

        // ST 完整订货号通常以 T6/H6 等封装和温度后缀结尾，SEGGER 数据库使用不带该后缀的型号。
        string normalized = value.ToUpperInvariant();
        if (normalized.StartsWith("STM32", StringComparison.Ordinal)
            && Regex.IsMatch(normalized, @"^STM32[A-Z0-9]+[THUIY][0-9]$"))
        {
            return normalized[..^2];
        }

        return value;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _connected = false;
            _connectionRequest = null;
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    private static string BuildTargetInfoScript(string configuredDevice) => string.Join(
        Environment.NewLine,
        IsStm32H7Device(configuredDevice)
            ? [
                "connect",
                "mem32 0x5C001000 1",
                "mem32 0x1FF1E880 1",
                "exit"
            ]
            : [
                "connect",
                "mem32 0xE0042000 1",
                "mem32 0x1FFF7A22 1",
                "mem32 0x1FFF75E0 1",
                "mem32 0x1FFFF7E0 1",
                "mem32 0x1FFFF7CC 1",
                "exit"
            ]) + Environment.NewLine;

    private static bool IsStm32H7Device(string configuredDevice) =>
        ResolveDeviceName(configuredDevice).StartsWith("STM32H7", StringComparison.OrdinalIgnoreCase);

    private static async Task<CommanderRunResult> RunCommanderAsync(
        JLinkConnectionRequest request,
        string script,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        string executable = ResolveExecutablePath(request.ExecutablePath);
        string scriptPath = Path.Combine(Path.GetTempPath(), $"DeviceDebugStudio-{Guid.NewGuid():N}.jlink");
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        using CancellationTokenSource timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        CancellationToken processCancellation = timeoutCancellation.Token;
        try
        {
            await File.WriteAllTextAsync(scriptPath, script, Utf8NoBom, processCancellation).ConfigureAwait(false);
            ProcessStartInfo startInfo = new()
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = GetWorkingDirectory(executable),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom
            };
            string configuredDevice = request.Device ?? string.Empty;
            string deviceName = ResolveDeviceName(configuredDevice);
            if (!string.Equals(deviceName, configuredDevice.Trim(), StringComparison.Ordinal))
            {
                progress?.Report($"设备型号已规范化：{configuredDevice.Trim()} -> {deviceName}");
            }

            startInfo.ArgumentList.Add("-device");
            startInfo.ArgumentList.Add(deviceName);
            startInfo.ArgumentList.Add("-if");
            startInfo.ArgumentList.Add(request.InterfaceName.Trim());
            startInfo.ArgumentList.Add("-speed");
            startInfo.ArgumentList.Add(request.SpeedKHz.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-autoconnect");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-NoGui");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-ExitOnError");
            startInfo.ArgumentList.Add("0");
            startInfo.ArgumentList.Add("-CommandFile");
            startInfo.ArgumentList.Add(scriptPath);

            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 J-Link 命令行工具。 ");
            }

            StringBuilder output = new();
            Task stdoutTask = ReadOutputAsync(process.StandardOutput, output, progress, processCancellation);
            Task stderrTask = ReadOutputAsync(process.StandardError, output, progress, processCancellation);
            try
            {
                await process.WaitForExitAsync(processCancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                }
                throw;
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
            {
                TryKill(process);
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
                throw new TimeoutException($"J-Link 操作超过 {timeout.TotalSeconds:F0} 秒未完成。 ");
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return new CommanderRunResult(process.ExitCode, output.ToString(), DateTimeOffset.UtcNow - startedAt);
        }
        finally
        {
            TryDelete(scriptPath);
        }
    }

    private static async Task<JLinkProgrammingResult> ProgramOneShotAsync(
        JLinkProgrammingRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        JLinkConnectionRequest connectionRequest = new()
        {
            ExecutablePath = request.ExecutablePath,
            Device = request.Device,
            InterfaceName = request.InterfaceName,
            SpeedKHz = request.SpeedKHz
        };
        CommanderRunResult run = await RunCommanderAsync(
            connectionRequest,
            BuildCommanderScript(request),
            progress,
            cancellationToken,
            TimeSpan.FromMinutes(15)).ConfigureAwait(false);
        return new JLinkProgrammingResult(run.ExitCode, run.Output, run.Duration);
    }

    private static void ValidateConnectionRequest(JLinkConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.InterfaceName))
        {
            throw new ArgumentException("调试接口不能为空。 ", nameof(request));
        }
        if (request.SpeedKHz is < 1 or > 50_000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "J-Link 速度必须在 1~50000 kHz 之间。 ");
        }
    }

    private static void ValidateRequest(JLinkProgrammingRequest request)
    {
        ValidateConnectionRequest(new JLinkConnectionRequest
        {
            Device = request.Device,
            InterfaceName = request.InterfaceName,
            SpeedKHz = request.SpeedKHz
        });
        if (string.IsNullOrWhiteSpace(request.FirmwareFile) || !File.Exists(request.FirmwareFile.Trim()))
        {
            throw new FileNotFoundException("找不到要烧录的固件文件。 ", request.FirmwareFile);
        }
        foreach (JLinkEraseRange range in request.EraseRanges)
        {
            if (range.EndAddressExclusive <= range.StartAddress)
            {
                throw new ArgumentException("擦除范围的结束地址必须大于起始地址。 ", nameof(request));
            }
        }
    }

    private static string FormatAddress(uint address) => $"0x{address:X8}";
    private static string QuoteScriptValue(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    public static bool ContainsCommanderError(string output) => Regex.IsMatch(
        output ?? string.Empty,
        @"(?im)(?:\bFAILED:\s*(?:Failed to (?:connect|open|start)|Cannot (?:connect|open)|Could not (?:connect|open)|Unable to (?:connect|open)|No (?:J-Link|emulator|target|device))|\b(?:Failed|Cannot|Could not|Unable to)\s+(?:connect|open|start)\b|\bNo\s+(?:J-Link|emulator|target|device)\b)");

    private static async Task ReadOutputAsync(
        StreamReader reader,
        StringBuilder output,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lock (output)
                {
                    output.AppendLine(line);
                }
                progress?.Report(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string GetWorkingDirectory(string executable)
    {
        try
        {
            string? directory = Path.GetDirectoryName(executable);
            return string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory;
        }
        catch (ArgumentException)
        {
            return Environment.CurrentDirectory;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(JLinkProgrammer));
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record CommanderRunResult(int ExitCode, string Output, TimeSpan Duration);
}
