using System.ServiceProcess;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// Runs as the Auto TDP Windows service, which the installer registers to start only on demand. With
/// --console it runs in a console window instead, for trying it out as administrator.
/// </summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        var service = new AutoTdpService();
        if (args.Contains("--console", StringComparer.OrdinalIgnoreCase))
        {
            service.RunInConsole();
            return;
        }

        ServiceBase.Run(service);
    }
}
