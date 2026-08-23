# Multiplayer — Async Time System Performance Review

**Mod:** `Multiplayer-continuous`
**Date:** 2026-08-22
**Scope:** Review only, no code changes. Focus on the async time subsystem (AsyncTime / AsyncWorldTime, the tick loop, time-multiplier logic, and their interaction with the normal tick/speed pipeline), with particular attention to behavior at high speed (the new 25× Hyperspeed tier).

**Files examined:** `TickPatch.cs`, `ITickable.cs`, `AsyncTimeComp.cs`, `AsyncWorldTimeComp.cs`, `ConstantTicker.cs`, `MpTimeSpeed.cs`, `AsyncTimePatches.cs`, `SetMapTime.cs`, `TickRatePatch.cs`, `StorytellerPatches.cs`, `MultiplayerAsyncQuest.cs`, `TimestampFixer.cs`, `TimeVote.cs`, `HyperspeedTextures.cs`, `TimeControlUI.cs`.

---

## 1. How the async system works (overview)

Multiplayer replaces RimWorld's normal update-driven ticking with its own deterministic, lockstep tick loop.

`TickPatch` patches `TickManager.TickManagerUpdate` and returns `false` from its Prefix, so vanilla's update never runs — Multiplayer drives everything. Each Unity frame the Prefix:

- computes how far the client is behind the server (`ticksBehind = tickUntil - Timer`) and nudges the pacing (`serverTimePerTick` ±20%) to keep a small buffer,
- sets `ticksToRun` (normally **1** in live play; many in replay/catch-up),
- periodically reports its average frame time to the server, and
- calls `RunCmds()` then `DoUpdate()`.

The unit of the shared timeline is `Timer`, which advances by exactly **1 per `DoTick`**. Game state is carried by a set of `ITickable`s exposed through `AllTickables`: one `AsyncWorldTimeComp` (the planet/world, `TickableId = -1`) plus one `AsyncTimeComp` per loaded `Map` (`TickableId = map.uniqueID`). Each tickable owns its own `mapTicks`/`worldTicks` counter, its own `randState` (deterministic RNG), and its own command queue.

The speed multiplier is applied **inside** each `Timer` step, not by running the loop more times. In `DoTick`, every eligible tickable gets `TimeToTickThrough += 1`, and `TickTickable` then runs `tickable.Tick()` repeatedly, subtracting `timePerTick = 1 / rateMultiplier` each iteration until the accumulator drops below zero. So a tickable at multiplier **M** executes ~**M** game-ticks per `Timer` step. At Normal (1×) that's 1 game-tick per step; at Ultrafast (15×) it's 15; at the new **Hyperspeed (25×)** it's 25. This is the key structural fact for everything below: **one Unity frame in live play performs one `Timer` step, but that step contains M full game-ticks of every map, run synchronously.**

The rate multiplier itself is resolved through three paths: `TickRatePatch` (patches the vanilla `TickManager.TickRateMultiplier` getter, used for draw/tween code), `AsyncTimeComp.TickRateMultiplier` (per-map pacing), and `AsyncWorldTimeComp.TickRateMultiplier` (world pacing). `ActualRateMultiplier` sits on top: in **async-time mode** each tickable uses its own rate; in **non-async (single global speed) mode** it takes the **minimum** rate across the world and every map. A large family of `SetMapTime`/`TimeSnapshot` patches temporarily swap `Find.TickManager`'s fields to a given map's values so vanilla UI, sound, and draw code reads the correct per-map time.

Determinism is preserved by pushing faction/RNG context around every `Tick()` and `ExecuteCmd()` (`PreContext`/`PostContext`), moving a number of normally frame-driven managers (regions, power, glow, storyteller, quests, ship countdown, autosave) into the deterministic tick/command path, and hashing per-map/world RNG state each tick for desync detection.

---

## 2. Performance-related gaps and weaknesses found

### 2.1 The rate multiplier is recomputed on every micro-tick, and it isn't cheap (highest impact)

