using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using DeviceDebugStudio.App.ViewModels;
using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Infrastructure.Import;
using DeviceDebugStudio.Infrastructure.Persistence;
using DeviceDebugStudio.Infrastructure.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Wpf.Ui.Appearance;
using UiWindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace DeviceDebugStudio.App;

public partial class App : Application
{
    public const string DefaultTerminalTextColor = "#E6EBE8";
    public const string DefaultTerminalBackgroundColor = "#141817";
    public static IReadOnlyList<string> DefaultTerminalTextPalette { get; } =
        ["#E6EBE8", "#7FE2B8", "#F7C574", "#9CDCFE", "#DCDCAA", "#FF8F8F", "#C586C0", "#7AA2F7"];
    public static IReadOnlyList<string> DefaultTerminalBackgroundPalette { get; } =
        ["#141817", "#000000", "#1E293B", "#173A34", "#312544", "#443125", "#F5F5F5", "#FFFFFF"];

    private static int _errorDialogVisible;
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(AppPaths.DiagnosticsDirectory, "DeviceDebugStudio-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();

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
                services.AddSingleton<BleDiscoveryService>();
                services.AddSingleton<BleGattBrowserService>();
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        try
        {
            await _host.StartAsync();
            SystemTheme systemTheme = ApplicationThemeManager.GetSystemTheme();
            ApplicationTheme theme = systemTheme == SystemTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplyTheme(theme);

            MainWindowViewModel viewModel = _host.Services.GetRequiredService<MainWindowViewModel>();
            await viewModel.InitializeAsync();
            _host.Services.GetRequiredService<MainWindow>().Show();
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "应用启动失败");
            MessageBox.Show($"嵌入式调试台启动失败：\n{exception.Message}", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    public static void ApplyTheme(ApplicationTheme theme)
    {
        ApplicationThemeManager.Apply(theme, UiWindowBackdropType.None, true);
        bool dark = theme == ApplicationTheme.Dark;
        Current.Resources["WorkspaceBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#181B1A" : "#F4F6F4"));
        Current.Resources["PanelBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#202422" : "#FCFDFC"));
        Current.Resources["SidebarBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#242927" : "#ECEFEC"));
        Current.Resources["DividerBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#39403C" : "#D7DDD8"));
        Current.Resources["MutedTextBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#A9B3AD" : "#66706A"));
        Current.Resources["ButtonSurfaceBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#2A2F2C" : "#FFFFFF"));
        Current.Resources["ButtonForegroundBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#EDF3EF" : "#22302A"));
    }

    public static void ApplyTerminalColors(string textColor, string backgroundColor)
    {
        string normalizedText = NormalizeTerminalColor(textColor, DefaultTerminalTextColor);
        string normalizedBackground = NormalizeTerminalColor(backgroundColor, DefaultTerminalBackgroundColor);
        Current.Resources["TerminalTextBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(normalizedText));
        Current.Resources["TerminalBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(normalizedBackground));
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
