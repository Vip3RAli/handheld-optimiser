using System.Net;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The game library's guards against what comes from the network or a file on disk: only https links
/// from deals, the API keys kept encrypted, no week-long "nothing found" marker for a server's outage,
/// and one request at a time for the same picture. Nothing here goes online or touches the registry.
/// </summary>
public sealed class LibraryHardeningTests
{
    // ----- Deal links -----

    [Theory]
    [InlineData("https://itad.link/abc/")]
    [InlineData("HTTPS://store.epicgames.com/p/hades")]
    public void IsHttpsUrl_AcceptsAbsoluteHttps(string url) => Assert.True(GameDeals.IsHttpsUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://itad.link/abc/")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("//itad.link/abc/")]
    [InlineData("/deals/abc")]
    public void IsHttpsUrl_RefusesAnythingElse(string? url) => Assert.False(GameDeals.IsHttpsUrl(url));

    [Fact]
    public void ParseDeals_LeavesOutADealWhoseLinkIsNotHttps()
    {
        const string answer = """
            {
              "list": [
                { "id": "a", "title": "Safe", "type": "game",
                  "deal": { "shop": { "name": "Steam" }, "price": { "amount": 1, "currency": "GBP" }, "cut": 50,
                            "url": "https://itad.link/a/" } },
                { "id": "b", "title": "Plain http", "type": "game",
                  "deal": { "shop": { "name": "Steam" }, "price": { "amount": 1, "currency": "GBP" }, "cut": 50,
                            "url": "http://itad.link/b/" } },
                { "id": "c", "title": "A file", "type": "game",
                  "deal": { "shop": { "name": "Steam" }, "price": { "amount": 1, "currency": "GBP" }, "cut": 50,
                            "url": "file:///C:/Windows/System32/calc.exe" } }
              ]
            }
            """;

        var deal = Assert.Single(GameDeals.ParseDeals(answer));
        Assert.Equal("a", deal.Id);
    }

    [Fact]
    public void ParseDeals_DropsABannerThatIsNotHttps()
    {
        const string answer = """
            {
              "list": [
                { "id": "a", "title": "Game", "type": "game",
                  "assets": { "banner400": "http://img/400.jpg", "banner300": "https://img/300.jpg" },
                  "deal": { "shop": { "name": "Steam" }, "price": { "amount": 1, "currency": "GBP" }, "cut": 50,
                            "url": "https://itad.link/a/" } }
              ]
            }
            """;

        Assert.Equal("https://img/300.jpg", Assert.Single(GameDeals.ParseDeals(answer)).Image);
    }

    [Fact]
    public void Safe_ChecksDealsReadBackFromDisk()
    {
        var kept = new Deal("a", "Kept", "Steam", "£1", "£2", 50, false, false, "https://itad.link/a/", "http://img/a.jpg", null);
        var edited = kept with { Id = "b", Url = "C:\\Windows\\System32\\calc.exe" };

        var deal = Assert.Single(GameDeals.Safe([kept, edited]));
        Assert.Equal("a", deal.Id);
        Assert.Null(deal.Image);
    }

    // ----- API keys -----

    /// <summary>The registry key's values, as a dictionary.</summary>
    private sealed class FakeStore : LibrarySettings.ISecretStore
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public object? Get(string name) => Values.GetValueOrDefault(name);

        public bool SetBinary(string name, byte[] value)
        {
            Values[name] = value;
            return true;
        }

        public void Delete(string name) => Values.Remove(name);
    }

    [Fact]
    public void Protect_RoundTripsForThisAccount()
    {
        var sealedKey = LibrarySettings.Protect("abc123");

        Assert.NotNull(sealedKey);
        Assert.DoesNotContain("abc123", System.Text.Encoding.UTF8.GetString(sealedKey!));
        Assert.Equal("abc123", LibrarySettings.Unprotect(sealedKey!));
    }

    [Fact]
    public void Unprotect_GivesNullForBytesItDidNotEncrypt() =>
        Assert.Null(LibrarySettings.Unprotect([1, 2, 3, 4]));

    [Fact]
    public void WriteSecret_KeepsOnlyTheEncryptedCopy()
    {
        var store = new FakeStore();
        store.Values["SteamWebApiKey"] = "old plain text";

        LibrarySettings.WriteSecret(store, "SteamWebApiKey", "  new-key  ");

        Assert.False(store.Values.ContainsKey("SteamWebApiKey"));
        Assert.IsType<byte[]>(store.Values["SteamWebApiKeyProtected"]);
        Assert.Equal("new-key", LibrarySettings.ReadSecret(store, "SteamWebApiKey"));
    }