`TickTickable` recomputes the pacing value **inside its inner loop**, once per game-tick:

```
while (tickable.TimeToTickThrough >= 0)
{
    float timePerTick = tickable.TimePerTick(tickable.DesiredTimeSpeed); // recomputed every iteration
    ...
}
```

`DoTick` also computes it once as a skip-guard immediately before, so there is at minimum a double-compute even at Normal speed. The problem is that within a single `Timer` step none of the inputs to this value change — commands (the only thing that can change a speed or start/stop a pausing session between ticks) execute in `RunCmds` **between** `DoTick`s, not inside `TickTickable`. So for a tickable running at M×, the value is recomputed **M times** per step for a result that is effectively constant across the step. At Hyperspeed that is 25 recomputations per map per frame where 1 would do.

The cost of each recomputation compounds the problem:

- `AsyncTimeComp.TickRateMultiplier` calls `IsAnySessionCurrentlyPausing` **twice** (map session manager + world session manager) on every call, then compares ticks and switches.
- `ActualRateMultiplier` in **non-async mode** (the mode the auto-degrade notes indicate you actually play) does not use the tickable's own rate — it iterates **every map plus the world**, calling `TickRateMultiplier` on each and taking the min. That is O(maps) session scans per call.
- `AsyncWorldTimeComp.DesiredTimeSpeed` (read every inner-loop iteration for the world tickable) is a **LINQ chain that allocates** and, for each map, calls `ActualRateMultiplier(map.DesiredTimeSpeed)` — which in non-async mode is itself O(maps).

Put together, in non-async mode the per-frame pacing overhead scales roughly as **O(maps² × M)** — quadratic in map count and linear in the speed multiplier — for a set of values that are invariant across the step. For a single home map this is small; it grows with every additional loaded map (secondary colonies, pocket maps, quest maps, temporary maps) and is multiplied by 25 at Hyperspeed versus ~1 at Normal. This is the single most consequential inefficiency in the subsystem and it is made worse precisely by the new high tier.

### 2.2 Per-tick full-map work that used to be per-frame now scales with the speed multiplier

Several things vanilla runs once per *frame* (i.e., independent of speed) are, for determinism, run once per *game-tick* here — so they now run **M times per frame**:

- **`CacheNothingHappening()`** (end of every `AsyncTimeComp.Tick`) scans `SpawnedPawnsInFaction(OfPlayer)` and checks each pawn's state, purely to decide whether Superfast should be 6× or 12×. Despite the name it is recomputed every tick (it is a per-tick field, not a cache), so at Hyperspeed it is 25 full colonist scans per map per frame feeding a slow-changing heuristic.
- **`map.postTickVisuals.ProcessPostTickVisuals()`** runs every tick, but only the final tick of the frame is ever drawn — the intermediate visual processing at high speed is wasted.
- **`UpdateManagers()`** (region clean, dirty region/room rebuild, power net update, glow grid update) runs every tick. These are largely incremental/no-op when clean, but the glow and power passes are non-trivial and now fire 25×/frame at Hyperspeed instead of once.
- **`map.pathFinder.ForceCompleteScheduledJobs()`** runs at the top of every map tick, synchronously draining the multithreaded pathfinder before ticking. At high speed this forces a worker-thread join up to 25×/frame, serializing pathfinding against the tick thread far more often than at normal speed. The in-code comment flags this as a determinism trade-off; it is a real high-speed cost.

None of these are bugs — they are the price of determinism — but the design means their frequency is tied to the speed multiplier, so the 25× tier multiplies all of them.

### 2.3 GC churn on the per-tick quest path

`MultiplayerAsyncQuest.TickQuests` does `quests.ToList()` (a fresh list allocation) on every call, and it is called from `TickMapQuests` (every map tick, when async-time and unpaused) and `TickWorldQuests`. At Hyperspeed that is a new list allocation per map per game-tick — 25×/frame per map of Gen0 garbage. On Mono this is exactly the kind of short-lived allocation the existing performance-optimization pass was trying to eliminate elsewhere; this path was not covered by that pass.

