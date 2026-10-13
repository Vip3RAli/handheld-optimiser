using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The one place every page reads tweak states from. Checks run in the background and can overlap
/// (the scan at start-up, a re-check, the check after a tweak is applied), so what matters here is
/// which answer is kept.
/// </summary>
public sealed class TweakStateStoreTests
{
    private static Tweak MakeTweak(string id, bool oneClick = true) => new()
    {
        Id = id,
        Name = id,
        Description = "Test tweak",
        Category = TweakCategory.Gaming,
        IncludeInOneClick = oneClick
    };

    /// <summary>Checks that answer only when the test says so, in the order they were asked.</summary>
    private sealed class Checks
    {
        private readonly Dictionary<string, List<TaskCompletionSource<TweakState>>> _asked = [];

        public Task<TweakState> Ask(Tweak tweak, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<TweakState>();
            if (!_asked.TryGetValue(tweak.Id, out var list))
            {
                _asked[tweak.Id] = list = [];
            }

            list.Add(answer);
            return answer.Task;
        }

        public int TimesAsked(Tweak tweak) => _asked.TryGetValue(tweak.Id, out var list) ? list.Count : 0;

        public void Answer(Tweak tweak, TweakState state, int question = 0) => _asked[tweak.Id][question].SetResult(state);

        public void Fail(Tweak tweak, Exception ex, int question = 0) => _asked[tweak.Id][question].SetException(ex);

        public void Cancel(Tweak tweak, int question = 0) => _asked[tweak.Id][question].SetCanceled();
    }

    [Fact]
    public void A_tweak_has_no_state_until_its_check_answers()
    {
        var tweak = MakeTweak("a");
        var checks = new Checks();
        var store = new TweakStateStore([tweak], checks.Ask);

        Assert.Null(store.StateOf(tweak));
        Assert.False(store.IsChecking(tweak));

        var check = store.CheckAsync([tweak]);

        Assert.Null(store.StateOf(tweak));
        Assert.True(store.IsChecking(tweak));
        Assert.False(check.IsCompleted);

        checks.Answer(tweak, TweakState.Applied);

        Assert.Equal(TweakState.Applied, store.StateOf(tweak));
        Assert.False(store.IsChecking(tweak));
        Assert.True(check.IsCompleted);
    }

    [Fact]
    public void The_answer_to_the_newest_question_is_the_one_kept()
    {
        // Asked at start-up, then asked again after the tweak was applied. The first check is slow and
        // answers last, with what was true before the change.
        var tweak = MakeTweak("a");
        var checks = new Checks();
        var store = new TweakStateStore([tweak], checks.Ask);

        _ = store.CheckAsync([tweak]);
        _ = store.CheckAsync([tweak]);

        checks.Answer(tweak, TweakState.Applied, question: 1);
        Assert.Equal(TweakState.Applied, store.StateOf(tweak));
        Assert.False(store.IsChecking(tweak));

        checks.Answer(tweak, TweakState.NotApplied, question: 0);
        Assert.Equal(TweakState.Applied, store.StateOf(tweak));
    }

    [Fact]
    public void A_tweak_is_still_being_checked_while_its_newest_question_is_out()
    {
        var tweak = MakeTweak("a");
        var checks = new Checks();
        var store = new TweakStateStore([tweak], checks.Ask);

        _ = store.CheckAsync([tweak]);
        var second = store.CheckAsync([tweak]);

        checks.Answer(tweak, TweakState.NotApplied, question: 0);

        Assert.True(store.IsChecking(tweak));
        Assert.Null(store.StateOf(tweak));
        Assert.False(second.IsCompleted);

        checks.Answer(tweak, TweakState.Applied, question: 1);

        Assert.False(store.IsChecking(tweak));
        Assert.True(second.IsCompleted);
    }

    [Fact]
    public void A_check_that_fails_reads_as_unknown_and_is_reported()
    {
        var broken = MakeTweak("broken");
        var fine = MakeTweak("fine");
        var checks = new Checks();
        var reported = new List<string>();
        var store = new TweakStateStore([broken, fine], checks.Ask, (tweak, ex) => reported.Add($"{tweak.Id}: {ex.Message}"));

        var check = store.CheckAsync([broken, fine]);

        checks.Fail(broken, new InvalidOperationException("no answer"));
        checks.Answer(fine, TweakState.Applied);

        Assert.True(check.IsCompletedSuccessfully);
        Assert.Equal(TweakState.Unknown, store.StateOf(broken));
        Assert.Equal(TweakState.Applied, store.StateOf(fine));
        Assert.Equal(["broken: no answer"], reported);
    }

    [Fact]
    public void A_cancelled_check_keeps_what_was_known()
    {
        var tweak = MakeTweak("a");
        var checks = new Checks();
        var store = new TweakStateStore([tweak], checks.Ask);

        _ = store.CheckAsync([tweak]);
        checks.Answer(tweak, TweakState.Applied);

        var second = store.CheckAsync([tweak]);
        checks.Cancel(tweak, question: 1);

        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(TweakState.Applied, store.StateOf(tweak));
        Assert.False(store.IsChecking(tweak));
    }

