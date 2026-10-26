namespace HandheldOptimiser.Services;

/// <summary>Restarts Windows, after the player has said yes. Shared by the restart banner and the GPU memory section.</summary>
public static class WindowsRestart
{
    /// <summary>Starts a restart in five seconds, which Windows shows with the reason.</summary>
    public static void Start() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = System.IO.Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            Arguments = "/r /t 5 /c \"Handheld Optimiser: applying changes\"",
            CreateNoWindow = true,
            UseShellExecute = false
        })?.Dispose();
}
