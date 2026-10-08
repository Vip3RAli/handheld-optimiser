using System.Globalization;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>Reading IsThereAnyDeal's and Steam's answers for the Deals row. Nothing here goes online.</summary>
public sealed class GameDealsTests
{
    private const string DealsAnswer = """
        {
          "nextOffset": 3, "hasMore": true,
          "list": [
            {
              "id": "018d937f-21e1-728e-86d7-9acb3c59f2bb", "slug": "hades", "title": "Hades", "type": "game", "mature": false,
              "assets": { "banner145": "https://img/145.jpg", "banner300": "https://img/300.jpg", "banner400": "https://img/400.jpg" },
              "deal": {
                "shop": { "id": 61, "name": "Steam" },
                "price": { "amount": 6.24, "amountInt": 624, "currency": "GBP" },
                "regular": { "amount": 24.99, "amountInt": 2499, "currency": "GBP" },
                "cut": 75, "voucher": null,
                "historyLow": { "amount": 6.24, "amountInt": 624, "currency": "GBP" },
                "flag": "H", "drm": [], "platforms": [ { "id": 1, "name": "Windows" }, { "id": 2, "name": "Mac" } ],
                "timestamp": "2026-10-01T17:00:00+02:00", "expiry": "2026-10-15T19:00:00+02:00",
                "url": "https://itad.link/abc/"
              }
            },
            {
              "id": "dlc-1", "title": "Hades Soundtrack", "type": "dlc",
              "deal": { "shop": { "name": "Steam" }, "price": { "amount": 1, "currency": "GBP" }, "cut": 50, "url": "https://itad.link/d/" }
            },
            {
              "id": "mac-1", "title": "Mac Only", "type": "game",
              "deal": { "shop": { "name": "GOG" }, "price": { "amount": 1, "currency": "GBP" }, "cut": 50,
                        "platforms": [ { "id": 2, "name": "Mac" } ], "url": "https://itad.link/m/" }
            }
          ]
        }
        """;

    [Fact]
    public void ParseDeals_ReadsGamesThatRunOnWindows()
    {
        var deal = Assert.Single(GameDeals.ParseDeals(DealsAnswer));

        Assert.Equal("Hades", deal.Title);
        Assert.Equal("Steam", deal.Shop);
        Assert.Equal(75, deal.Cut);
        Assert.True(deal.HistoricalLow);
        Assert.False(deal.OnWishlist);
        Assert.Equal("https://itad.link/abc/", deal.Url);
        Assert.Equal("https://img/400.jpg", deal.Image);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 17, 0, 0, TimeSpan.Zero), deal.Expiry);
        Assert.Contains("6", deal.Price);
        Assert.Contains("24", deal.Regular);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "list": {} }""")]
    [InlineData("[]")]
    [InlineData("""{ "list": [ 1, "x", null ] }""")]
    public void ParseDeals_BrokenAnswerListsNothing(string json) => Assert.Empty(GameDeals.ParseDeals(json));

    [Fact]
    public void ParseWishlist_HighestPriorityFirst()
    {
        const string json = """
            { "response": { "items": [
              { "appid": 30, "priority": 3, "date_added": 1 },
              { "appid": 10, "priority": 1, "date_added": 1 },
              { "appid": 20, "priority": 2, "date_added": 1 }
            ] } }
            """;

        Assert.Equal([10L, 20L, 30L], GameDeals.ParseWishlist(json));
        Assert.Empty(GameDeals.ParseWishlist("""{ "response": {} }"""));
    }

    [Fact]
    public void ParseLookup_SkipsGamesItDoesNotKnow()
    {
        var found = GameDeals.ParseLookup("""{ "app/220": "game-a", "app/137730": null }""");

        Assert.Equal([("app/220", "game-a")], found);
    }

    [Fact]
    public void ParsePrices_PicksTheCheapestDealPerGame()
    {
        const string json = """
            [
              { "id": "game-a", "historyLow": {}, "deals": [
                { "shop": { "name": "Steam" }, "price": { "amount": 9.99, "currency": "USD" }, "regular": { "amount": 19.99, "currency": "USD" },
                  "cut": 50, "url": "https://itad.link/steam/" },
                { "shop": { "name": "GOG" }, "price": { "amount": 7.99, "currency": "USD" }, "regular": { "amount": 19.99, "currency": "USD" },
                  "cut": 60, "url": "https://itad.link/gog/" }
              ] },
              { "id": "game-b", "deals": [] }
            ]
            """;

        var (id, price) = Assert.Single(GameDeals.ParsePrices(json));
        Assert.Equal("game-a", id);
        Assert.Equal("GOG", price.Shop);
        Assert.Equal(60, price.Cut);
        Assert.Equal("https://itad.link/gog/", price.Url);
    }

    [Fact]
    public void ParseInfo_ReadsTitleAndBanner()
    {
        var info = GameDeals.ParseInfo("""{ "id": "x", "title": "Portal 2", "assets": { "banner300": "https://img/p.jpg" } }""");

        Assert.Equal(("Portal 2", "https://img/p.jpg"), info);
        Assert.Null(GameDeals.ParseInfo("""{ "id": "x" }"""));
    }

    [Fact]
    public void FormatPrice_UsesTheCurrencysOwnSymbol()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-GB");
            Assert.Equal("£12.99", GameDeals.FormatPrice(12.99m, "GBP", "GB"));
            Assert.Contains("€", GameDeals.FormatPrice(12.99m, "EUR", "DE"));
            Assert.Equal("12.99 XYZ", GameDeals.FormatPrice(12.99m, "XYZ", "GB"));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