Separately, the quest cache lookups are LINQ scans over a `Dictionary<AsyncTimeComp, List<Quest>>`: `TryGetCachedQuestMap` and `TryRemoveCachedQuest` do `FirstOrDefault/SingleOrDefault(x => x.Value.Contains(quest))`, i.e. O(total cached quests) with a per-entry `List.Contains`. `TryGetCachedQuestMap` is invoked from the `SetContextForQuest` patch that wraps `Quest.TicksSinceAppeared/Accepted/Cleanup` getters, which can be read frequently. `TryGetQuestMap` additionally uses reflection (`GetField("mapParent")`) and a `List.Contains` type check per quest part, though only on the (rarer) caching path. (Minor correctness aside, not performance: the `?? false | worldQuestsCache.Remove(...)` expression in `TryRemoveCachedQuest` relies on `|` binding tighter than `??`, so the world-cache removal is only evaluated when the quest is absent from every map cache — worth a second look, but not a perf issue.)

### 2.4 The live tick loop has no time-budget guard; frame cost is quantized to M ticks

The catch-up/simulating path in `DoUpdate` bounds itself with `updateTimer.ElapsedMilliseconds < 25`, so it yields when a frame gets expensive. The **live** path has no such guard inside a `Timer` step: once a `DoTick` begins, `TickTickable` runs the full M ticks synchronously regardless of how long they take. This is inherent to lockstep determinism (you cannot advance the shared `Timer` by a fraction of a step), but the consequence is that at Hyperspeed the minimum granularity of a frame's work is **25 full map-ticks** — you cannot render a frame in the middle. Higher M therefore produces coarser, more stutter-prone frames even at the same *average* TPS, and a single heavy tick (an incident, a large caravan arrival, a GC pause) lands as a 25-tick-wide hitch.

### 2.5 Exceptions during ticking are amplified 25× at Hyperspeed

`TickTickable` wraps each `tickable.Tick()` in try/catch and logs on failure. A try/catch that never throws is nearly free, but a tick that throws **every** tick becomes catastrophic at high speed: exception construction + stack capture + `Log.Error` is expensive, and at Hyperspeed it runs 25×/frame per affected map. The auto-degrade investigation already noted an `IdleCallVolumeFactor` exception storm in the real desync log — under Hyperspeed that storm's cost (and log spam) is multiplied by the tier, and it directly feeds the "can't keep up → fall behind → desync" cycle those notes describe. The exception path is the worst-case interaction between the async loop and the new tier.

### 2.6 Redundant iterator/closure allocation on hot paths (lower impact)

`AllTickables` is a `yield` iterator allocated fresh each time it is enumerated — and it is enumerated three-plus times per `Timer` step (`RunCmds`, `DoTick`, and elsewhere). In live play that is once per frame, so it is minor (the earlier optimization pass consciously left it as-is for that reason). It becomes more significant on the **simulating/catch-up path**, where `DoUpdate` loops many times per frame and each iteration re-allocates the `RunCmds` and `DoTick` iterators. The `TipSignalCtor` patch also wraps every tooltip's text getter in a fresh closure — unrelated to speed, but a steady source of small allocations while the UI is up.

### 2.7 Speed/interaction subtleties worth being aware of

- **`ConstantTicker.Tick()` runs once per `Timer` step, not per game-tick.** So `TickShipCountdown` decrements by a fixed 1/60s per step regardless of M. At Hyperspeed the game advances 25 game-ticks per step but the ship countdown only advances once — so the launch countdown effectively runs ~25× slower in game-time at Hyperspeed. This is pre-existing vanilla-MP behavior, but the new high tier widens the discrepancy. It is a correctness/gameplay nuance rather than a performance cost, noted here because it is a direct async-vs-speed interaction.
- **`TickSyncCoordinator` (desync hashing) is gated on `Timer % 30`**, i.e. per step, so it does *not* scale with M — good. But `FinishLocalOpinion` and the fragmented sync packet it can send are the periodic spike on this path; at high speed each step covers 25× more simulation, so each opinion covers more work, which is fine but means the hash/compare work per opinion grows with speed.

