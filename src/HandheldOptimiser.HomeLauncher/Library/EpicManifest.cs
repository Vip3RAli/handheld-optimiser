using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>A piece of a build as Epic keeps it on its CDN: about a megabyte, shared by every file that has those bytes.</summary>
/// <param name="Guid">The piece's id, as 32 hex digits.</param>
/// <param name="Hash">Epic's rolling hash of the piece, which is part of its address.</param>
/// <param name="Sha">The SHA-1 of the piece unpacked, as hex.</param>
/// <param name="Group">The folder of the CDN the piece is in.</param>
/// <param name="Size">The piece's size unpacked.</param>
/// <param name="FileSize">The piece's size on the CDN.</param>
internal sealed record EpicChunk(string Guid, ulong Hash, string Sha, byte Group, uint Size, long FileSize);

/// <summary>Part of a file: so many bytes from somewhere in a piece.</summary>
internal sealed record EpicChunkPart(string Guid, uint Offset, uint Size);

/// <summary>A file of a build, as the pieces it is put together from, in order.</summary>
/// <param name="Sha">The SHA-1 of the whole file, as hex.</param>
/// <param name="Tags">The optional parts of the game the file belongs to; none for a file every install has.</param>
internal sealed record EpicFile(string Path, string Sha, IReadOnlyList<EpicChunkPart> Parts, IReadOnlyList<string> Tags)
{
    public long Size => Parts.Sum(p => (long)p.Size);
}

/// <summary>A build of an Epic game: how it is started, the files it is made of and the pieces those are made from.</summary>
/// <param name="Version">The manifest's format, which says which folder of the CDN its pieces are in.</param>
internal sealed record EpicManifest(
    int Version,
    string AppName,
    string BuildVersion,
    string LaunchExe,
    string LaunchCommand,
    IReadOnlyList<string> PrerequisiteIds,
    string PrerequisitePath,
    string PrerequisiteArguments,
    IReadOnlyList<EpicChunk> Chunks,
    IReadOnlyList<EpicFile> Files);

/// <summary>
/// Reads Epic's build manifests and the pieces they name, as the Epic Games Launcher does. The layout is the
/// one Legendary documents by reading it (its code is not used here): a short header, then a body packed with
/// zlib that holds the build's details, the list of pieces and the list of files, each list stored a column
/// at a time. Only the binary format is read; the old JSON manifests of a few early games are refused.
/// </summary>
internal static class EpicManifests
{
    private const uint ManifestMagic = 0x44BEC00C;
    private const uint ChunkMagic = 0xB1FE3AA2;

    /// <summary>A manifest's bytes read, or null when they are not a binary manifest or do not match their checksum.</summary>
    public static EpicManifest? Parse(byte[] bytes)
    {
        try
        {
            var header = new Reader(bytes);
            if (header.U32() != ManifestMagic)
            {
                return null;
            }

            var headerSize = (int)header.U32();
            var sizeUnpacked = (int)header.U32();
            header.U32();
            var sha = header.Bytes(20);
            var storedAs = header.U8();
            var version = (int)header.U32();

            var body = bytes.AsSpan(headerSize).ToArray();
            if ((storedAs & 1) != 0)
            {
                body = Unzlib(body, sizeUnpacked);
            }

            if (!SHA1.HashData(body).AsSpan().SequenceEqual(sha))
            {
                return null;
            }

            var reader = new Reader(body);

            // The build's details.
            var metaStart = reader.Position;
            var metaSize = (int)reader.U32();
            var dataVersion = reader.U8();
            reader.U32();
            reader.U8();
            reader.U32();
            var appName = reader.Text();
            var buildVersion = reader.Text();
            var launchExe = reader.Text();
            var launchCommand = reader.Text();
            var prerequisites = reader.Texts((int)reader.U32());
            reader.Text();
            var prerequisitePath = reader.Text();
            var prerequisiteArguments = reader.Text();
            _ = dataVersion;
            reader.Position = metaStart + metaSize;

            // The pieces, a column at a time.
            var chunksStart = reader.Position;
            var chunksSize = (int)reader.U32();
            reader.U8();
            var count = (int)reader.U32();
            var guids = new string[count];
            for (var i = 0; i < count; i++)
            {
                guids[i] = reader.Guid();
            }

            var hashes = new ulong[count];
            for (var i = 0; i < count; i++)
            {
                hashes[i] = reader.U64();
            }

            var shas = new string[count];
            for (var i = 0; i < count; i++)
            {
                shas[i] = Convert.ToHexStringLower(reader.Bytes(20));
            }

            var groups = new byte[count];
            for (var i = 0; i < count; i++)
            {
                groups[i] = reader.U8();
            }

            var sizes = new uint[count];
            for (var i = 0; i < count; i++)
            {
                sizes[i] = reader.U32();
            }

            var chunks = new List<EpicChunk>(count);
            for (var i = 0; i < count; i++)
            {
                chunks.Add(new EpicChunk(guids[i], hashes[i], shas[i], groups[i], sizes[i], (long)reader.U64()));
            }

            reader.Position = chunksStart + chunksSize;

            // The files, a column at a time.
            var filesStart = reader.Position;
            var filesSize = (int)reader.U32();
            reader.U8();
            var fileCount = (int)reader.U32();
            var names = reader.Texts(fileCount);
            reader.Texts(fileCount);
            var fileShas = new string[fileCount];
            for (var i = 0; i < fileCount; i++)
            {
                fileShas[i] = Convert.ToHexStringLower(reader.Bytes(20));
            }

            reader.Position += fileCount;
            var tags = new List<string>[fileCount];
            for (var i = 0; i < fileCount; i++)
            {
                tags[i] = reader.Texts((int)reader.U32());
            }

            var files = new List<EpicFile>(fileCount);
            for (var i = 0; i < fileCount; i++)
            {
                var partCount = (int)reader.U32();
                var parts = new List<EpicChunkPart>(partCount);
                for (var p = 0; p < partCount; p++)
                {
                    var partStart = reader.Position;
                    var partSize = (int)reader.U32();
                    parts.Add(new EpicChunkPart(reader.Guid(), reader.U32(), reader.U32()));
                    reader.Position = partStart + partSize;
                }

                files.Add(new EpicFile(names[i], fileShas[i], parts, tags[i]));
            }

            _ = filesStart + filesSize;
            return new EpicManifest(version, appName, buildVersion, launchExe, launchCommand, prerequisites, prerequisitePath, prerequisiteArguments, chunks, files);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException or OverflowException or ArgumentException)
        {
            // Cut short, or not laid out as expected.
            return null;
        }
    }

