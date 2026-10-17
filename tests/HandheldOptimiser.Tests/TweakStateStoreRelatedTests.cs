using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// What is checked again after a single toggle, and how many PowerShell checks may run at once. After
/// a toggle only the tweak itself and the ones that share a registry key with it are asked about, and
/// script checks queue for a few slots rather than all starting together.
/// </summary>
public sealed class TweakStateStoreRelatedTests
{
    private const string SharedKey = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";

    private static Tweak MakeTweak(
        string id,
        IReadOnlyList<RegistryValueSpec>? values = null,
        IReadOnlyList<RegistryValueSpec>? scriptValues = null,
        bool script = false) => new()
    {
        Id = id,
        Name = id,
        Description = "Test tweak",
        Category = TweakCategory.Games,
        RegistryValues = values ?? [],
        ScriptRegistryValues = scriptValues ?? [],

        // Never run: the store is handed its own check below. Only whether there is one matters.
        ScriptDetect = script ? (_, _) => Task.FromResult(TweakState.Unknown) : null
    };

    private static RegistryValueSpec Spec(string subKey, string valueName, RegistryRoot root = RegistryRoot.LocalMachine) =>
        new(root, subKey, valueName, 0);

    /// <summary>
    /// Checks that answer only when the test says so. Safe to call from any thread, since a check that
    /// waited for a slot starts wherever the slot was given up.
    /// </summary>
    private sealed class Checks
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, List<TaskCompletionSource<TweakState>>> _asked = [];
        private int _running;

        public int MostAtOnce { get; private set; }

        public Task<TweakState> Ask(Tweak tweak, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<TweakState>();

            lock (_lock)
            {
                if (!_asked.TryGetValue(tweak.Id, out var list))
                {
                    _asked[tweak.Id] = list = [];
                }

                list.Add(answer);
                _running++;
                MostAtOnce = Math.Max(MostAtOnce, _running);
            }

            return answer.Task;
        }

        public int TimesAsked(Tweak tweak)
        {
            lock (_lock)
            {
                return _asked.TryGetValue(tweak.Id, out var list) ? list.Count : 0;
            }
        }

        public int TotalAsked
        {
            get
            {
                lock (_lock)
                {
                    return _asked.Values.Sum(list => list.Count);
                }
            }
        }

