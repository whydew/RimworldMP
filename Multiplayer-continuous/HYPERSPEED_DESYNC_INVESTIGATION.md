# Hyperspeed desync — investigation & recommendations (no code changed)

**Date:** 2026-08-22
**Scope:** Why the new top speed tier (Hyperspeed, 25×) desyncs even when both players run the identical modified `Multiplayer.dll`, an analysis of the async-time system, and ranked fix options. **Investigation only — no files were modified.**

---

## TL;DR

The Hyperspeed tier itself is **deterministic**: the multiplier is a single shared constant, the three tick-rate functions return it consistently, and speed changes still flow through Multiplayer's synced command/vote system. Because both clients run byte-identical code, the tier cannot, by itself, make one client compute a different number than the other.

So the desync is almost certainly **not a logic bug in the tier** — it's the tier **amplifying latent, timing-dependent non-determinism that already exists in the mod** and only shows up under load. In order of likelihood:

1. **Multithreaded pathfinding race** — the surgical fix for it is currently *compiled out* (`#if false`), leaving only a blanket safeguard. This is RimWorld MP's classic desync source and it scales directly with tick throughput.
2. **Floating-point / FP-rounding-mode divergence** between the two machines — more float math per second at 25× surfaces it faster.
3. **Performance-threshold effects** — at 25× a machine can fall behind faster than the server is allowed to slow down, stressing the catch-up/throttle paths where desyncs surface.

**Highest-value next step:** read the actual desync report the game saved (the `MpDesyncs` folder) — it names exactly which state diverged and roughly where. That turns the ranked hypotheses below into a definitive diagnosis. **Cheapest safe mitigation:** lower the multiplier (e.g., 18–20×, or fold Hyperspeed back to ~Ultrafast).

---

## 0. Log analysis (from the dropped Player.log — UPDATE)

The captured log is player **Whydew** (the host — runs `LocalServer`), session with `[Multiplayer] Registered Hyperspeed time-speed tier (25x)`, RimWorld 1.6.4871, mods: HugsLib, AllowTool, RimHUD, Smarter Construction, Prepatcher, Harmony, Multiplayer. Two concrete problems appear — **neither is the Hyperspeed arithmetic**:

**1. Performance collapse at 25× (confirmed).** The log has ~35 consecutive lines of:
`MpServerLog: Simulation paused because some players are too far behind: Whydew`
Whydew's own machine could not sustain 25× and kept falling past the >90-ticks-behind threshold, forcing the server to stall the timeline over and over. This is exactly cause (C) below, now confirmed from real data: the multiplier is simply too high for at least one player's hardware, producing constant stall/stutter (which by itself feels like a "desync/freeze").

