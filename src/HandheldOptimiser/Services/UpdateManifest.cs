using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HandheldOptimiser.Services;

/// <summary>
/// A signed description of one release installer, published beside it as "&lt;installer&gt;.sig".
///
/// Shared, as a linked file, between the app, which verifies it, and tools\UpdateSigner, which writes
/// it, so both always agree on exactly what the signature covers. Covering the version and file name
/// as well as the hash means an older signed installer cannot be passed off as a newer release.
/// </summary>
public sealed class UpdateManifest
{
    public const string ProductName = "HandheldOptimiser";

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Product { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }

    /// <summary>Lowercase hex SHA-256 of the installer.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Base64 ECDSA P-256 signature over <see cref="SignedBytes"/>, SHA-256, IEEE P1363 format.</summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>
    /// The exact bytes that are signed. The fields are joined rather than the JSON being signed, so
    /// whitespace or property order in the file can never change what the signature means.
    /// </summary>
    public byte[] SignedBytes() => Encoding.UTF8.GetBytes(string.Join('\n',
        Product,
        Version,
        FileName,
        Size.ToString(CultureInfo.InvariantCulture),
        Sha256.ToLowerInvariant()));

    /// <summary>
    /// True if no field could blur the boundary between fields in <see cref="SignedBytes"/>. Checked
    /// before a signature is trusted.
    /// </summary>
    public bool FieldsAreWellFormed() =>
        new[] { Product, Version, FileName, Sha256 }.All(f => f.Length > 0 && !f.Contains('\n')) && Size > 0;
}