    [Fact]
    public void Checking_some_tweaks_does_not_ask_about_the_others()
    {
        var a = MakeTweak("a");
        var b = MakeTweak("b");
        var checks = new Checks();
        var store = new TweakStateStore([a, b], checks.Ask);

        _ = store.CheckAsync([a]);

        Assert.Equal(1, checks.TimesAsked(a));
        Assert.Equal(0, checks.TimesAsked(b));
        Assert.False(store.IsChecking(b));
    }

    [Fact]
    public void Checking_everything_can_wait_for_only_the_tweaks_just_changed()
    {
        // After one tweak is applied everything is checked again, but the page only has to wait for
        // that tweak's own answer before it lets go.
        var changed = MakeTweak("changed");
        var slow = MakeTweak("slow");
        var checks = new Checks();
        var store = new TweakStateStore([changed, slow], checks.Ask);

        var wait = store.CheckAllAsync(waitFor: [changed]);

        Assert.Equal(1, checks.TimesAsked(slow));
        Assert.False(wait.IsCompleted);

        checks.Answer(changed, TweakState.Applied);

        Assert.True(wait.IsCompleted);
        Assert.True(store.IsChecking(slow));
    }

    [Fact]
    public void Checking_everything_waits_for_everything_when_nothing_is_named()
    {
        var a = MakeTweak("a");
        var b = MakeTweak("b");
        var checks = new Checks();
        var store = new TweakStateStore([a, b], checks.Ask);

        var wait = store.CheckAllAsync();

        checks.Answer(a, TweakState.Applied);
        Assert.False(wait.IsCompleted);

        checks.Answer(b, TweakState.NotApplied);
        Assert.True(wait.IsCompleted);
    }

    [Fact]
    public void Checking_what_is_missing_asks_only_about_tweaks_never_asked_about()
    {
        // The Dashboard asks for its own tweaks as it opens, a moment before the scan at start-up asks
        // for the rest. Neither may start a second check for a tweak the other has out.
        var asked = MakeTweak("asked");
        var answered = MakeTweak("answered");
        var never = MakeTweak("never");
        var checks = new Checks();
        var store = new TweakStateStore([asked, answered, never], checks.Ask);

        _ = store.CheckAsync([asked, answered]);
        checks.Answer(answered, TweakState.NotApplied);

        var missing = store.CheckMissingAsync([asked, answered, never]);

        Assert.Equal(1, checks.TimesAsked(asked));
        Assert.Equal(1, checks.TimesAsked(answered));
        Assert.Equal(1, checks.TimesAsked(never));
        Assert.False(missing.IsCompleted);

        checks.Answer(never, TweakState.Applied);
        Assert.True(missing.IsCompleted);
    }

    [Fact]
    public void Each_change_is_announced_for_its_own_tweak()
    {
        var a = MakeTweak("a");
        var b = MakeTweak("b");
        var checks = new Checks();
        var store = new TweakStateStore([a, b], checks.Ask);
        var announced = new List<string>();
        store.Changed += tweak => announced.Add($"{tweak.Id}:{store.StateOf(tweak)?.ToString() ?? "none"}:{store.IsChecking(tweak)}");

        _ = store.CheckAsync([a]);
        checks.Answer(a, TweakState.Partial);

        Assert.Equal(["a:none:True", "a:Partial:False"], announced);
    }

    [Fact]
    public void A_stale_answer_is_not_announced()
    {
        var tweak = MakeTweak("a");
        var checks = new Checks();
        var store = new TweakStateStore([tweak], checks.Ask);

        _ = store.CheckAsync([tweak]);
        _ = store.CheckAsync([tweak]);
        checks.Answer(tweak, TweakState.Applied, question: 1);

        var announced = 0;
        store.Changed += _ => announced++;
        checks.Answer(tweak, TweakState.NotApplied, question: 0);

        Assert.Equal(0, announced);
    }

    [Fact]
    public void A_tweak_is_found_by_its_id_not_by_the_object()
    {
        // The definitions hand out a new object each time they are read.
        var checks = new Checks();
        var store = new TweakStateStore([MakeTweak("a")], checks.Ask);

        _ = store.CheckAsync([MakeTweak("a")]);
        checks.Answer(MakeTweak("a"), TweakState.Applied);

        Assert.Equal(TweakState.Applied, store.StateOf(MakeTweak("a")));
    }

    [Fact]
    public void The_summary_counts_what_is_applied_and_says_when_it_is_still_waiting()
    {
        var a = MakeTweak("a");
        var b = MakeTweak("b");
        var c = MakeTweak("c");
        var checks = new Checks();
        var store = new TweakStateStore([a, b, c], checks.Ask);

        Assert.Equal(new TweakSummary(Applied: 0, Answered: 0, Total: 3), store.Summarise([a, b, c]));

        _ = store.CheckAllAsync();
        checks.Answer(a, TweakState.Applied);
        checks.Answer(b, TweakState.Partial);

        var waiting = store.Summarise([a, b, c]);
        Assert.Equal(new TweakSummary(Applied: 1, Answered: 2, Total: 3), waiting);
        Assert.False(waiting.IsComplete);

        checks.Answer(c, TweakState.Applied);

        var done = store.Summarise([a, b, c]);
        Assert.Equal(new TweakSummary(Applied: 2, Answered: 3, Total: 3), done);
        Assert.True(done.IsComplete);
    }
}
