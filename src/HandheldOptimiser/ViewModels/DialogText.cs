namespace HandheldOptimiser.ViewModels;

/// <summary>What a confirmation's message needs from the dialog that shows it.</summary>
public static class DialogText
{
    /// <summary>About how many characters fit on a line of the dialog before the text wraps.</summary>
    private const int CharactersPerLine = 70;

    /// <summary>The most lines a dialog shows before its buttons would leave a 720 unit tall screen.</summary>
    private const int MostLines = 16;

    /// <summary>
    /// Whether the message is long enough to need a scrolling area, such as a list of every app about
    /// to be removed. A short message is left as plain text: in a scrolling area, a tweak's two
    /// paragraph warning grew a scroll bar it had no use for.
    /// </summary>
    public static bool NeedsScrolling(string message) =>
        message.Split('\n').Sum(line => 1 + line.Length / CharactersPerLine) > MostLines;
}
