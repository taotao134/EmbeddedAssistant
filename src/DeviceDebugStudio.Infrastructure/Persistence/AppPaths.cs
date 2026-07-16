namespace DeviceDebugStudio.Infrastructure.Persistence;

public static class AppPaths
{
    public static string LocalDataDirectory { get; } = EnsureDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeviceDebugStudio"));

    public static string ProfilesDirectory { get; } = EnsureDirectory(Path.Combine(LocalDataDirectory, "Profiles"));

    public static string DiagnosticsDirectory { get; } = EnsureDirectory(Path.Combine(LocalDataDirectory, "Logs"));

    public static string CaptureDirectory { get; } = EnsureDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "DeviceDebugStudio",
        "Captures"));

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
