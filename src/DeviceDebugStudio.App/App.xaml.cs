using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DeviceDebugStudio.App.ViewModels;
using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Infrastructure.Import;
using DeviceDebugStudio.Infrastructure.Persistence;
using DeviceDebugStudio.Infrastructure.Transports;
using DeviceDebugStudio.Infrastructure.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Wpf.Ui.Appearance;
using UiWindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace DeviceDebugStudio.App;

public partial class App : Application
{
    static App()
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
    }

    public const string ProductName = "嵌入式调试台";
    public const string DefaultTerminalTextColor = "#111111";
    public const string DefaultTerminalBackgroundColor = "#FFFFFF";
    public const string DefaultTerminalSeparatorColor = AppSettings.DefaultTerminalSeparatorColor;
    public const string DarkThemeTerminalTextColor = "#F8FAFC";
    public const string DarkThemeTerminalBackgroundColor = "#0B1220";
    public static IReadOnlyList<string> DefaultTerminalTextPalette { get; } =
        ["#111111", "#7FE2B8", "#F7C574", "#9CDCFE", "#DCDCAA", "#FF8F8F", "#C586C0", "#7AA2F7"];
    public static IReadOnlyList<string> DefaultTerminalBackgroundPalette { get; } =
        ["#FFFFFF", "#F5F5F5", "#000000", "#1E293B", "#173A34", "#312544", "#443125", "#141817"];
    public static string ProductVersionText => $"v{typeof(App).Assembly.GetName().Version?.ToString(3) ?? "未知"}";
    public static string MainWindowTitle => $"{ProductName} · {ProductVersionText}";

    private static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(3);
    private static readonly object DiagnosticLoggerLock = new();
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private static int _errorDialogVisible;
    private static int _diagnosticLoggingEnabled;
    private IHost? _host;
    private Mutex? _instanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        Stopwatch startupStopwatch = Stopwatch.StartNew();
        base.OnStartup(e);
        if (!TryAcquireApplicationMutex(out Mutex? instanceMutex))
        {
            MessageBox.Show(
                "嵌入式调试台已在运行或正在安装更新。",
                "嵌入式调试台",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _instanceMutex = instanceMutex;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        AppSettings startupSettings;
        try
        {
            startupSettings = await new AppSettingsStore().LoadAsync();
        }
        catch
        {
            startupSettings = new AppSettings();
        }
        ConfigureDiagnosticLogging(startupSettings.DebugLoggingEnabled);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<JsonDeviceProfileStore>();
                services.AddSingleton<IDeviceProfileStore>(provider => provider.GetRequiredService<JsonDeviceProfileStore>());
                services.AddSingleton<IConfigurableDeviceProfileStore>(provider => provider.GetRequiredService<JsonDeviceProfileStore>());
                services.AddSingleton<LegacyConfigImporter>();
                services.AddSingleton<AppSettingsStore>();
                services.AddSingleton<DeviceProfileFileService>();
                services.AddSingleton<CaptureFileReader>();
                services.AddSingleton<OnlineUpdateService>();
                services.AddTransient<BleDiscoveryService>();
                services.AddTransient<BleGattBrowserService>();
                services.AddTransient<MainWindowViewModel>();
                services.AddTransient<MainWindow>();
            })
            .Build();

        try
        {
            await _host.StartAsync();
            SystemTheme systemTheme = ApplicationThemeManager.GetSystemTheme();
            ApplicationTheme theme = systemTheme == SystemTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplyTheme(theme);

            ApplyTerminalColorsForTheme(
                theme,
                startupSettings.TerminalTextColor,
                startupSettings.TerminalBackgroundColor,
                startupSettings.TerminalSeparatorColor);

            MainWindow mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindowViewModel viewModel = (MainWindowViewModel)mainWindow.DataContext;
            MainWindow = mainWindow;
            mainWindow.Show();
            Log.Information("主窗口已显示，启动耗时 {ElapsedMilliseconds} ms", startupStopwatch.ElapsedMilliseconds);
            await viewModel.InitializeAsync(startupSettings);
            Log.Information("配置初始化完成，总耗时 {ElapsedMilliseconds} ms", startupStopwatch.ElapsedMilliseconds);
            if (OnlineUpdateService.TryConsumeInstallerFailure(out string installerFailure))
            {
                Log.Error("上次在线更新未完成：{InstallerFailure}", installerFailure);
                MessageBox.Show(
                    mainWindow,
                    $"上次在线更新未完成。\n\n{installerFailure}",
                    "更新失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "应用启动失败");
            MessageBox.Show($"嵌入式调试台启动失败：\n{exception.Message}", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            IHost? host = _host;
            _host = null;
            if (host is not null)
            {
                using CancellationTokenSource shutdown = new(HostShutdownTimeout);
                try
                {
                    Log.Information("正在停止应用宿主");
                    Task stopTask = Task.Run(() => host.StopAsync(shutdown.Token));
                    if (!stopTask.Wait(HostShutdownTimeout))
                    {
                        Log.Warning("应用宿主停止超时，继续结束进程");
                    }
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    Log.Warning("应用宿主停止超时，继续结束进程");
                }
                finally
                {
                    try
                    {
                        Task disposeTask = Task.Run(host.Dispose);
                        if (!disposeTask.Wait(HostShutdownTimeout))
                        {
                            Log.Warning("应用宿主销毁超时，继续结束进程");
                        }
                    }
                    catch (Exception exception)
                    {
                        Log.Error(exception, "应用关闭时销毁宿主失败");
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "应用关闭时停止宿主失败");
        }
        finally
        {
            ReleaseApplicationMutex();
            Log.Information("应用退出流程完成");
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }

    public async Task OpenAdditionalWindowAsync(Guid? preferredProfileId = null)
    {
        if (_host is null)
        {
            return;
        }

        MainWindow window = _host.Services.GetRequiredService<MainWindow>();
        int windowNumber = Current.Windows.OfType<MainWindow>().Count() + 1;
        window.Title = $"{MainWindowTitle} · 窗口 {windowNumber}";
        window.Show();

        if (window.DataContext is MainWindowViewModel viewModel)
        {
            try
            {
                AppSettings settings = await _host.Services
                    .GetRequiredService<AppSettingsStore>()
                    .LoadAsync();
                if (preferredProfileId is not null)
                {
                    settings = settings with { SelectedProfileId = preferredProfileId };
                }
                await viewModel.InitializeAsync(settings);
            }
            catch (Exception exception)
            {
                Log.Error(exception, "新调试窗口初始化失败");
                MessageBox.Show(window, $"新窗口初始化失败：{exception.Message}", "窗口初始化失败", MessageBoxButton.OK, MessageBoxImage.Error);
                window.Close();
            }
        }
    }

    private static bool TryAcquireApplicationMutex(out Mutex? mutex)
    {
        Mutex candidate = new(initiallyOwned: false, OnlineUpdateService.ApplicationMutexName);
        try
        {
            try
            {
                if (!candidate.WaitOne(0))
                {
                    candidate.Dispose();
                    mutex = null;
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
            }

            mutex = candidate;
            return true;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    private void ReleaseApplicationMutex()
    {
        Mutex? instanceMutex = Interlocked.Exchange(ref _instanceMutex, null);
        if (instanceMutex is null)
        {
            return;
        }

        try
        {
            instanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        finally
        {
            instanceMutex.Dispose();
        }
    }

    public static void ApplyTheme(ApplicationTheme theme)
    {
        ApplicationThemeManager.Apply(theme, UiWindowBackdropType.None, true);
        bool dark = theme == ApplicationTheme.Dark;
        Current.Resources["WorkspaceBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#0F172A" : "#F6F8FA"));
        Current.Resources["PanelBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#111827" : "#FFFFFF"));
        Current.Resources["SidebarBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#172033" : "#E7EDF4"));
        Current.Resources["DividerBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#28354A" : "#CBD5E1"));
        Current.Resources["MutedTextBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#94A3B8" : "#64748B"));
        Current.Resources["AccentSubtleBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#332563EB" : "#142563EB"));
        Current.Resources["AccentSoftBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#4D2563EB" : "#332563EB"));
        Current.Resources["NavigationHoverBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#1E293B" : "#EFF6FF"));
        Current.Resources["NavigationSelectedBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#203A6B" : "#E0ECFF"));
        Current.Resources["ButtonSurfaceBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#162033" : "#FFFFFF"));
        Current.Resources["ButtonForegroundBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#E5E7EB" : "#0F172A"));
        Current.Resources["QuickCommandListCanvasBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#131B2A" : "#F3F6FA"));
        Current.Resources["QuickCommandEditorBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#1B283D" : "#E7EEF7"));
        Current.Resources["QuickCommandEditorHeaderBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#223653" : "#DCE8F5"));
        Current.Resources["QuickCommandEditorBorderBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#3E5C83" : "#9FB4CE"));
        Current.Resources["QuickCommandEditorSectionBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#162033" : "#FFFFFF"));

        foreach (Window window in Current.Windows.OfType<Window>())
        {
            ApplyWindowTitleBarTheme(window, theme);
        }
    }

    public static void ApplyWindowTitleBarTheme(Window window, ApplicationTheme theme)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }

        int enabled = theme == ApplicationTheme.Dark ? 1 : 0;
        int captionColor = theme == ApplicationTheme.Dark ? 0x000F172A : 0x00FFFFFF;
        int textColor = theme == ApplicationTheme.Dark ? 0x00E5E7EB : 0x000F172A;
        try
        {
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColor, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref textColor, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    public static void ApplyTerminalColors(string textColor, string backgroundColor)
    {
        ApplyTerminalColors(textColor, backgroundColor, DefaultTerminalSeparatorColor);
    }

    public static void ApplyTerminalColors(string textColor, string backgroundColor, string separatorColor)
    {
        string normalizedText = NormalizeTerminalColor(textColor, DefaultTerminalTextColor);
        string normalizedBackground = NormalizeTerminalColor(backgroundColor, DefaultTerminalBackgroundColor);
        SetResourceBrushColor("TerminalTextBrush", normalizedText);
        SetResourceBrushColor("TerminalBackgroundBrush", normalizedBackground);
        ApplyTerminalSeparatorColor(separatorColor);
    }

    public static void ApplyTerminalSeparatorColor(string separatorColor) =>
        SetResourceBrushColor(
            "TerminalSeparatorBrush",
            NormalizeTerminalColor(separatorColor, DefaultTerminalSeparatorColor));

    public static bool DiagnosticLoggingEnabled => Volatile.Read(ref _diagnosticLoggingEnabled) != 0;

    public static void ConfigureDiagnosticLogging(bool enabled)
    {
        lock (DiagnosticLoggerLock)
        {
            Serilog.ILogger previous = Log.Logger;
            Log.Logger = enabled
                ? new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.File(
                        Path.Combine(AppPaths.DiagnosticsDirectory, "DeviceDebugStudio-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14,
                        shared: true)
                    .CreateLogger()
                : new LoggerConfiguration().CreateLogger();
            Interlocked.Exchange(ref _diagnosticLoggingEnabled, enabled ? 1 : 0);
            try
            {
                if (enabled)
                {
                    File.WriteAllText(AppPaths.DebugLoggingMarkerPath, "enabled");
                }
                else if (File.Exists(AppPaths.DebugLoggingMarkerPath))
                {
                    File.Delete(AppPaths.DebugLoggingMarkerPath);
                }
            }
            catch
            {
            }
            (previous as IDisposable)?.Dispose();
        }
    }

    public static int ClearDiagnosticLogs()
    {
        bool restoreLogging = DiagnosticLoggingEnabled;
        ConfigureDiagnosticLogging(false);
        int removed = DeleteLogFiles(AppPaths.DiagnosticsDirectory)
            + DeleteLogFiles(AppPaths.UpdateDiagnosticsDirectory);
        if (restoreLogging)
        {
            ConfigureDiagnosticLogging(true);
        }
        return removed;
    }

    private static int DeleteLogFiles(string directory)
    {
        int removed = 0;
        foreach (string path in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(path);
                removed++;
            }
            catch
            {
            }
        }
        return removed;
    }

    public static void ApplyTerminalColorsForTheme(
        ApplicationTheme theme,
        string textColor,
        string backgroundColor,
        string separatorColor)
    {
        if (theme == ApplicationTheme.Dark)
        {
            ApplyTerminalColors(DarkThemeTerminalTextColor, DarkThemeTerminalBackgroundColor, separatorColor);
            return;
        }

        ApplyTerminalColors(textColor, backgroundColor, separatorColor);
    }

    private static void SetResourceBrushColor(string resourceKey, string colorText)
    {
        System.Windows.Media.Color color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorText);
        if (Current.Resources[resourceKey] is System.Windows.Media.SolidColorBrush brush && !brush.IsFrozen)
        {
            if (brush.Color != color)
            {
                brush.Color = color;
            }
            return;
        }

        Current.Resources[resourceKey] = new System.Windows.Media.SolidColorBrush(color);
    }

    public static string NormalizeTerminalColor(string? value, string fallback)
    {
        if (!TryNormalizeTerminalColor(value, out string normalized))
        {
            return fallback;
        }
        return normalized;
    }

    public static bool TryNormalizeTerminalColor(string? value, out string normalized)
    {
        try
        {
            System.Windows.Media.Color color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value ?? string.Empty);
            normalized = color.A == byte.MaxValue
                ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            return true;
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref int value,
        int valueSize);

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "未处理的界面异常");
        if (Interlocked.Exchange(ref _errorDialogVisible, 1) == 0)
        {
            try
            {
                MessageBox.Show(e.Exception.Message, "运行错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Volatile.Write(ref _errorDialogVisible, 0);
            }
        }
        e.Handled = true;
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log.Fatal(exception, "未处理的进程异常");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "未观察的异步异常");
        e.SetObserved();
    }
}
