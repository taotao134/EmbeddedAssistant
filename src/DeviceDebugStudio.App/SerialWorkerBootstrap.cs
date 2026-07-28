using System.Runtime.CompilerServices;
using DeviceDebugStudio.Infrastructure.Transports;

namespace DeviceDebugStudio.App;

internal static class SerialWorkerBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        if (!SerialPortTransport.IsWorkerInvocation(arguments))
        {
            return;
        }

        int exitCode = SerialPortTransport.RunWorkerProcess(arguments);
        Environment.Exit(exitCode);
    }
}
