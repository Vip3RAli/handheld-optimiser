namespace HandheldOptimiser.Services;

/// <summary>
/// Turns text into a PowerShell single-quoted string literal, for the places where a name or path is
/// pasted into a script this elevated app runs.
///
/// Doubling only the ASCII apostrophe is not enough. PowerShell also ends a single-quoted string at the
/// typographic quotes U+2018 to U+201B, so a device or package name containing one of those could close
/// the literal early and have the rest of the name run as script. Every quote form PowerShell recognises
/// is doubled, which inside a single-quoted string means "one literal quote".
/// </summary>
public static class PsQuote
{
    /// <summary>The characters PowerShell treats as a single quote: ', ‘, ’, ‚ and ‛.</summary>
    private static readonly char[] SingleQuotes = ['\'', '‘', '’', '‚', '‛'];

    /// <summary>The text with every single-quote form doubled, for use between single quotes.</summary>
    public static string Escape(string value)
    {
        if (value.IndexOfAny(SingleQuotes) < 0)
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length + 8);

        foreach (var c in value)
        {
            builder.Append(c);

            if (Array.IndexOf(SingleQuotes, c) >= 0)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>The text as a complete single-quoted literal, quotes included.</summary>
    public static string Literal(string value) => $"'{Escape(value)}'";

    /// <summary>
    /// Whether the text holds a control character such as a line break. Safe inside a quoted literal,
    /// but no real device or package name has one, so a name that does was not produced honestly.
    /// </summary>
    public static bool HasControlCharacters(string value) => value.Any(char.IsControl);
}
