namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The overlay's frame rate: the average of the last few one-second counts for the program in front, so the
/// figure can be read rather than jumping each second. A game's count can swing by 30 fps from one second to
/// the next (seen in Fortnite on the Ally), and TDP Engine moving the power limit adds to that. It starts
/// again when another program comes in front, so one program's frames never colour another's.
/// </summary>
internal sealed class FpsAverage(int seconds = FpsAverage.DefaultSeconds)
{
    public const int DefaultSeconds = 3;

    private readonly Queue<double> _counts = new();
    private int _process;

    /// <summary>Adds this second's count for a program and gives the average, or null when the count is not known.</summary>
    public double? Add(int process, double? fps)
    {
        if (process != _process)
        {
            _counts.Clear();
            _process = process;
        }

        if (fps is not { } count)
        {
            _counts.Clear();
            return null;
        }

        _counts.Enqueue(count);
        while (_counts.Count > seconds)
        {
            _counts.Dequeue();
        }

        return _counts.Average();
    }
}
