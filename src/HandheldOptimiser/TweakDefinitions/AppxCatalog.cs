using HandheldOptimiser.Models;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// The curated bloatware list. Deliberately a fixed catalog rather than "remove everything not on an
/// allowlist": the user sees each package ID before it goes, and a new Windows build cannot silently
/// widen the blast radius.
///
/// Anything ASUS, Lenovo, MSI, AMD, Intel, Realtek, Xbox/Game Pass, Store or codec-related is absent here by design and
/// additionally blocked at removal time by <see cref="Services.SafetyGuard"/>.
/// </summary>
public static class AppxCatalog
{
    public static IReadOnlyList<AppxTarget> All =>
    [
        // ---- Third-party preinstalls: pure shovelware, nothing depends on them ----
        new() { IdentityName = "BytedancePte.Ltd.TikTok",           FriendlyName = "TikTok",              Group = AppxGroup.ThirdPartyPreinstall, Recommended = true },
        new() { IdentityName = "Facebook.InstagramBeta",            FriendlyName = "Instagram",           Group = AppxGroup.ThirdPartyPreinstall, Recommended = true },
        new() { IdentityName = "Facebook.Facebook",                 FriendlyName = "Facebook",            Group = AppxGroup.ThirdPartyPreinstall, Recommended = true },
        new() { IdentityName = "5319275A.WhatsAppDesktop",          FriendlyName = "WhatsApp",            Group = AppxGroup.ThirdPartyPreinstall, Recommended = true, KeepIfNote = "you use WhatsApp on this device." },
        new() { IdentityName = "SpotifyAB.SpotifyMusic",            FriendlyName = "Spotify",             Group = AppxGroup.ThirdPartyPreinstall, Recommended = true, KeepIfNote = "you listen to Spotify while gaming." },
        new() { IdentityName = "king.com.CandyCrushSaga",           FriendlyName = "Candy Crush Saga",    Group = AppxGroup.ThirdPartyPreinstall, Recommended = true },
        new() { IdentityName = "king.com.CandyCrushSodaSaga",       FriendlyName = "Candy Crush Soda",    Group = AppxGroup.ThirdPartyPreinstall, Recommended = true },
        new() { IdentityName = "Disney.37853FC22B2CE",              FriendlyName = "Disney+",             Group = AppxGroup.ThirdPartyPreinstall, Recommended = true },
        new() { IdentityName = "4DF9E0F8.Netflix",                  FriendlyName = "Netflix",             Group = AppxGroup.ThirdPartyPreinstall, Recommended = true },
        new() { IdentityName = "AmazonVideo.PrimeVideo",            FriendlyName = "Prime Video",         Group = AppxGroup.ThirdPartyPreinstall, Recommended = true },
        new() { IdentityName = "9E2F88E3.Twitter",                  FriendlyName = "X (Twitter)",         Group = AppxGroup.ThirdPartyPreinstall, Recommended = true },
        new() { IdentityName = "LinkedInCorporation.LinkedIn",      FriendlyName = "LinkedIn",            Group = AppxGroup.ThirdPartyPreinstall, Recommended = true },
        new() { IdentityName = "7EE7776C.LinkedInforWindows",       FriendlyName = "LinkedIn (other version)", Group = AppxGroup.ThirdPartyPreinstall, Recommended = true },
        new() { IdentityName = "Duolingo-LearnLanguagesforFree",    FriendlyName = "Duolingo",            Group = AppxGroup.ThirdPartyPreinstall, Recommended = true },
        new() { IdentityName = "AdobeSystemsIncorporated.AdobeLightroom", FriendlyName = "Adobe Lightroom", Group = AppxGroup.ThirdPartyPreinstall, Recommended = true },
        new() { IdentityName = "AdobeSystemsIncorporated.AdobeExpress",   FriendlyName = "Adobe Express",   Group = AppxGroup.ThirdPartyPreinstall, Recommended = true },
        new() { IdentityName = "Clipchamp.Clipchamp",               FriendlyName = "Clipchamp",           Group = AppxGroup.ThirdPartyPreinstall, Recommended = true, KeepIfNote = "you edit clips on the device itself." },
        new() { IdentityName = "ByteDance.CapCut",                  FriendlyName = "CapCut",              Group = AppxGroup.ThirdPartyPreinstall, Recommended = true, KeepIfNote = "you edit video with CapCut." },
        new() { IdentityName = "DropboxInc.Dropbox",                FriendlyName = "Dropbox",             Group = AppxGroup.ThirdPartyPreinstall, Recommended = true, KeepIfNote = "you use Dropbox for file sync." },
        new() { IdentityName = "McAfee.Security",                   FriendlyName = "McAfee Security",     Group = AppxGroup.ThirdPartyPreinstall, Recommended = true },

        // ---- Microsoft promotional / news & ad surfaces ----
        new() { IdentityName = "Microsoft.BingNews",                FriendlyName = "Bing News",           Group = AppxGroup.MicrosoftPromotional, Recommended = true },
        new() { IdentityName = "Microsoft.News",                    FriendlyName = "Microsoft News",      Group = AppxGroup.MicrosoftPromotional, Recommended = true },
        new() { IdentityName = "Microsoft.BingWeather",             FriendlyName = "Bing Weather",        Group = AppxGroup.MicrosoftPromotional, Recommended = true, KeepIfNote = "you use the weather widget." },
        new() { IdentityName = "Microsoft.Weather",                 FriendlyName = "Weather (other version)", Group = AppxGroup.MicrosoftPromotional, Recommended = true, KeepIfNote = "you use the weather widget." },
        new() { IdentityName = "Microsoft.BingFinance",             FriendlyName = "Bing Finance",        Group = AppxGroup.MicrosoftPromotional, Recommended = true },
        new() { IdentityName = "Microsoft.BingSports",              FriendlyName = "Bing Sports",         Group = AppxGroup.MicrosoftPromotional, Recommended = true },
        new() { IdentityName = "Microsoft.BingTranslator",          FriendlyName = "Translator",          Group = AppxGroup.MicrosoftPromotional, Recommended = true },
        new() { IdentityName = "Microsoft.MicrosoftOfficeHub",      FriendlyName = "Microsoft 365 app (Office hub)", Group = AppxGroup.MicrosoftPromotional, Recommended = true, KeepIfNote = "you open Microsoft 365 or Office files from it." },
        new() { IdentityName = "Microsoft.MicrosoftSolitaireCollection", FriendlyName = "Solitaire Collection", Group = AppxGroup.MicrosoftPromotional, Recommended = true },
        new() { IdentityName = "Microsoft.OneConnect",              FriendlyName = "Mobile Plans",        Group = AppxGroup.MicrosoftPromotional, Recommended = true },
        new() { IdentityName = "Microsoft.SkypeApp",                FriendlyName = "Skype",               Group = AppxGroup.MicrosoftPromotional, Recommended = true },
        new() { IdentityName = "MicrosoftTeams",                    FriendlyName = "Teams (personal)",    Group = AppxGroup.MicrosoftPromotional, Recommended = true, KeepIfNote = "you use personal Teams chat." },
        new() { IdentityName = "MSTeams",                           FriendlyName = "Teams (new client)",  Group = AppxGroup.MicrosoftPromotional, KeepIfNote = "you use Teams for work." },
        new() { IdentityName = "Microsoft.OutlookForWindows",       FriendlyName = "New Outlook",         Group = AppxGroup.MicrosoftPromotional, KeepIfNote = "you read mail on the handheld." },

        // ---- Assistants ----
        new() { IdentityName = "Microsoft.549981C3F5F10",           FriendlyName = "Cortana",             Group = AppxGroup.Assistant, Recommended = true },
        new() { IdentityName = "Microsoft.Copilot",                 FriendlyName = "Copilot",             Group = AppxGroup.Assistant, Recommended = true },
        new() { IdentityName = "Microsoft.MicrosoftCopilot",        FriendlyName = "Copilot (other version)", Group = AppxGroup.Assistant, Recommended = true },
        new() { IdentityName = "Microsoft.Windows.Ai.Copilot.Provider", FriendlyName = "Copilot background component", Group = AppxGroup.Assistant, Recommended = true },
        new() { IdentityName = "Microsoft.BingSearch",              FriendlyName = "Bing Search (Start web results)", Group = AppxGroup.Assistant, Recommended = true },

        // ---- Utilities and media most handheld users never open ----
        new() { IdentityName = "Microsoft.Microsoft3DViewer",       FriendlyName = "3D Viewer",           Group = AppxGroup.MediaAndUtilities, Recommended = true },
        new() { IdentityName = "Microsoft.MixedReality.Portal",     FriendlyName = "Mixed Reality Portal", Group = AppxGroup.MediaAndUtilities, Recommended = true },
        new() { IdentityName = "Microsoft.MSPaint",                 FriendlyName = "Paint 3D",            Group = AppxGroup.MediaAndUtilities, Recommended = true, KeepIfNote = "you use Paint 3D. Classic Paint is a different app and is left alone." },
        new() { IdentityName = "Microsoft.Print3D",                 FriendlyName = "Print 3D",            Group = AppxGroup.MediaAndUtilities, Recommended = true },
        new() { IdentityName = "Microsoft.People",                  FriendlyName = "People",              Group = AppxGroup.MediaAndUtilities, Recommended = true },
        new() { IdentityName = "Microsoft.WindowsMaps",             FriendlyName = "Maps",                Group = AppxGroup.MediaAndUtilities, Recommended = true },
        new() { IdentityName = "Microsoft.GetHelp",                 FriendlyName = "Get Help",            Group = AppxGroup.MediaAndUtilities, Recommended = true },
        new() { IdentityName = "Microsoft.Getstarted",              FriendlyName = "Tips",                Group = AppxGroup.MediaAndUtilities, Recommended = true },
        new() { IdentityName = "Microsoft.WindowsFeedbackHub",      FriendlyName = "Feedback Hub",        Group = AppxGroup.MediaAndUtilities, Recommended = true },
        new() { IdentityName = "Microsoft.Todos",                   FriendlyName = "Microsoft To Do",     Group = AppxGroup.MediaAndUtilities, Recommended = true },
        new() { IdentityName = "Microsoft.Whiteboard",              FriendlyName = "Whiteboard",          Group = AppxGroup.MediaAndUtilities, Recommended = true },
        new() { IdentityName = "Microsoft.MicrosoftJournal",        FriendlyName = "Journal",             Group = AppxGroup.MediaAndUtilities, Recommended = true },
        new() { IdentityName = "Microsoft.PowerAutomateDesktop",    FriendlyName = "Power Automate",      Group = AppxGroup.MediaAndUtilities, Recommended = true },
        new() { IdentityName = "Microsoft.Windows.DevHome",         FriendlyName = "Dev Home",            Group = AppxGroup.MediaAndUtilities, Recommended = true },
        new() { IdentityName = "MicrosoftCorporationII.QuickAssist", FriendlyName = "Quick Assist",       Group = AppxGroup.MediaAndUtilities, KeepIfNote = "you use it for remote support." },
        new() { IdentityName = "Microsoft.WindowsAlarms",           FriendlyName = "Clock / Alarms",      Group = AppxGroup.MediaAndUtilities },
        new() { IdentityName = "Microsoft.WindowsSoundRecorder",    FriendlyName = "Sound Recorder",      Group = AppxGroup.MediaAndUtilities },
        new() { IdentityName = "Microsoft.MicrosoftStickyNotes",    FriendlyName = "Sticky Notes",        Group = AppxGroup.MediaAndUtilities },
        new() { IdentityName = "Microsoft.YourPhone",               FriendlyName = "Phone Link",          Group = AppxGroup.MediaAndUtilities, KeepIfNote = "you link your phone for notifications." },
        new() { IdentityName = "Microsoft.ZuneMusic",               FriendlyName = "Media Player",        Group = AppxGroup.MediaAndUtilities, KeepIfNote = "you use it to play music or video." },
        new() { IdentityName = "Microsoft.ZuneVideo",               FriendlyName = "Films & TV",          Group = AppxGroup.MediaAndUtilities }
    ];

    public static string GroupHeader(AppxGroup group) => group switch
    {
        AppxGroup.ThirdPartyPreinstall => "Third-party preinstalls",
        AppxGroup.MicrosoftPromotional => "Microsoft promotional apps",
        AppxGroup.Assistant => "Assistants and web search",
        AppxGroup.MediaAndUtilities => "Utilities and media",
        _ => group.ToString()
    };
}
