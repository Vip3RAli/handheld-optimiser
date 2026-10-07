using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>When the library closes a store's own app after one of its games.</summary>
internal enum StoreClosing
{
    Off,

    /// <summary>Only when it was not open before the game started, so the game opened it.</summary>
    OpenedByGame,

    Always
}

/// <summary>
/// The stores' own apps (Steam, the Epic Games Launcher and the rest), which games start and then leave
/// running in the background.
/// </summary>
internal static class StoreClients
{
    /// <param name="ExeNames">Its exe file names, the main one first.</param>
    public sealed record Client(GameStore Store, string Name, string[] ExeNames);

    public static readonly Client[] All =
    [
        new(GameStore.Steam, "Steam", ["steam.exe"]),
        new(GameStore.Xbox, "Xbox app", ["XboxPcApp.exe"]),
        new(GameStore.Epic, "Epic Games Launcher", ["EpicGamesLauncher.exe"]),
        new(GameStore.BattleNet, "Battle.net", ["Battle.net.exe"]),
        new(GameStore.Gog, "GOG Galaxy", ["GalaxyClient.exe"]),
        new(GameStore.Ea, "EA app", ["EADesktop.exe"]),
        new(GameStore.Ubisoft, "Ubisoft Connect", ["UbisoftConnect.exe", "upc.exe"])
    ];

    public static Client? For(GameStore store) => All.FirstOrDefault(c => c.Store == store);

    /// <summary>The store app a startup command starts, if it is one.</summary>
    public static Client? ForCommand(string command) =>
        All.FirstOrDefault(c => c.ExeNames.Any(exe => command.Contains(exe, StringComparison.OrdinalIgnoreCase)));

    public static bool IsRunning(GameStore store)
    {
        if (For(store) is not { } client)
        {
            return false;
        }

        var running = false;
        foreach (var exe in client.ExeNames)
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            {
                process.Dispose();
                running = true;
            }
        }

        return running;
    }

    /// <summary>
    /// Closes a store's app. Steam is asked to shut down, which lets it finish syncing saves to the
    /// cloud; the others have no such command and are closed outright.
    /// </summary>
    /// <returns>Whether it was running and has been told to close.</returns>
    public static bool Close(GameStore store)
    {
        if (For(store) is not { } client || !IsRunning(store))
        {
            return false;
        }

        if (store == GameStore.Steam && SteamLibrary.ReadSteamExe() is { } steam)
        {
            try
            {
                Process.Start(new ProcessStartInfo(steam, "-shutdown") { UseShellExecute = false })?.Dispose();
                Program.Log("Asked Steam to shut down");
                return true;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                Program.Log($"Could not ask Steam to shut down: {ex.Message}");
            }
        }

        var closed = false;
        foreach (var exe in client.ExeNames)
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            {
                using (process)
                {
                    try
                    {
                        // Their web views run as child processes, which go with them.
                        process.Kill(entireProcessTree: true);
                        closed = true;
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        Program.Log($"Could not close {client.Name}: {ex.Message}");
                    }
                }
            }
        }

        if (closed)
        {
            Program.Log($"Closed {client.Name}");
        }

        return closed;
    }
}
