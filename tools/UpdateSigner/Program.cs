using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.UpdateSigner;

/// <summary>
/// Signs release installers for the in-app updater.
///
/// The private key is an ECDSA P-256 key kept in %APPDATA%\HandheldOptimiser-Signing, encrypted with
/// Windows DPAPI so only this Windows account can use it and builds never ask for a password. It never
/// goes near the repository. The app has the matching public key built in and refuses any installer
/// whose signature does not check out. Losing the key means installed copies cannot verify future
/// updates, so export-backup writes a password-protected copy to keep somewhere safe.
/// </summary>
internal static class Program
{
    private static readonly string DefaultKeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HandheldOptimiser-Signing", "update-signing-key.bin");

    // Ties the DPAPI blob to this purpose, so it cannot be confused with other data this account protects.
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("HandheldOptimiser update signing key v1");

    private static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);

            return options.Command switch
            {
                "keygen" => KeyGen(options),
                "public-key" => PrintPublicKey(options),
                "sign" => Sign(options),
                "verify" => Verify(options),
                "export-backup" => ExportBackup(options),
                "import-backup" => ImportBackup(options),
                _ => Usage()
            };
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            UpdateSigner <command> [options]

              keygen                           Create the signing key (refuses to replace an existing one)
              public-key                       Print the public key to paste into UpdateService.cs
              sign <installer> <version>       Write <installer>.sig
              verify <installer> --public-key <base64>
                                               Check <installer>.sig against a public key
              export-backup <file.pem>         Write a password-protected copy of the key
              import-backup <file.pem>         Restore the key from such a copy

            Options:
              --key <path>                     Key file (default: %APPDATA%\HandheldOptimiser-Signing\update-signing-key.bin)
            """);
        return 2;
    }

    private static int KeyGen(Options o)
    {
        if (File.Exists(o.KeyPath))
        {
            Console.Error.WriteLine($"A signing key already exists at {o.KeyPath}. Refusing to replace it: installed copies of the app trust that key.");
            return 1;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        SaveKey(key, o.KeyPath);

        Console.WriteLine($"Created {o.KeyPath}");
        Console.WriteLine($"Public key: {Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}");
        Console.WriteLine("Back it up now with: export-backup <file.pem>");
        return 0;
    }

    private static int PrintPublicKey(Options o)
    {
        using var key = LoadKey(o.KeyPath);
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    private static int Sign(Options o)
    {
        var installer = o.Arg(0, "installer");
        var version = o.Arg(1, "version");

        if (!System.Version.TryParse(version, out _))
        {
            throw new ArgumentException($"\"{version}\" is not a version number.");
        }

        using var key = LoadKey(o.KeyPath);

        var manifest = new UpdateManifest
        {
            Product = UpdateManifest.ProductName,
            Version = version,
            FileName = Path.GetFileName(installer),
            Size = new FileInfo(installer).Length,
            Sha256 = HashFile(installer)
        };

        manifest.Signature = Convert.ToBase64String(key.SignData(manifest.SignedBytes(), HashAlgorithmName.SHA256));

        var sigPath = installer + ".sig";
        File.WriteAllText(sigPath, JsonSerializer.Serialize(manifest, UpdateManifest.JsonOptions));

        Console.WriteLine($"Signed {manifest.FileName} {manifest.Version} -> {sigPath}");
        return 0;
    }

    private static int Verify(Options o)
    {
        var installer = o.Arg(0, "installer");
        var publicKey = o.PublicKey ?? throw new ArgumentException("--public-key is required.");

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(installer + ".sig"))
                       ?? throw new JsonException("Empty signature file.");

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);

        var problems = new List<string>();

        if (!manifest.FieldsAreWellFormed()) problems.Add("malformed fields");
        if (manifest.FileName != Path.GetFileName(installer)) problems.Add("file name differs");
        if (manifest.Size != new FileInfo(installer).Length) problems.Add("size differs");
        if (!string.Equals(manifest.Sha256, HashFile(installer), StringComparison.OrdinalIgnoreCase)) problems.Add("hash differs");
        if (!key.VerifyData(manifest.SignedBytes(), Convert.FromBase64String(manifest.Signature), HashAlgorithmName.SHA256)) problems.Add("signature does not match this public key");

        if (problems.Count > 0)
        {
            Console.Error.WriteLine($"FAILED: {string.Join(", ", problems)}");
            return 1;
        }

        Console.WriteLine($"OK: {manifest.FileName} {manifest.Version} is signed by this key.");
        return 0;
    }

    private static int ExportBackup(Options o)
    {
        var target = o.Arg(0, "file.pem");
        if (File.Exists(target))
        {
            throw new IOException($"{target} already exists.");
        }

        using var key = LoadKey(o.KeyPath);

        var password = ReadPassword("Backup password: ");
        if (password.Length < 12)
        {
            throw new ArgumentException("Use at least 12 characters; this password is all that protects the backup.");
        }

        if (ReadPassword("Repeat it: ") != password)
        {
            throw new ArgumentException("The passwords did not match.");
        }

        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);
        File.WriteAllText(target, key.ExportEncryptedPkcs8PrivateKeyPem(password.AsSpan(), pbe));

        Console.WriteLine($"Wrote {target}. Keep it (and the password) somewhere other than this PC.");
        return 0;
    }

    private static int ImportBackup(Options o)
    {
        var source = o.Arg(0, "file.pem");

        if (File.Exists(o.KeyPath))
        {
            Console.Error.WriteLine($"A signing key already exists at {o.KeyPath}. Move it away first if you really mean to replace it.");
            return 1;
        }

        using var key = ECDsa.Create();
        key.ImportFromEncryptedPem(File.ReadAllText(source), ReadPassword("Backup password: ").AsSpan());
        SaveKey(key, o.KeyPath);

        Console.WriteLine($"Restored the signing key to {o.KeyPath}");
        Console.WriteLine($"Public key: {Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}");
        return 0;
    }

    private static void SaveKey(ECDsa key, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var protectedKey = System.Security.Cryptography.ProtectedData.Protect(
            key.ExportPkcs8PrivateKey(), DpapiEntropy, DataProtectionScope.CurrentUser);

        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        file.Write(protectedKey);
    }

    private static ECDsa LoadKey(string path)
    {
        if (!File.Exists(path))
        {
            throw new IOException($"No signing key at {path}. Create one with keygen, or restore one with import-backup.");
        }

        var pkcs8 = System.Security.Cryptography.ProtectedData.Unprotect(
            File.ReadAllBytes(path), DpapiEntropy, DataProtectionScope.CurrentUser);

        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        CryptographicOperations.ZeroMemory(pkcs8);
        return key;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        var password = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return password.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0) password.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }
    }

    private sealed class Options
    {
        public string Command { get; private init; } = string.Empty;
        public string KeyPath { get; private init; } = DefaultKeyPath;
        public string? PublicKey { get; private init; }
        private List<string> Positional { get; } = [];

        public string Arg(int index, string name) =>
            index < Positional.Count ? Positional[index] : throw new ArgumentException($"Missing <{name}>.");

        public static Options Parse(string[] args)
        {
            string? keyPath = null;
            string? publicKey = null;
            var positional = new List<string>();

            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--key" when i + 1 < args.Length:
                        keyPath = args[++i];
                        break;
                    case "--public-key" when i + 1 < args.Length:
                        publicKey = args[++i];
                        break;
                    default:
                        positional.Add(args[i]);
                        break;
                }
            }

            var options = new Options
            {
                Command = args.Length > 0 ? args[0] : string.Empty,
                KeyPath = keyPath ?? DefaultKeyPath,
                PublicKey = publicKey
            };
            options.Positional.AddRange(positional);
            return options;
        }
    }
}
