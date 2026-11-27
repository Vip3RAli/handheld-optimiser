# Auto TDP Improvement Notes

A plain-English summary of bugs and improvement opportunities found in the Auto TDP system across `HandheldOptimiser.PowerService`, `HandheldOptimiser.HomeLauncher`, and `HandheldOptimiser.GameBar`.

---

## High-Priority Bugs (Noticeable While Playing)

1. **[x] Judging power changes too early**
   - **Where:** `src/HandheldOptimiser.PowerService/AutoTdpController.cs` (lines 181–218, 663–676)
   - **What happens:** After changing watts, `Change()` clears the reading windows and sets a settle timer (`_nextChange`), but `Tick()` still adds FPS readings into `_readings` and `_longReadings` *before* checking `now < _nextChange`. The controller ends up judging a power change using frames recorded while the chip was still ramping up or down.
   - **Fix:** Ignore FPS readings while `now < _nextChange`, and shorten `SettleAfterRaise` dead-time accordingly so total reaction time stays fast.

2. **[x] Jumping to max watts on easy scenes**
   - **Where:** `src/HandheldOptimiser.PowerService/AutoTdpController.cs` (lines 273–292, 328–330)
   - **What happens:** When leaving a low-FPS menu (held at low watts) into an easy scene that already hits or exceeds the target FPS, `HeldMovedAway()` runs *before* checking `middle < target * Below` and calls `BackToTop(now)`, slamming TDP straight to `MaxWatts` and spiking the fans. Additionally, reaching the target FPS clears `_heldNoGain` and `_atTopSince`, but leaves `_noGainUntil`, `_steppingDown`, `_wasted`, and `_stepDownWait` set.
   - **Fix:** Only call `BackToTop(now)` when FPS is below target; when at or above target, cleanly release the `NoGain` hold state.

3. **[x] Giving up too early on 1 W steps near max watts**
   - **Where:** `src/HandheldOptimiser.PowerService/AutoTdpController.cs` (lines 59, 311–313, 460–496)
   - **What happens:** Near 25–30 W, a +1 W step is only a ~3.5–4% power increase, which typically yields ~1.5–2.5% more FPS—below the fixed 3% `Gain` threshold. Two +1 W steps in a row therefore trigger `NoGain` and roll back even when `MaxWatts` would reach the target. Similarly, `StepUp()` always demands +15% FPS (`StepUpGain`), even when clamped to a +1–2 W step near `ceiling`.
   - **Fix:** Judge cumulative FPS gain from `_wastedFrom` across raises and/or scale the required gain by the relative power increase (`step / fromWatts`).

4. **[x] Learned watts lost on exit or poisoned by quit menus**
   - **Where:** `src/HandheldOptimiser.HomeLauncher/Library/AutoTdp.cs` (lines 115–145), `src/HandheldOptimiser.PowerService/AutoTdpService.Session.cs` (lines 336–365)
   - **What happens:** `StopAsync()` checks `status.State` at the exact instant the game closes. During shutdown, most games stop rendering for at least a second, switching `State` to `Waiting` so `ShouldRememberWatts()` returns `false` and discards what was learned. Conversely, sitting in a 30 fps quit menu lets the controller settle to ~8 W (`NoGain`) and overwrites the real gameplay wattage.
   - **Fix:** Track a session-level settled gameplay wattage (e.g., median or EMA of `Watts` while in `Holding`/`AtMaximum`) in the service and return it on `Stop`.

5. **[x] AC Boost Bursts confusing Auto TDP**
   - **Where:** `src/HandheldOptimiser.PowerService/PowerLimits.cs` (lines 33–43), `src/HandheldOptimiser.PowerService/AutoTdpService.cs` (lines 705–720)
   - **What happens:** Every 5 seconds, `Apply()` sets `sPPT = watts + 13` (up to 43 W) and `fPPT = watts + 23` (up to 53 W) when `AcBoostBursts` is enabled. This lets the APU draw far more than `controller.Watts`, tricking Auto TDP into lowering `SPL` toward 7 W until the boost budget runs out or temperature hits `TemperatureLimit - 3` (which abruptly flattens `sPPT`/`fPPT` back to `SPL`).
   - **Fix:** Limit AC boost bursts to `FixedWatts` mode or while `State == Waiting` (loading/shader compilation), or keep `sPPT` flat during closed-loop Auto TDP control.

6. **[x] Per-game "Off" profile overridden by Auto mode**
   - **Where:** `src/HandheldOptimiser.HomeLauncher/Library/GameSessionController.cs` (lines 376–384), `src/HandheldOptimiser.PowerService/AutoTdpService.cs` (lines 373–408)
   - **What happens:** When a game profile explicitly turns Auto TDP off, `StopIfRunningAsync()` releases `_control`. If the service stays running (e.g., replacing Armoury Crate, overlay active, or within `IdleExit`) and Mode is Auto, `StartForRunningGame()` detects the game in `_playerLibrary` and restarts Auto TDP ~10 seconds later. Conversely, Auto mode with `AutoTdpFps == 0` and default fans doesn't start the service from `GameSessionController` at all.
   - **Fix:** Respect explicit per-game "Off" profiles in the service (or pass an explicit suppress/off signal for that folder) and start Auto sessions cleanly when Auto mode is active.

---

## Smaller Logic Bugs & Cleanup