    [Fact]
    public void ReadSecret_MovesAPlainTextKeyToEncryptedStorage()
    {
        var store = new FakeStore();
        store.Values["IsThereAnyDealKey"] = " plain-key ";

        Assert.Equal("plain-key", LibrarySettings.ReadSecret(store, "IsThereAnyDealKey"));

        Assert.False(store.Values.ContainsKey("IsThereAnyDealKey"));
        var sealedKey = Assert.IsType<byte[]>(store.Values["IsThereAnyDealKeyProtected"]);
        Assert.Equal("plain-key", LibrarySettings.Unprotect(sealedKey));

        // Read again, from the encrypted copy alone.
        Assert.Equal("plain-key", LibrarySettings.ReadSecret(store, "IsThereAnyDealKey"));
    }

    [Fact]
    public void ReadSecret_PrefersTheEncryptedCopyAndClearsALeftOverPlainOne()
    {
        var store = new FakeStore();
        store.Values["SteamGridDbKeyProtected"] = LibrarySettings.Protect("current")!;
        store.Values["SteamGridDbKey"] = "stale";

        Assert.Equal("current", LibrarySettings.ReadSecret(store, "SteamGridDbKey"));
        Assert.False(store.Values.ContainsKey("SteamGridDbKey"));
    }

    [Fact]
    public void WriteSecret_WithNothingRemovesBothCopies()
    {
        var store = new FakeStore();
        store.Values["SteamGridDbKey"] = "plain";
        store.Values["SteamGridDbKeyProtected"] = LibrarySettings.Protect("sealed")!;

        LibrarySettings.WriteSecret(store, "SteamGridDbKey", "   ");

        Assert.Empty(store.Values);
        Assert.Null(LibrarySettings.ReadSecret(store, "SteamGridDbKey"));
    }

    // ----- Artwork markers -----

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public void IsTryLater_ForTheServersTrouble(HttpStatusCode status) => Assert.True(Artwork.IsTryLater(status));

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void IsTryLater_NotForAPictureThatIsGone(HttpStatusCode status) => Assert.False(Artwork.IsTryLater(status));

    [Fact]
    public void Combine_AServerErrorAmongTheCandidatesWritesNoMarker()
    {
        var result = Artwork.Combine([Artwork.Fetched.NotFound, Artwork.Fetched.TryLater, Artwork.Fetched.NotFound]);

        Assert.Equal(Artwork.Fetched.TryLater, result);
        Assert.False(Artwork.WritesMarker(result));
    }

    [Fact]
    public void Combine_OnlyNothingFoundWritesAMarker()
    {
        Assert.True(Artwork.WritesMarker(Artwork.Combine([Artwork.Fetched.NotFound, Artwork.Fetched.NotFound])));

        // No candidates at all: SteamGridDB answered with an empty list.
        Assert.True(Artwork.WritesMarker(Artwork.Combine([])));
    }

    [Fact]
    public void Combine_SavedWins() =>
        Assert.Equal(Artwork.Fetched.Saved, Artwork.Combine([Artwork.Fetched.TryLater, Artwork.Fetched.Saved]));

    [Fact]
    public void IsImage_RefusesWhatIsNotAPicture()
    {
        Assert.False(Artwork.IsImage([]));
        Assert.False(Artwork.IsImage(System.Text.Encoding.UTF8.GetBytes("<html>Service Unavailable</html>")));
    }

    // ----- Joining requests -----

    [Fact]
    public async Task InFlight_StartsOneRequestForTheSameKey()
    {
        var fetches = new InFlight<int>();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        Task<int> Start()
        {
            Interlocked.Increment(ref started);
            return gate.Task;
        }

        var first = fetches.RunAsync("steam:620", Start);
        var second = fetches.RunAsync("STEAM:620", Start);
        gate.SetResult(7);

        Assert.Equal(new[] { 7, 7 }, await Task.WhenAll(first, second));
        Assert.Equal(1, started);

        // Once done, the next ask starts afresh.
        Assert.Equal(8, await fetches.RunAsync("steam:620", () => Task.FromResult(8)));
    }

    // ----- Errors -----

    [Fact]
    public void IsFatal_OnlyForErrorsThatLeaveNothingToCarryOnWith()
    {
        Assert.True(LibraryApp.IsFatal(new OutOfMemoryException()));
        Assert.True(LibraryApp.IsFatal(new InvalidOperationException("wrapped", new OutOfMemoryException())));
        Assert.False(LibraryApp.IsFatal(new InvalidOperationException()));
        Assert.False(LibraryApp.IsFatal(new FormatException()));
    }
}
