using System.IO;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The library's own store sign-ins, kept encrypted on disk. GOG stands in for any store here, so the Steam
/// sign-in that other tests may read is never touched.
/// </summary>
public sealed class StoreSessionsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ho-sessions-" + Guid.NewGuid().ToString("N"));
    private readonly string _before = StoreSessions.Folder;

    public StoreSessionsTests() => StoreSessions.Folder = _folder;

    public void Dispose()
    {
        StoreSessions.Folder = _before;
        StoreSessions.Forget(GameStore.Gog);
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static StoreSession Session(bool needsSignIn = false) => new()
    {
        Store = GameStore.Gog,
        AccountId = "12345",
        AccountName = "Player One",
        RefreshToken = "refresh-secret",
        AccessToken = "access-secret",
        AccessExpires = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000),
        NeedsSignIn = needsSignIn,
    };

    [Fact]
    public void A_saved_sign_in_reads_back_the_same_and_is_not_plain_text_on_disk()
    {
        Assert.True(StoreSessions.Save(Session()));
        StoreSessions.Forget(GameStore.Gog);

        Assert.Equal(Session(), StoreSessions.Get(GameStore.Gog));
        Assert.True(StoreSessions.SignedIn(GameStore.Gog));

        var bytes = File.ReadAllBytes(Path.Combine(_folder, "gog.bin"));
        Assert.DoesNotContain("refresh-secret", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("refresh-secret", System.Text.Encoding.Unicode.GetString(bytes));
    }

    [Fact]
    public void Signing_out_deletes_the_file()
    {
        StoreSessions.Save(Session());
        StoreSessions.Delete(GameStore.Gog);

        Assert.Null(StoreSessions.Get(GameStore.Gog));
        Assert.False(File.Exists(Path.Combine(_folder, "gog.bin")));
    }

    [Fact]
    public void A_broken_file_is_as_good_as_signed_out()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "gog.bin"), [1, 2, 3, 4]);
        StoreSessions.Forget(GameStore.Gog);

        Assert.Null(StoreSessions.Get(GameStore.Gog));
    }

    [Fact]
    public void A_sign_in_the_store_refused_is_kept_but_not_signed_in()
    {
        StoreSessions.Save(Session(needsSignIn: true));

        Assert.NotNull(StoreSessions.Get(GameStore.Gog));
        Assert.False(StoreSessions.SignedIn(GameStore.Gog));
        Assert.Equal("Sign in again", StoreSessions.Get(GameStore.Gog)!.Describe());
    }

    [Fact]
    public void The_row_names_the_account()
    {
        Assert.Equal("Signed in as Player One", Session().Describe());
        Assert.Equal("Signed in", (Session() with { AccountName = null }).Describe());
    }

    [Fact]
    public void An_access_token_near_its_end_is_not_valid()
    {
        var now = DateTimeOffset.UtcNow;
        var session = Session() with { AccessExpires = now.AddMinutes(5) };

        Assert.False(session.AccessValid(now, TimeSpan.FromMinutes(10)));
        Assert.True(session.AccessValid(now, TimeSpan.FromMinutes(1)));
        Assert.False((session with { AccessToken = null }).AccessValid(now, TimeSpan.Zero));
    }
}
