namespace HandheldOptimiser.Models;

/// <summary>
/// The handheld families this app recognises. A family rather than a model: the Ally X and the ROG Xbox
/// Ally are both <see cref="RogAlly"/>, and the Legion Go S and Go 2 are both <see cref="LegionGo"/>,
/// because what matters here is which vendor software and which quirks the device has.
/// </summary>
public enum HandheldDevice
{
    Unknown,
    RogAlly,
    LegionGo,
    MsiClaw,
    SteamDeck,
    Ayaneo,
    Gpd,
    OneXPlayer
}

public static class HandheldDevices
{
    public static string DisplayName(this HandheldDevice device) => device switch
    {
        HandheldDevice.RogAlly => "ROG Ally",
        HandheldDevice.LegionGo => "Legion Go",
        HandheldDevice.MsiClaw => "MSI Claw",
        HandheldDevice.SteamDeck => "Steam Deck",
        HandheldDevice.Ayaneo => "AYANEO",
        HandheldDevice.Gpd => "GPD",
        HandheldDevice.OneXPlayer => "OneXPlayer",
        _ => "Unknown device"
    };

    /// <summary>
    /// Works out the family from the strings the firmware reports. Vendors disagree about where the
    /// model goes: ASUS puts the code in the product name ("ROG Ally RC71L_RC71L"), Lenovo puts a
    /// machine type there ("83E1") and the name in the family ("Legion Go 8APU1"), and MSI's board
    /// code ("MS-1T41") is steadier than its product name. So every model string is checked in all
    /// three places, and all but ASUS are tied to the manufacturer as well, since a four character
    /// code on its own could belong to anyone.
    /// </summary>
    public static HandheldDevice Identify(string? manufacturer, string? product, string? family, string? board)
    {
        bool Maker(string name) => manufacturer?.Contains(name, StringComparison.OrdinalIgnoreCase) == true;

        bool MakerIs(string name) => string.Equals(manufacturer?.Trim(), name, StringComparison.OrdinalIgnoreCase);

        bool Model(params string[] codes) => codes.Any(code =>
            new[] { product, family, board }.Any(s => s?.Contains(code, StringComparison.OrdinalIgnoreCase) == true));

        // RC71 is the Ally, RC72 the Ally X and RC73 the ROG Xbox Ally and Xbox Ally X.
        if (Model("ROG Ally", "ROG Xbox Ally", "RC71", "RC72", "RC73"))
        {
            return HandheldDevice.RogAlly;
        }

        // 83E1 is the Legion Go, 83N0 and 83N1 the Go 2, and the rest the Go S.
        if (Maker("Lenovo") && Model("Legion Go", "83E1", "83N0", "83N1", "83L3", "83N6", "83Q2", "83Q3"))
        {
            return HandheldDevice.LegionGo;
        }

        // The board codes cover the A1M, the 7 and 8 AI+ and the AMD A8, whatever the product name says.
        if (Maker("Micro-Star") && Model("Claw", "MS-1T41", "MS-1T42", "MS-1T52", "MS-1T8K", "MS-1T91"))
        {
            return HandheldDevice.MsiClaw;
        }

        // Jupiter is the LCD Deck and Galileo the OLED one.
        if (Maker("Valve") && Model("Jupiter", "Galileo"))
        {
            return HandheldDevice.SteamDeck;
        }

        if (Maker("AYANEO") || Maker("AYADEVICE") || Maker("AYA NEO") || MakerIs("AYA"))
        {
            return HandheldDevice.Ayaneo;
        }

        if (MakerIs("GPD"))
        {
            return HandheldDevice.Gpd;
        }

        if (Maker("ONE-NETBOOK"))
        {
            return HandheldDevice.OneXPlayer;
        }

        return HandheldDevice.Unknown;
    }
}