**2. A per-tick exception storm in the animal idle-call sound path (new, and the prime desync suspect).** Many animals (Hare, Cat, Elephant, Dromedary…) throw every tick:
```
Exception ticking Hare39118 … System.NotImplementedException: The method or operation is not implemented.
  at Verse.Pawn_CallTracker.get_IdleCallVolumeFactor ()
  at Verse.Pawn_CallTracker.DoCall (System.Boolean forceAggressive)
  at Verse.Pawn_CallTracker.TryDoCall ()
  at Verse.Pawn_CallTracker.CallTrackerTickInterval (System.Int32 delta)
  at Verse.Pawn.TickInterval (System.Int32 delta)
    - PREFIX multiplayer: Multiplayer.Client.ThingMethodPatches:Prefix
    - FINALIZER multiplayer: Multiplayer.Client.ThingMethodPatches:Finalizer
  at Verse.Thing.DoTick () → Verse.TickList.Tick ()
```
Why this matters for desyncs:
- It is **vanilla audio code** (`IdleCallVolumeFactor` = call/sound volume) being executed **inside the deterministic tick** (`CallTrackerTickInterval` → `TryDoCall` → `DoCall`). Sound/volume depends on client-local state (camera/listener), which must never influence the simulation.
- `TryDoCall`/`DoCall` consume RNG to decide whether/what an animal "calls." The exception aborts `Pawn.TickInterval` partway through. If the throw happens on different pawns/ticks on the two machines (e.g., because each player's camera/audio context differs, or one is viewing a different map), the amount of RNG consumed diverges → **desync**. Even if deterministic, it's a serious stability bug (constant error spam, skipped animal ticks).
- It is **amplified ~25× by Hyperspeed**: `CallTrackerTickInterval` runs per game-tick, so at 25× it fires ~1.7× more than at Ultrafast and ~25× more than Normal — which is why the top tier is where it blew up.
- It is **not** produced by the tier code. `IdleCallVolumeFactor` is a stock method; a stock method throwing `NotImplementedException` points at a **mod/DLC/version interaction** (a web search found no documented occurrence, so it's specific to this setup).

**What the log does NOT show:** no `"FP round mode doesn't match"` line (so FP-mode divergence, hypothesis B, is unlikely), no pathfinding/melee stack (hypothesis A not evidenced here), and **no explicit `"Desynced after last valid tick…"` line in Whydew's log** — the formal desync message, if one fired, is most likely in the **friend's** log or the `MpDesyncs` report folder.

**Still needed to fully confirm the RNG-desync:**
- The **friend's `Player.log`** (compare: do the same animals throw at the same ticks? does their log carry the `Desynced…` line?).
- The **`MpDesyncs` folder** — located at `…\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\MpDesyncs\` (not in the Mods folder). It contains the desync report naming the exact diverging state.

**Refined conclusion:** the evidence points at (C) performance overload **plus** the idle-call exception storm as the concrete culprits, both *triggered/amplified by* 25× rather than caused by the tier's math. Lowering the multiplier addresses (C) immediately; the exception must be tracked to its mod/DLC source and/or the audio path kept out of ticking.

---

## 1. How speed actually drives ticking (the mechanism)

Understanding this is what lets us reason about determinism.

- The **server** owns a shared timeline, `gameTimer`, and advances it at **up to ~60 steps/sec** (`MultiplayerServer.Run` → `gameTimer++`, paced by `serverTimePerTick`, which is clamped to `[StandardTimePerTick, 4× StandardTimePerTick]` — i.e., it can only *slow down*, never speed up). Both clients receive the same `tickUntil = gameTimer`.
- The **client** (`TickPatch.DoTick` → `TickTickable`) runs, per Timer step, `rate` game-ticks, where `rate = ActualRateMultiplier(speed)`. It accumulates `TimeToTickThrough += 1` per Timer step and subtracts `1/rate` per game-tick.
- So **effective speed = gameTimer rate × multiplier**. Ultrafast: 60×15 = up to 900 TPS. Hyperspeed: 60×25 = up to **1500 TPS** — about 1.7× more simulation per second than Ultrafast.

The three functions you asked about all return the same constant for the tier, and are consistent with each other:

| Function | Role | Hyperspeed value |
|---|---|---|
| `TickRatePatch.Prefix` (patches vanilla `TickManager.TickRateMultiplier` getter) | Value seen by vanilla/rendering code | 25 |
| `AsyncTimeComp.TickRateMultiplier` | **Paces per-map ticking** (via `ActualRateMultiplier`→`TimePerTick`) | 25 |
| `AsyncWorldTimeComp.TickRateMultiplier` | Paces world/global ticking | 25 |

Because these are constants evaluated identically on both clients, **the number of ticks executed per Timer step is identical on both machines** (`TimeToTickThrough` is a float accumulator driven only by `+1` and `-1/25`, using the same IEEE-754 ops on both). The tier does not desync the *tick count*.

> Note: `1/25` isn't exactly representable in float — but neither is `1/15`, `1/6`, or `1/3`, and those speeds don't desync. Reciprocal inexactness is therefore not the differentiator.

**Conclusion for Part 1's core question:** the tier is deterministic. A desync requires a *non-deterministic input* to the simulation, and 25× makes those inputs bite ~1.7× harder per second than 15×. The specific inputs follow.

---

## 2. Plausible desync causes, ranked

### (A) Multithreaded pathfinding race — most likely  🔴
`Source/Client/Patches/PathFinderPatch.cs` is **entirely disabled** — the whole file is wrapped in `#if false … #endif`, so none of it compiles. Its own header comment describes precisely a thread-timing desync:

> *"PathGridDoorsBlockedJob is a cross-tick Unity Job… During MapTick(N) it runs concurrently on a worker thread and reads live `pawn.Position` while the main thread writes `pawn.Position = nextCell`… Host and client see different enemy positions depending on thread scheduling → different blocked cells → different A* path → different nextCell → WillCollideNextCell differs → one client enters the attack branch → ChooseMeleeVerb → `Rand.Chance` → desync."*

That is a textbook non-deterministic, **thread-scheduling-dependent** divergence that produces exactly the RNG-state mismatch the desync detector catches. With the surgical snapshot patch compiled out, determinism relies solely on this line in `AsyncTimeComp.Tick`:

```csharp
map.pathFinder.ForceCompleteScheduledJobs();   // "can prevent all Multithread Races"
```

which force-completes path jobs on the main thread every tick. Two problems for high speed:
- It runs **once per game-tick** → at 25× it runs ~1.7× more often than at 15×, so **any** gap in its coverage (a job type or code path it doesn't fully drain, or work scheduled after it) is exposed ~1.7× more often per second.
- It's also a **performance** cost (see Part 3): it serializes pathfinding onto the main thread exactly when you most need throughput.

This is my leading hypothesis: 25× didn't create the race, it just made an already-marginal race trip. **The desync log will confirm it** if it points at pathing/melee/`Rand.Chance` in `PatherTick`/`TryGetMeleeVerb`.

### (B) Floating-point / FP-rounding-mode divergence  🟠
The desync detector's **first** check is `if (roundMode != other.roundMode) return "FP round mode doesn't match"` (`ClientSyncOpinion.CheckForDesync`), and MP samples the CPU rounding mode at startup (`RoundMode.GetCurrentRoundMode`). RimWorld MP assumes bit-identical float math across clients; if the two machines' FPU rounding modes differ (a driver, overlay, or native plugin can change it), float results diverge. At 25× there's ~1.7× more float math per second, so a latent FP difference desyncs sooner. **If the desync message literally says "FP round mode doesn't match," this is your cause** and it's environmental, not the tier.

### (C) Performance-threshold / catch-up stress  🟠
The server's `serverTimePerTick` can only slow the timeline to **4× slower** (`MultiplayerServer.TickNet` clamp). At 25×, a machine that can't sustain ~1500 TPS falls behind faster than the server can compensate; once a player is `ExtrapolatedTicksBehind > 90` the server logs *"Simulation paused because some players are too far behind"* and stalls the timeline. The resulting stop/start and long `Simulating` catch-up bursts are where marginal races (A) and FP issues (B) most often manifest. This is an amplifier, not a root cause by itself, but 25× is much more likely to cross the threshold than 15×.

### (D) `deltaTime` / FPS-tied code leaking into ticking  🟡
`Determinism.cs` shows MP explicitly hunts down real-time/FPS-dependent code in the tick path (e.g., it replaces `Time.deltaTime` in `MapBrightnessTracker.Tick` with a constant, commenting *"tied to the FPS… not good for MP"*). Any per-tick `deltaTime`/wall-clock usage MP hasn't patched — from vanilla edge cases or another mod — is FPS-dependent, and 25× both lowers FPS and widens the frame-time gap between the two PCs, making such divergence worse.

### (E) Coarser desync *detection* at high speed (makes it worse, not causes it)  🟡
Sync opinions are finalized every **30 Timer steps** (`ConstantTicker.TickSyncCoordinator`: `TickPatch.Timer % 30 == 0`). At 25× that window spans ~750 game-ticks (vs ~450 at 15×, ~30 at Normal). RNG state is still captured per game-tick, so detection is complete — but a divergence can accumulate far more simulation before it's flagged, making the desync report less pinpointed and recovery heavier.

### What is *not* the cause
- The tier's arithmetic (constant, consistent across all three functions).
- Speed changes reaching clients out of sync (they go through the synced command/vote path like every other speed).
- Reciprocal float inexactness of `1/25` (same class as existing speeds).

---

## 3. Async-time system analysis (stability, performance, high-speed fragility)

Fragile areas and bottlenecks, most impactful first:

1. **Pathfinding determinism is a single point of failure.** The good, surgical fix (`PathFinderPatch`, snapshot pawn positions so worker threads read stable data) is `#if false`. Everything rides on `ForceCompleteScheduledJobs()`. That is both the **top desync risk** and, because it serializes multithreaded pathfinding back onto the main thread every tick, the **top high-speed performance bottleneck**. Fixing this is the highest-leverage change for both stability *and* throughput.

2. **Per-tick fixed overhead multiplies by the speed.** Each game-tick, `AsyncTimeComp.Tick` does `PreContext`/`PostContext` (faction push/pop, `Rand.PushState`/`StateCompressed`/`PopState`), a `TryAddMapRandomState` list append, and `CacheNothingHappening()` which **scans every spawned player pawn** (checking `Awake()` and `dangerWatcher.DangerRating`). At 25× all of that runs 25× per Timer step. `CacheNothingHappening` only feeds the Superfast "nothing happening" boost, yet it re-scans all pawns every single tick — a good candidate to throttle (recompute every N ticks, tick-based so it stays deterministic).

3. **Sync-opinion lists grow with tick throughput.** `commandRandomStates` / per-map `randomStates` get a `uint` appended per tick and are only drained every 30 Timer steps → at 25× they grow ~25× faster within each window, adding GC/allocation pressure precisely when frames are already tight.

4. **The multiplier and the server throttle aren't co-designed.** The server can only slow to 4×; there's no coupling between "how fast the tier asks the sim to run" and "how far behind the slowest client is." A higher tier makes the >90-ticks-behind stall path much easier to hit.

5. **Detection cadence is Timer-based, not tick-based.** Fine at normal speeds, coarse at 25× (point E above).

None of these are *bugs* introduced by the tier — they're pre-existing characteristics that the tier stresses.

---

## 4. Recommendations, ranked by risk vs. reward

### Do first (low risk, high information / high reward)
1. **Read the real desync report.** Grab the newest folder/files under `…/RimWorld/MpDesyncs` (and the `Player.log`) from *both* players from last night's session. The desync message names the diverging state ("Wrong random state on map X" / "…for the world" / "…from commands" / "FP round mode doesn't match"), and `SaveableDesyncInfo` stores stack-trace-hash divergence points. This distinguishes (A) vs (B) vs (D) definitively. **Zero code risk; do this before changing anything.** (Send me those files and I'll pinpoint it.)
2. **Lower the multiplier.** Drop `HyperspeedMultiplier` from 25 → ~18–20, or temporarily set the ceiling back to Ultrafast (15). One-line change. If the desync is amplification/performance (very likely), this alone may make it stable, and it costs nothing to try. **Best reward-to-effort ratio.**

### Moderate (medium complexity, targets the amplifiers)
3. **Auto-degrade under load (host-authoritative).** When any player's `ticksBehind`/`ExtrapolatedTicksBehind` exceeds a threshold, have the host force the speed down from Hyperspeed to Ultrafast automatically (through the existing synced time-change path). Directly defuses cause (C) and keeps everyone in lockstep. Reuses telemetry the server already tracks.
4. **Make Hyperspeed opt-in per session with a warning**, so it's only enabled when all players know their hardware can take it. Cheap, reduces accidental exposure.
5. **Reduce per-tick cost at high speed.** Throttle `CacheNothingHappening` (e.g., recompute every 15–30 ticks, tick-based) and trim other per-tick overhead. Improves high-speed FPS, which indirectly reduces desync exposure by keeping clients in sync. Must stay strictly tick-based to remain deterministic; needs testing.

### Higher effort / higher risk (biggest structural payoff)
6. **Finish and re-enable the snapshot `PathFinderPatch`** (the `#if false` one) and retire or shrink the blanket `ForceCompleteScheduledJobs`. This attacks the most probable *root* non-determinism (A) **and** restores multithreaded pathfinding for better high-speed performance. Riskiest item (transpilers over Unity Jobs; must be validated with the arbiter and desync tooling), so gate it behind the log-driven confirmation from step 1.
7. **Confirm/repair FP determinism** if step 1 shows "FP round mode doesn't match": identify what changes the rounding mode on the affected machine (GPU driver, audio/overlay, another native plugin) rather than changing mod code.
8. **(Low priority) Tie desync-detection cadence to game-ticks at high speed** for earlier, better-pinpointed detection. Protocol-touching and risky for modest benefit — only if desyncs remain elusive.

### Suggested path
Start with **#1 + #2** (diagnose from the log, and lower the multiplier as an immediate mitigation). Let the log decide whether to invest in **#6** (pathfinding) or **#7** (FP), and add **#3** (auto-degrade) as durable insurance regardless of root cause.

---

## Appendix — files reviewed
`AsyncTime/TickRatePatch.cs`, `AsyncTime/AsyncTimeComp.cs`, `AsyncTime/AsyncWorldTimeComp.cs`, `AsyncTime/TimeControlUI.cs`, `AsyncTime/MpTimeSpeed.cs`, `Patches/TickPatch.cs`, `Patches/PathFinderPatch.cs` (disabled), `Patches/Determinism.cs`, `ConstantTicker.cs`, `Desyncs/SyncCoordinator.cs`, `Desyncs/ClientSyncOpinion.cs`, `Common/MultiplayerServer.cs`, `Common/RoundMode.cs`, `Common/TimeVote.cs`. No files were modified.