7. **[x] Stale internal counters across scenes**
   - **Where:** `src/HandheldOptimiser.PowerService/AutoTdpController.cs` (lines 474–497, 512–524, 557–560)
   - **What happens:** `_wasted` is not reset when hitting target FPS, lowering watts, pausing, or throttling for heat—so a single failed raise at 18 W followed later by a failed raise at 12 W can apply `_wastedFrom = 18` as `_noGainCap`. Also `_topFps` is not cleared in `BackToTop()` or after a successful `Step.Up`.
   - **Fix:** Reset `_wasted = 0` on target met, lowering, pause, and heat throttle; clear `_topFps` when resetting to top.

8. **[x] Unnecessary 25% power drops while heat-limited**
   - **Where:** `src/HandheldOptimiser.PowerService/AutoTdpController.cs` (lines 303–307, 381–388)
   - **What happens:** When `Watts >= ceiling` is caused by `_heatCeiling` or `_rememberedHeat` instead of `MaxWatts`, the controller reports `AtMaximum` and runs `StepDown()` after a minute—dropping power by 25% on a game that is already short of FPS due to heat. Also `CoolDown()` always cuts by a fixed `2 W` every `5 s` regardless of how far temperature is above `TemperatureLimit`.
   - **Fix:** Skip `StepDown()` probes when capped by heat, and scale thermal cuts with the temperature overshoot.

9. **[x] Dead code & divergent request parsing**
   - **Where:** `src/HandheldOptimiser.PowerService/AutoTdpController.cs` (line 343 `Step.Lower`), `src/HandheldOptimiser.PowerService/AutoTdpRules.cs` (lines 21–35) vs `src/HandheldOptimiser.PowerService/AutoTdpService.Session.cs` (lines 286–303)
   - **What happens:** `Step.Lower` is assigned to `_judging` on every lower, but `Judge()` has no handler for it. Meanwhile, `AutoTdpRules.FromRequest()` is only called by unit tests while production `Begin()` uses a separate private `Settings()` helper with different zero-value defaults (`DefaultMaxWatts = 25`, `DefaultTemperature = 80`).
   - **Fix:** Remove or implement `Step.Lower` judging, and unify `FromRequest()` with `Settings()` so tests cover the live parsing path.

10. **[x] Controller wiped on charger plug/unplug & redundant temperature reads**
    - **Where:** `src/HandheldOptimiser.PowerService/AutoTdpService.cs` (lines 419, 446, 466–490, 582–595, 711)
    - **What happens:** Plugging or unplugging the charger runs one tick under the old `MaxWatts`, then `TickAuto()` constructs a brand-new `AutoTdpController`, discarding `_floor`, `_rememberedHeat`, and rolling windows instead of updating `MaxWatts` in place. Also `ReadTemperature()` is called up to 3 times per 1-second tick.
    - **Fix:** Update `MaxWatts` on the existing controller when AC/battery state changes, and read temperature once per tick.

---

## Quality-of-Life & Accuracy Upgrades

11. **[x] Frame-time / 1%-low awareness using ETW QPC timestamps**
    - **Where:** `src/HandheldOptimiser.PowerService/FrameCounter.cs` (lines 127–158, 222, 309–369)
    - **What happens:** `FrameCounter` already enables QPC timestamps (`Wnode.ClientContext = 1`) on present events, but ignores `EVENT_HEADER.TimeStamp` and only counts events per tick. Using QPC inter-frame intervals would remove 100 ms ETW flush-boundary jitter and expose P95/P99 frametimes (1% lows) so TDP isn't lowered when average FPS looks fine but frametimes are stuttering.

12. **[x] Faster descent on FPS-capped games & backoff on `NoGain` checks**
    - **Where:** `src/HandheldOptimiser.PowerService/AutoTdpController.cs` (lines 331–346, 530–536)
    - **What happens:** On an FPS-capped game (`spare ≈ 0`), stepping down is fixed at 1 W every 10 seconds (~3 minutes to drop from 30 W to 12 W). Gently accelerating consecutive clean lowers (e.g., 1 W → 2 W after 3 dip-free lowers) speeds up settling. Similarly, `NoGainHold` is always 60 seconds with no backoff; backing off (1 → 2 → 4 min) avoids waking up the fans every minute during long CPU-bound scenes.

13. **[x] Monotonic clock instead of `DateTime.UtcNow`**
    - **Where:** `AutoTdpController`, `FrameCounter`, `AutoTdpService`, `GameLock`, `FineFans`, `DynamicFans`
    - **What happens:** NTP clock syncs or sleep/wake jumps can skew `FrameCounter.Take()` FPS calculations or prematurely trigger/freeze settle, `NoGameExit`, and `IdleExit` timers. Using a monotonic clock (`Stopwatch.GetTimestamp()` / `TimeProvider` or `Environment.TickCount64`) avoids wall-clock jumps.

14. **[x] Absolute-watt fan floors & Game Bar FPS stops**
    - **Where:** `src/HandheldOptimiser.PowerService/FineFans.cs` (line 67), `src/HandheldOptimiser.PowerService/DynamicFans.cs` (line 46), `src/HandheldOptimiser.GameBar/TdpWidget.xaml.cs` (line 50)
    - **What happens:** `FineFans` and `DynamicFans` trigger their high-power fan floor at `70% of topWatts`, forcing higher fan speeds at 18 W on battery (25 W top) or 11 W under a 15 W manual cap while allowing step 0 at 20 W plugged in (30 W top). Also `TdpWidget.FrameRateStops` (`{ 0, 30, 40, 45, 60 }`) omits the `50, 72, 90, 120` fps targets offered in `AutoTdp.FrameRates`.
