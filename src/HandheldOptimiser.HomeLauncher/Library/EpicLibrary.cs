using System.IO;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Reads installed games from the Epic Games Launcher's manifests. Most Epic games need the launcher for
/// sign-in, so they are started through its URI with silent=true, which keeps the launcher minimised.
/// </summary>
internal static class EpicLibrary
{
    private static readonly string ManifestDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public static IEnumerable<Game> Scan()
    {
        string[] manifests;
        try
        {
            manifests = Directory.GetFiles(ManifestDir, "*.item");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var path in manifests)
        {
            var game = ReadManifest(path);
            if (game is not null)
            {
                yield return game;
            }
        }
    }

    private static Game? ReadManifest(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var appName = Str(root, "AppName");
            var title = Str(root, "DisplayName");
            var ns = Str(root, "CatalogNamespace");
            var itemId = Str(root, "CatalogItemId");
            var mainGame = Str(root, "MainGameAppName");

            var isGame = root.TryGetProperty("AppCategories", out var cats)
                && cats.ValueKind == JsonValueKind.Array
                && cats.EnumerateArray().Any(c => c.GetString() == "games");
            var incomplete = root.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True;

            // DLC manifests point MainGameAppName at the base game.
            var isDlc = !string.IsNullOrEmpty(mainGame) && !string.Equals(mainGame, appName, StringComparison.OrdinalIgnoreCase);

            if (appName is null || title is null || ns is null || itemId is null || !isGame || incomplete || isDlc)
            {
                return null;
            }

            var installDir = Str(root, "InstallLocation");
            var exe = Str(root, "LaunchExecutable");
            var exePath = installDir is not null && exe is not null ? Path.Combine(installDir, exe) : null;

            return new Game(
                Key: $"epic:{appName}",
                Title: title,
                Store: GameStore.Epic,
                CoverPath: null,
                IconPath: exePath,
                LaunchTarget: $"com.epicgames.launcher://apps/{Uri.EscapeDataString($"{ns}:{itemId}:{appName}")}?action=launch&silent=true");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