    /// <summary>The folder of the CDN a manifest's pieces are in, which changed with the manifest's format.</summary>
    internal static string ChunkFolder(int version) => version switch
    {
        >= 15 => "ChunksV4",
        >= 6 => "ChunksV3",
        >= 3 => "ChunksV2",
        _ => "Chunks",
    };

    /// <summary>Where a piece is under a build's address on the CDN.</summary>
    public static string ChunkPath(EpicChunk chunk, int version) =>
        $"{ChunkFolder(version)}/{chunk.Group:D2}/{chunk.Hash:X16}_{chunk.Guid}.chunk";

    /// <summary>
    /// A piece as the CDN gives it, unpacked and checked against the manifest's SHA-1 and size. A piece that
    /// fails is refused, never written.
    /// </summary>
    public static byte[] UnpackChunk(byte[] file, EpicChunk chunk)
    {
        var reader = new Reader(file);
        if (reader.U32() != ChunkMagic)
        {
            throw new InvalidDataException("A piece was not one of Epic's.");
        }

        reader.U32();
        var headerSize = (int)reader.U32();
        reader.U32();
        reader.Position += 16 + 8;
        var storedAs = reader.U8();
        if (headerSize > file.Length)
        {
            throw new InvalidDataException("A piece was cut short.");
        }

        var data = file.AsSpan(headerSize).ToArray();
        if ((storedAs & 1) != 0)
        {
            data = Unzlib(data, (int)chunk.Size);
        }

        if (data.Length != chunk.Size || !string.Equals(Convert.ToHexStringLower(SHA1.HashData(data)), chunk.Sha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A piece did not match its checksum.");
        }

        return data;
    }

    private static byte[] Unzlib(byte[] packed, int size)
    {
        var unpacked = new byte[size];
        using var zlib = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
        var read = 0;
        int n;
        while (read < size && (n = zlib.Read(unpacked, read, size - read)) > 0)
        {
            read += n;
        }

        if (read != size)
        {
            throw new InvalidDataException("Epic's data was shorter than it said.");
        }

        return unpacked;
    }

    /// <summary>Reads Epic's little-endian numbers and its strings from a block of bytes.</summary>
    private sealed class Reader(byte[] bytes)
    {
        public int Position { get; set; }

        public byte U8() => bytes[Position++];

        public uint U32()
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Position, 4));
            Position += 4;
            return value;
        }

        public ulong U64()
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(Position, 8));
            Position += 8;
            return value;
        }

        public byte[] Bytes(int count)
        {
            var value = bytes.AsSpan(Position, count).ToArray();
            Position += count;
            return value;
        }

        /// <summary>A piece's id: four numbers, written as Epic writes them in a piece's address.</summary>
        public string Guid() => $"{U32():X8}{U32():X8}{U32():X8}{U32():X8}";

        /// <summary>
        /// One of Epic's strings: its length first, counting the zero that ends it; a negative length is a
        /// string of two-byte characters.
        /// </summary>
        public string Text()
        {
            var length = (int)U32();
            if (length == 0)
            {
                return string.Empty;
            }

            if (length < 0)
            {
                var wide = checked(-length * 2);
                var text = Encoding.Unicode.GetString(bytes, Position, wide - 2);
                Position += wide;
                return text;
            }

            var plain = Encoding.ASCII.GetString(bytes, Position, length - 1);
            Position += length;
            return plain;
        }

        public List<string> Texts(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            var list = new List<string>(Math.Min(count, 1 << 16));
            for (var i = 0; i < count; i++)
            {
                list.Add(Text());
            }

            return list;
        }
    }
}