        public void Answer(Tweak tweak, TweakState state, int question = 0)
        {
            TaskCompletionSource<TweakState> answer;

            lock (_lock)
            {
                answer = _asked[tweak.Id][question];
                _running--;
            }

            answer.SetResult(state);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "Timed out waiting for the checks.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Tweaks_that_write_under_the_same_key_are_related()
    {
        var changed = MakeTweak("changed", [Spec(SharedKey, "AllowGameDVR")]);
        var sameKey = MakeTweak("same.key", [Spec(SharedKey.ToUpperInvariant(), "SomethingElse")]);
        var viaScript = MakeTweak("via.script", scriptValues: [Spec(SharedKey, "AllowGameDVR")]);
        var otherRoot = MakeTweak("other.root", [Spec(SharedKey, "AllowGameDVR", RegistryRoot.CurrentUser)]);
        var unrelated = MakeTweak("unrelated", [Spec(@"SOFTWARE\Something\Else", "AllowGameDVR")]);
        var scriptOnly = MakeTweak("script.only", script: true);

        var store = new TweakStateStore([changed, sameKey, viaScript, otherRoot, unrelated, scriptOnly], new Checks().Ask);

        Assert.Equal(
            ["changed", "same.key", "via.script"],
            store.RelatedTo([changed]).Select(t => t.Id));
    }

    [Fact]
    public void A_tweak_with_no_registry_values_is_related_only_to_itself()
    {
        var scriptOnly = MakeTweak("script.only", script: true);
        var other = MakeTweak("other", [Spec(SharedKey, "AllowGameDVR")]);

        var store = new TweakStateStore([scriptOnly, other], new Checks().Ask);

        Assert.Equal(["script.only"], store.RelatedTo([scriptOnly]).Select(t => t.Id));
    }

    [Fact]
    public void Related_tweaks_are_found_by_id_using_the_store_s_own_definitions()
    {
        // The definitions hand out a new object each time they are read, and one passed in may have
        // been built without its values.
        var stored = MakeTweak("changed", [Spec(SharedKey, "AllowGameDVR")]);
        var sibling = MakeTweak("sibling", [Spec(SharedKey, "Other")]);
        var store = new TweakStateStore([stored, sibling], new Checks().Ask);

        Assert.Equal(["changed", "sibling"], store.RelatedTo([MakeTweak("changed")]).Select(t => t.Id));
    }

    [Fact]
    public void Checking_after_a_toggle_asks_only_about_related_tweaks_and_waits_only_for_the_changed_one()
    {
        var changed = MakeTweak("changed", [Spec(SharedKey, "AllowGameDVR")]);
        var related = MakeTweak("related", [Spec(SharedKey, "Other")]);
        var unrelated = MakeTweak("unrelated", [Spec(@"SOFTWARE\Something\Else", "Value")]);
        var checks = new Checks();
        var store = new TweakStateStore([changed, related, unrelated], checks.Ask);

        var wait = store.CheckRelatedAsync([changed]);

        Assert.Equal(1, checks.TimesAsked(changed));
        Assert.Equal(1, checks.TimesAsked(related));
        Assert.Equal(0, checks.TimesAsked(unrelated));

        checks.Answer(changed, TweakState.Applied);

        Assert.True(wait.IsCompleted);
        Assert.True(store.IsChecking(related));
    }

    [Fact]
    public async Task No_more_than_three_script_checks_run_at_once()
    {
        var tweaks = Enumerable.Range(1, 7).Select(i => MakeTweak($"script.{i}", script: true)).ToList();
        var checks = new Checks();
        var store = new TweakStateStore(tweaks, checks.Ask);

        var all = store.CheckAllAsync();

        Assert.Equal(TweakStateStore.DefaultScriptSlots, checks.TotalAsked);

        // Each answer frees a slot for one of those waiting, in whatever order they get it.
        var answered = new HashSet<string>();
        while (answered.Count < tweaks.Count)
        {
            Tweak? next = null;
            await WaitUntil(() => (next = tweaks.FirstOrDefault(t => !answered.Contains(t.Id) && checks.TimesAsked(t) == 1)) is not null);

            var tweak = next!;
            answered.Add(tweak.Id);
            checks.Answer(tweak, TweakState.Applied);
        }

        await all.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(tweaks.Count, checks.TotalAsked);
        Assert.Equal(TweakStateStore.DefaultScriptSlots, checks.MostAtOnce);
        Assert.All(tweaks, t => Assert.Equal(TweakState.Applied, store.StateOf(t)));
    }

    [Fact]
    public void A_registry_only_check_does_not_wait_behind_script_checks()
    {
        var scripts = Enumerable.Range(1, 4).Select(i => MakeTweak($"script.{i}", script: true)).ToList();
        var registryOnly = MakeTweak("registry", [Spec(SharedKey, "AllowGameDVR")]);
        var checks = new Checks();
        var store = new TweakStateStore([.. scripts, registryOnly], checks.Ask);

        _ = store.CheckAllAsync();

        Assert.Equal(1, checks.TimesAsked(registryOnly));
        Assert.Equal(0, checks.TimesAsked(scripts[3]));
    }

    [Fact]
    public async Task A_queued_check_that_a_newer_one_replaced_never_starts()
    {
        // One slot, held by the first tweak. The second is asked twice while it waits; only the newer
        // question is worth a PowerShell run.
        var holder = MakeTweak("holder", script: true);
        var queued = MakeTweak("queued", script: true);
        var checks = new Checks();
        var store = new TweakStateStore([holder, queued], checks.Ask, scriptSlots: 1);

        _ = store.CheckAsync([holder]);
        _ = store.CheckAsync([queued]);
        var newest = store.CheckAsync([queued]);

        Assert.Equal(0, checks.TimesAsked(queued));

        checks.Answer(holder, TweakState.Applied);

        await WaitUntil(() => checks.TimesAsked(queued) == 1);
        checks.Answer(queued, TweakState.NotApplied);

        await newest.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, checks.TimesAsked(queued));
        Assert.Equal(TweakState.NotApplied, store.StateOf(queued));
    }
}
