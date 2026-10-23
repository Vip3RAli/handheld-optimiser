namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>How a menu row's setting steps through its choices with left and right, and how it says what it is set to.</summary>
internal static class SettingSteps
{
    /// <summary>The next or previous choice of a setting, going round at either end.</summary>
    public static T Step<T>(T value, int step) where T : struct, Enum
    {
        var choices = Enum.GetValues<T>();
        return choices[(Array.IndexOf(choices, value) + step + choices.Length) % choices.Length];
    }

    /// <summary>The next or previous of a list of numbers, going round at either end. A number not in the list counts as the first.</summary>
    public static int StepAmong(int value, IReadOnlyList<int> choices, int step)
    {
        var index = Math.Max(0, choices.ToList().IndexOf(value));
        return choices[(index + step + choices.Count) % choices.Count];
    }

    /// <summary>The next or previous choice, where the first choice is none of them (Don't change), going round at either end.</summary>
    public static T? StepChoice<T>(T? value, IReadOnlyList<T> choices, int step) where T : struct
    {
        var options = new List<T?> { null };
        options.AddRange(choices.Select(choice => (T?)choice));
        var index = Math.Max(0, options.IndexOf(value));
        return options[(index + step + options.Count) % options.Count];
    }

    /// <summary>What a switch shows at the right of its row.</summary>
    public static string OnOff(bool on) => on ? "On" : "Off";
}
