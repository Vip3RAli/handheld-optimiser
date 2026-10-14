namespace HandheldOptimiser.PowerService;

/// <summary>
/// What TDP Engine did, in ProgramData\HandheldOptimiser\logs\auto-tdp.log, where every change of power
/// limit is written with the frame rate and temperature that led to it.
/// </summary>
internal static class Log
{
    private const long MaxSize = 512 * 1024;

    private static readonly object Gate = new();

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HandheldOptimiser", "logs", "auto-tdp.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                // A change a few seconds over hours of play adds up; cap the file rather than rotate it.
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxSize)
                {
                    File.Delete(FilePath);
                }

                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never stop TDP Engine.
            }
        }
    }
}
