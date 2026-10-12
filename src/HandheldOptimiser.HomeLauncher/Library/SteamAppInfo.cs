using System.Buffers.Binary;
using System.Text;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>An app as the Steam client's own cache describes it.</summary>
/// <param name="Type">What it is, in lower case: "game", "dlc", "tool", "demo", "application" and so on.</param>
internal sealed record SteamApp(long AppId, string Name, string Type);

/// <summary>
/// The Steam client's caches of what it knows about apps and packages (appcache\appinfo.vdf and
/// packageinfo.vdf), which between them say which games the account owns without asking Steam for
/// anything. Both are Valve's binary form of their text format, which Valve does not document and has
/// changed before, so anything unexpected reads as nothing at all.
/// </summary>
internal static class SteamAppInfo
{
    // The first three bytes of each file say which it is, and the fourth which version.
    private const uint AppInfoMagic = 0x07564400;
    private const uint PackageInfoMagic = 0x06565500;

    // What an entry of the binary format is, before its name.
    private const byte ObjectStart = 0;
    private const byte Text = 1;
    private const byte Int32 = 2;
    private const byte Float = 3;
    private const byte Pointer = 4;
    private const byte WideText = 5;
    private const byte Colour = 6;
    private const byte UInt64 = 7;
    private const byte ObjectEnd = 8;
    private const byte Int64 = 10;
    private const byte ObjectEndAlt = 11;

    /// <summary>Every app in appinfo.vdf that has a name, whatever it is.</summary>
    public static List<SteamApp> ParseApps(byte[] data)
    {
        var apps = new List<SteamApp>();
        try
        {
            var position = 0;
            var magic = ReadUInt32(data, ref position);
            var version = magic & 0xFF;
            if ((magic & 0xFFFFFF00) != AppInfoMagic || version is < 0x27 or > 0x29)
            {
                return apps;
            }

            position += 4;

            // From version 29 the names of the entries are kept once, in a table at the end.
            string[]? names = null;
            if (version >= 0x29)
            {
                names = ReadNames(data, checked((int)BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position, 8))));
                position += 8;
            }

            // State, last update, access token, a hash, the change number and, from version 28, another hash.
            var header = 4 + 4 + 8 + 20 + 4 + (version >= 0x28 ? 20 : 0);
            while (true)
            {
                var appId = ReadUInt32(data, ref position);
                if (appId == 0)
                {
                    break;
                }

                var size = checked((int)ReadUInt32(data, ref position));
                var end = checked(position + size);
                position += header;

                var root = ReadObject(data, ref position, end, names, depth: 0);
                if (root.GetValueOrDefault("appinfo") is Dictionary<string, object> info
                    && info.GetValueOrDefault("common") is Dictionary<string, object> common
                    && common.GetValueOrDefault("name") is string { Length: > 0 } name)
                {
                    apps.Add(new SteamApp(appId, name, (common.GetValueOrDefault("type") as string ?? string.Empty).ToLowerInvariant()));
                }

                position = end;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or FormatException)
        {
            // A file the client was halfway through writing, or a version this does not know.
            return [];
        }

        return apps;
    }

    /// <summary>
    /// The apps in the packages of packageinfo.vdf, which holds the packages the account has a licence
    /// for. Package 0 is left out: it is the one everybody has, full of tools and free demos.
    /// </summary>
    public static HashSet<long> ParsePackageApps(byte[] data)
    {
        var apps = new HashSet<long>();
        try
        {
            var position = 0;
            var magic = ReadUInt32(data, ref position);
            var version = magic & 0xFF;
            if ((magic & 0xFFFFFF00) != PackageInfoMagic || version is < 0x27 or > 0x28)
            {
                return apps;
            }

            position += 4;
            while (true)
            {
                var packageId = ReadUInt32(data, ref position);
                if (packageId == uint.MaxValue)
                {
                    break;
                }

                // A hash, the change number and, from version 28, an access token.
                position += 20 + 4 + (version >= 0x28 ? 8 : 0);

                var root = ReadObject(data, ref position, data.Length, names: null, depth: 0);
                if (packageId != 0 && root.Values.OfType<Dictionary<string, object>>().FirstOrDefault() is { } package
                    && package.GetValueOrDefault("appids") is Dictionary<string, object> ids)
                {
                    foreach (var id in ids.Values.OfType<int>())
                    {
                        apps.Add((uint)id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or FormatException)
        {
            return [];
        }

        return apps;
    }

    /// <summary>Reads entries up to the end of the object they are in, which is left behind the position.</summary>
    private static Dictionary<string, object> ReadObject(byte[] data, ref int position, int end, string[]? names, int depth)
    {
        if (depth > 32)
        {
            throw new FormatException("Nested too deep");
        }

        var entries = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (position < end)
        {
            var type = data[position++];
            if (type is ObjectEnd or ObjectEndAlt)
            {
                break;
            }

            var name = names is null ? ReadText(data, ref position) : names[checked((int)ReadUInt32(data, ref position))];
            switch (type)
            {
                case ObjectStart:
                    entries[name] = ReadObject(data, ref position, end, names, depth + 1);
                    break;
                case Text:
                    entries[name] = ReadText(data, ref position);
                    break;
                case Int32:
                    entries[name] = unchecked((int)ReadUInt32(data, ref position));
                    break;
                case Float or Pointer or Colour:
                    position += 4;
                    break;
                case UInt64 or Int64:
                    position += 8;
                    break;
                case WideText:
                    while (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position, 2)) != 0)
                    {
                        position += 2;
                    }

                    position += 2;
                    break;
                default:
                    throw new FormatException($"Unknown entry type {type}");
            }
        }

        return entries;
    }

    private static string[] ReadNames(byte[] data, int position)
    {
        var count = checked((int)ReadUInt32(data, ref position));
        if (count > data.Length)
        {
            throw new FormatException("Too many names");
        }

        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            names[i] = ReadText(data, ref position);
        }

        return names;
    }

    private static uint ReadUInt32(byte[] data, ref int position)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position, 4));
        position += 4;
        return value;
    }

    // Text ends at a zero byte.
    private static string ReadText(byte[] data, ref int position)
    {
        var length = Array.IndexOf(data, (byte)0, position) - position;
        if (length < 0)
        {
            throw new FormatException("Text without an end");
        }

        var text = Encoding.UTF8.GetString(data, position, length);
        position += length + 1;
        return text;
    }
}
