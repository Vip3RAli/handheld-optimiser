namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Reads Valve's text KeyValues format, which Steam uses for its config files: quoted keys, each followed
/// by a quoted value or a braced block of more keys. Keys are matched without regard to case, as Steam does.
/// </summary>
internal sealed class KeyValues
{
    private readonly Dictionary<string, KeyValues> _blocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<KeyValuePair<string, KeyValues>> Blocks => _blocks;

    public KeyValues? Block(string key) => _blocks.GetValueOrDefault(key);

    public string? Value(string key) => _values.GetValueOrDefault(key);

    /// <summary>Follows a path of block names, such as "Software", "Valve", "Steam".</summary>
    public KeyValues? Path(params string[] keys)
    {
        var block = this;
        foreach (var key in keys)
        {
            block = block?.Block(key);
        }

        return block;
    }

    /// <returns>The top-level keys, or null when the text is not KeyValues.</returns>
    public static KeyValues? Parse(string text)
    {
        var position = 0;
        var root = new KeyValues();
        return ReadBlock(text, ref position, root, topLevel: true) ? root : null;
    }

    private static bool ReadBlock(string text, ref int position, KeyValues block, bool topLevel)
    {
        while (true)
        {
            var key = ReadToken(text, ref position);
            if (key is null)
            {
                // The end of the text closes the top level, but a nested block needs its brace.
                return topLevel;
            }

            if (key.Value.IsBrace)
            {
                return key.Value.Text == "}" && !topLevel;
            }

            var next = ReadToken(text, ref position);
            if (next is null)
            {
                return false;
            }

            if (next.Value is { IsBrace: true, Text: "{" })
            {
                var child = new KeyValues();
                if (!ReadBlock(text, ref position, child, topLevel: false))
                {
                    return false;
                }

                block._blocks[key.Value.Text] = child;
            }
            else if (next.Value.IsBrace)
            {
                return false;
            }
            else
            {
                block._values[key.Value.Text] = next.Value.Text;
            }
        }
    }

    private readonly record struct Token(string Text, bool IsBrace);

    private static Token? ReadToken(string text, ref int position)
    {
        while (position < text.Length)
        {
            var c = text[position];
            if (char.IsWhiteSpace(c))
            {
                position++;
            }
            else if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                // A comment runs to the end of the line.
                while (position < text.Length && text[position] != '\n')
                {
                    position++;
                }
            }
            else if (c is '{' or '}')
            {
                position++;
                return new Token(c.ToString(), true);
            }
            else if (c == '"')
            {
                var builder = new System.Text.StringBuilder();
                position++;
                while (position < text.Length && text[position] != '"')
                {
                    if (text[position] == '\\' && position + 1 < text.Length)
                    {
                        position++;
                        builder.Append(text[position] switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            var other => other
                        });
                    }
                    else
                    {
                        builder.Append(text[position]);
                    }

                    position++;
                }

                position++;
                return new Token(builder.ToString(), false);
            }
            else
            {
                // An unquoted token, which older files use now and then.
                var start = position;
                while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or '"'))
                {
                    position++;
                }

                return new Token(text[start..position], false);
            }
        }

        return null;
    }
}
