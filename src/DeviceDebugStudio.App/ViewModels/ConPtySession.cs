using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeviceDebugStudio.App.ViewModels;

/// <summary>
/// 为交互式命令创建 Windows ConPTY 会话，使子进程能够检测到真实终端。
/// </summary>
internal sealed class ConPtySession : IAsyncDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x0008_0000;
    private const uint CreateUnicodeEnvironment = 0x0000_0400;
    private const uint Infinite = 0xFFFF_FFFF;
    private const uint WaitObjectSignaled = 0;
    private const uint HandleFlagInherit = 0x0000_0001;
    private const int ProcThreadAttributePseudoConsole = 0x0002_0016;

    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly IntPtr _inputReadHandle;
    private readonly IntPtr _outputWriteHandle;
    private readonly IntPtr _processHandle;
    private readonly IntPtr _pseudoConsole;
    private int _disposed;

    private ConPtySession(
        FileStream input,
        FileStream output,
        IntPtr inputReadHandle,
        IntPtr outputWriteHandle,
        IntPtr processHandle,
        int processId,
        IntPtr pseudoConsole)
    {
        _input = input;
        _output = output;
        _inputReadHandle = inputReadHandle;
        _outputWriteHandle = outputWriteHandle;
        _processHandle = processHandle;
        ProcessId = processId;
        _pseudoConsole = pseudoConsole;
    }

    public int ProcessId { get; }

    public Stream Input => _input;

    public Stream Output => _output;

    public bool HasExited => WaitForSingleObject(_processHandle, 0) == WaitObjectSignaled;

    public int ExitCode
    {
        get
        {
            return GetExitCodeProcess(_processHandle, out uint exitCode) ? unchecked((int)exitCode) : -1;
        }
    }

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public static ConPtySession Start(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        short columns = 140,
        short rows = 40)
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("当前 Windows 版本不支持 ConPTY。");
        }

        IntPtr inputRead = IntPtr.Zero;
        IntPtr inputWrite = IntPtr.Zero;
        IntPtr outputRead = IntPtr.Zero;
        IntPtr outputWrite = IntPtr.Zero;
        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr pseudoConsoleAttribute = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        FileStream? input = null;
        FileStream? output = null;

        try
        {
            SecurityAttributes pipeAttributes = new()
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                InheritHandle = true
            };
            if (!CreatePipe(out inputRead, out inputWrite, ref pipeAttributes, 0)
                || !CreatePipe(out outputRead, out outputWrite, ref pipeAttributes, 0))
            {
                ThrowLastWin32Error("创建 ConPTY 管道失败");
            }

            // 只有传给 ConPTY 的子进程侧句柄需要继承，父进程保留的两端必须清除继承标志。
            if (!SetHandleInformation(inputWrite, HandleFlagInherit, 0)
                || !SetHandleInformation(outputRead, HandleFlagInherit, 0))
            {
                ThrowLastWin32Error("设置 ConPTY 管道属性失败");
            }

            int pseudoConsoleResult = CreatePseudoConsole(
                new Coord(Math.Max((short)40, columns), Math.Max((short)10, rows)),
                inputRead,
                outputWrite,
                0,
                out pseudoConsole);
            if (pseudoConsoleResult != 0)
            {
                throw new Win32Exception(pseudoConsoleResult, "创建 ConPTY 会话失败。");
            }

            IntPtr attributeSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
            if (attributeSize == IntPtr.Zero || attributeSize.ToInt64() > int.MaxValue)
            {
                ThrowLastWin32Error("获取 ConPTY 进程属性大小失败");
            }

            attributeList = Marshal.AllocHGlobal(attributeSize.ToInt32());
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize))
            {
                ThrowLastWin32Error("初始化 ConPTY 进程属性失败");
            }

            pseudoConsoleAttribute = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(pseudoConsoleAttribute, pseudoConsole);
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)ProcThreadAttributePseudoConsole,
                    pseudoConsoleAttribute,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                ThrowLastWin32Error("设置 ConPTY 进程属性失败");
            }

            StartupInfoEx startupInfo = new()
            {
                StartupInfo = { cb = Marshal.SizeOf<StartupInfoEx>() },
                AttributeList = attributeList
            };
            ProcessInformation processInformation;
            StringBuilder commandLine = new(BuildCommandLine(executable, arguments));
            uint creationFlags = ExtendedStartupInfoPresent | CreateUnicodeEnvironment;
            if (!CreateProcess(
                    executable,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    creationFlags,
                    IntPtr.Zero,
                    string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                    ref startupInfo,
                    out processInformation))
            {
                ThrowLastWin32Error("启动 ConPTY PowerShell 会话失败");
            }

            processHandle = processInformation.ProcessHandle;
            CloseHandle(processInformation.ThreadHandle);

            // ConPTY 已经接管了子进程的标准输入输出，父进程只保留管道的写入和读取端。
            input = new FileStream(
                new SafeFileHandle(inputWrite, ownsHandle: true),
                FileAccess.Write,
                4096,
                isAsync: false);
            inputWrite = IntPtr.Zero;
            output = new FileStream(
                new SafeFileHandle(outputRead, ownsHandle: true),
                FileAccess.Read,
                4096,
                isAsync: false);
            outputRead = IntPtr.Zero;

            IntPtr sessionInputRead = inputRead;
            IntPtr sessionOutputWrite = outputWrite;
            inputRead = IntPtr.Zero;
            outputWrite = IntPtr.Zero;
            return new ConPtySession(
                input,
                output,
                sessionInputRead,
                sessionOutputWrite,
                processHandle,
                processInformation.ProcessId,
                pseudoConsole);
        }
        catch
        {
            input?.Dispose();
            output?.Dispose();
            if (processHandle != IntPtr.Zero)
            {
                TerminateProcess(processHandle, 1);
                CloseHandle(processHandle);
            }

            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (pseudoConsoleAttribute != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pseudoConsoleAttribute);
            }

            CloseHandle(inputRead);
            CloseHandle(inputWrite);
            CloseHandle(outputRead);
            CloseHandle(outputWrite);

            if (pseudoConsole != IntPtr.Zero && (processHandle == IntPtr.Zero || input is null || output is null))
            {
                ClosePseudoConsole(pseudoConsole);
            }
        }
    }

    public Task WaitForExitAsync() => Task.Run(() => WaitForSingleObject(_processHandle, Infinite));

    public void Kill()
    {
        if (!HasExited)
        {
            TerminateProcess(_processHandle, 1);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _input.DisposeAsync().ConfigureAwait(false);
        await _output.DisposeAsync().ConfigureAwait(false);
        CloseHandle(_inputReadHandle);
        CloseHandle(_outputWriteHandle);
        CloseHandle(_processHandle);
        ClosePseudoConsole(_pseudoConsole);
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        StringBuilder builder = new();
        builder.Append(QuoteArgument(executable));
        foreach (string argument in arguments)
        {
            builder.Append(' ').Append(QuoteArgument(argument));
        }

        return builder.ToString();
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        bool needsQuotes = argument.Any(char.IsWhiteSpace) || argument.Contains('"');
        if (!needsQuotes)
        {
            return argument;
        }

        StringBuilder builder = new(argument.Length + 2);
        builder.Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(character);
        }

        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    private static void ThrowLastWin32Error(string message) =>
        throw new Win32Exception(Marshal.GetLastWin32Error(), message);

    private static void CloseHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            CloseNativeHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr reserved;
        public IntPtr desktop;
        public IntPtr title;
        public int x;
        public int y;
        public int xSize;
        public int ySize;
        public int xCountChars;
        public int yCountChars;
        public int fillAttribute;
        public int flags;
        public short showWindow;
        public short reserved2;
        public IntPtr reserved2Pointer;
        public IntPtr standardInput;
        public IntPtr standardOutput;
        public IntPtr standardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ProcessInformation
    {
        public readonly IntPtr ProcessHandle;
        public readonly IntPtr ThreadHandle;
        public readonly int ProcessId;
        public readonly int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out IntPtr readPipe,
        out IntPtr writePipe,
        ref SecurityAttributes attributes,
        int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        Coord size,
        IntPtr inputReadHandle,
        IntPtr outputWriteHandle,
        uint flags,
        out IntPtr pseudoConsole);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        uint flags,
        ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr processHandle, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr processHandle, out uint exitCode);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    private static extern bool CloseNativeHandle(IntPtr handle);
}