---

## 3. Which of these matter most in practice

For your actual scenario (hosted, non-async single global speed, small player count, Hyperspeed 25×), ranked by expected real-world impact:

1. **§2.1 — per-micro-tick recomputation of the rate multiplier.** It is in the innermost loop, it runs 25× more often at Hyperspeed than at Normal, each call does redundant session-pause scans, and in non-async mode it scans all maps (and the world tickable's `DesiredTimeSpeed` LINQ scans all maps again, with allocation). This is the clearest case of "unnecessary work that gets worse with both speed and map count," and it is pure overhead — the inputs don't change within the step.

2. **§2.5 — exception amplification.** You already have evidence of an in-tick exception storm. At 25× it is both a large CPU sink and a direct contributor to the fall-behind/desync loop the auto-degrade feature exists to paper over. Tracing that exception to its source matters more the higher the tier goes.

3. **§2.2 — per-tick full-map work (pawn scan, pathfinder join, visuals, managers).** These are the bulk of the *actual* per-tick cost, and Hyperspeed runs them 25×/frame. The pathfinder `ForceCompleteScheduledJobs` and the every-tick colonist scan in `CacheNothingHappening` are the standouts.

4. **§2.3 — quest-tick allocations** and **§2.4 — frame quantization/stutter.** Secondary, but §2.4 explains *why* Hyperspeed can feel stuttery even when average TPS looks acceptable, and §2.3 adds steady GC pressure that shows up as periodic hitches on Mono.

The §2.6 allocations and §2.7 nuances are low priority for CPU/stutter, though §2.7's ship-countdown scaling is worth knowing about as a behavioral quirk of the tier.

---

## 4. Particularly risky / high-impact areas

- **Non-async mode's O(maps²×M) rate resolution.** The auto-degrade note already flags that global-speed auto-degrade doesn't cover per-map async speeds. The flip side is that the *non-async* path is the one with the quadratic-in-maps recomputation, and it is the path most players use. Any session with several simultaneously loaded maps (multi-colony, pocket maps, active quest maps, caravans generating temporary maps) will feel this multiplied by 25 at Hyperspeed. This is the highest-leverage area to scrutinize.

- **`pathFinder.ForceCompleteScheduledJobs()` every tick.** It is a deliberate determinism guard (the in-code comment weighs it against `PathFinderPatch`), but forcing a worker-thread join up to 25×/frame is the most likely single-line cause of high-speed thread stalls. It sits right at the boundary between "keep the async loop deterministic" and "let pathfinding run concurrently," so it is both high-impact and delicate — worth measuring before touching.

- **The Hyperspeed multiplier as a global stress multiplier.** Every determinism-driven "moved into the tick" cost in this subsystem — managers, storyteller, quests, visuals, RNG hashing, exception handling — scales linearly with M. Ultrafast (15×) already stressed these; 25× raises every one of them ~1.7×. The auto-degrade feature is the safety valve for when a machine can't sustain that, but it is treating the symptom; the per-tick costs in §2.1–§2.3 are where the ceiling actually is. Raising `HyperspeedMultiplier` further without addressing those will lower the speed the auto-degrade settles at, not raise the speed the group can actually hold.

- **Live-path lack of a per-frame time budget (§2.4).** Because a `Timer` step is all-or-nothing at M ticks, there is no graceful degradation within a frame — the client either finishes the 25-tick block in time or visibly hitches. This is fundamental to lockstep, but it is the mechanism behind Hyperspeed stutter and is worth keeping in mind when reasoning about "why does average TPS look fine but it feels choppy."

---

*Review only — no code was modified and no fixes were applied. Findings are based on static reading of the source above; confirming the §2.1 and §2.2 hot-path costs with an in-game profiler at Hyperspeed (ideally with 2–3 loaded maps) would validate the ranking before any optimization work.*
