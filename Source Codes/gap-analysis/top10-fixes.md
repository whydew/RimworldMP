# Top-10 gap-analysis fixes

**Date:** 2026-09-16
**Base:** `d23f4d4`
**Scope:** the ten items in `RimworldMP_Gap_Analysis.md` §1

**Status: build and tests only, nothing tried in the game yet.**

- **Build:** Client and Common build in Release against Krafs.Rimworld.Ref 1.6.4850. No new compiler warnings in the files I changed.
- **Tests:** all 158 Common tests pass (`dotnet test Tests/Tests.csproj`).
- **Review:** an independent review of the whole diff was done, and its findings are fixed (listed at the end).
- **Not done:** nothing has been checked in-game. See "Live checks" below.

> **Everyone must update.** The protocol changed: `MpVersion.Protocol` went from 56 to 57, the sync packet format changed, and a new server setting was added. Every player and the server need this build.

## What changed, by gap-analysis item

### 1. Hyperspeed no longer leaks into vanilla (§A1)

**Files:** `AsyncTime/MpTimeSpeed.cs`, `SetMapTime.cs` (`TimeSnapshot`), `AsyncWorldTimeComp.cs`, `TickRatePatch.cs`, `Patches/MapSetup.cs`, the debug UI, `Patches/Determinism.cs`

- **The clamp.** `(TimeSpeed)5` is never written into `TickManager.curTimeSpeed` any more. Vanilla stores `Ultrafast` there, and a flag records that it really means Hyperspeed.
- **Reading and writing the speed.**
  - `MpTimeSpeed.SetOn`, `GetFrom` and `Restore` are now the only way MP writes or reads the speed.
  - `SetOn` writes the field directly. The property setter refuses the change, and posts a message every tick, while landing confirmation is open.
- **Who reads the real speed.** `TickRatePatch`, the map-start speed, the performance panel and the debug text all read the real speed through `GetFrom`.
- **Idle calls are back.**
  - `DeterministicAnimalIdleCalls` is removed, so animal idle calls work again at every speed.
  - Their random-number use comes only from the call-interval roll, which is the same on every client.
  - The sound itself already runs with its own separate random state (`SubSoundDef.TryPlay`).
  - A small guard on `IdleCallVolumeFactor` stays as a safety net, in case another mod leaves an out-of-range speed behind.
- **Side benefit.** Sounds whose `gameSpeedRange` stops at 4 now play at Hyperspeed too.

### 2. Leftover diagnostics are off by default (§C1)

**Files:** `Patches/SelfTendDesyncDiagnostic.cs`, `Patches/MapLoadDiagnostic.cs`, new `Util/MpDiagLog.cs`, `Settings/MpSettings*.cs`, `OnMainThread.cs`

- **How to turn them on.** Both diagnostics now depend on a new setting, **"Diagnostic logging"** (MP settings, dev mode only). It is off by default.
- **Where the output goes.**
  - Lines are written to `MpLogs/MpDiagnostics.log`; the previous run is kept as `-prev`.
  - The file is capped at 64 MB and uses no stack traces.
  - The lines don't count toward RimWorld's 10,000-message log limit, so desync logs are no longer silenced.

### 3. Tick-list removal matches registration (§A2)

**File:** `AsyncTime/AsyncTimePatches.cs`

- Registering and removing now share one helper, `TickListAdd.ListFor`, which mirrors vanilla `TickListFor`.
- Destroyed frames and corpses no longer stay in the tick list, and held corpses are no longer ticked twice.

### 4. Gravship takeoff/landing end is synced (§A3)

**Files:** `Patches/GravshipTravelSessionPatches.cs` (`GravshipCutsceneSync`), `Patches/TickPatch.cs`, `AsyncTime/AsyncWorldTimeComp.cs`, `Common/FreezeManager.cs`

- **Waiting for each client.**
  - While a cutscene runs, a client runs no commands and no ticks until its own cutscene reaches the end.
  - This wait starts immediately after the command that started the flight, on every client.
- **Finishing the flight.**
  - A client that reaches the end sends `SyncCutsceneEnded(mapId, flightStartStep)` instead of finishing the flight itself.
  - The first of these commands to arrive finishes the flight on every client: `TakeoffEnded`/`LandingEnded` runs under the faction that started the flight. That client then unfreezes.
  - Duplicate or stale end commands are ignored.
- **Join points.** Join points requested during a flight wait until it ends, the same way on every client, because the cutscene state isn't saved.
- **Safety valves.**
  - If a client's cutscene doesn't finish within 90 s, it stops waiting and logs an error. That case can still desync.
  - The server's "others are still frozen" wait went from 10 s to 60 s.
- **Known limits.**
  - The arbiter can't do the GPU capture, so it waits the full 90 s on every flight.
  - Vanilla's long events inside the cutscene (removing and placing the ship) still use the global random state. This is the existing upstream TODO #638.

### 5. Stat cache split instead of disabled (§C2)

**Files:** `Patches/Determinism.cs` (`StatWorkerGetValuePatch`), new `Patches/SimulationCaches.cs`, `MultiplayerGame.cs`, `AsyncWorldTimeComp.cs`

- **Which cache is used.**
  - **Simulation** (ticks and commands): vanilla's `temporaryStatCache`.
  - **Interface:** a separate cache for each `StatWorker`.
  - **Anything else** (loading, reloading): no cache.
- **Stale entries.** The freshness check uses `Math.Abs`, so an entry stamped on a clock that is further ahead counts as stale.
- **Clearing.** The caches are cleared when a game is created or loaded, and at every `CreateJoinPoint`.
- **Standalone servers.** They send joiners snapshots taken outside join points, so simulation reads are not cached there. That keeps the old P1a behaviour for standalone only.
- **Check on load.** If the transpiler doesn't match, an error is logged at load.

### 6. Desync traces: rolling hash per step, and a Hyperspeed toggle (§C3)

**Files:** `Desyncs/ClientSyncOpinion.cs`, `SyncCoordinator.cs`, `SaveableDesyncInfo.cs`, `UserReadableDesyncInfo.cs`, `DeferredStackTracing.cs`, `Common/Networking/Packet/SyncInfoPacket.cs`, `Common/ServerSettings.cs`, the host UI

- **What is sent now.**
  - Each opinion sends one `(trace count, rolling hash)` pair per traced timer step, at most 30 pairs.
  - It used to send one int per traced random-number call.
- **What stays local.** The per-trace list stays on each client.
- **After a desync.**
  - `diffAt` points to the first trace of the first step that differs.
  - The trace report includes that whole step. It is capped at 4000 traces.
- **New host setting: "Desync traces → At Hyperspeed".**
  - Off by default.
  - While it's off, nothing is traced while the ticking map (or the world) runs at Hyperspeed.
  - The decision uses the synced speed, so every client makes the same choice.

### 7. Variable tick rate on viewed maps is 3 instead of 1 (§C4)

**Files:** `Patches/VTRSyncPatch.cs` (`VTRSync.ViewedMapVtr`), `AsyncTime/AsyncTimeComp.cs`

- Maps nobody is viewing still use 15.
- Projectiles on viewed maps stay at 1, so their flight doesn't look choppy.
- This changes simulation results, so everyone needs the same build.

### 8. Pathfinding runs alongside the tick again (§C5)

**Files:** new `Patches/ConcurrentPathfinding.cs`, `AsyncTime/AsyncTimeComp.cs`, `Comp/Game/MultiplayerGameComp.cs`, `Debug/DebugActions.cs`

- **What changed.**
  - The doors/pawns-blocked job (managed code that reads live pawn positions) now runs on the main thread at schedule time.
  - The avoid grid, lord walk grid and request-customizer grid are copied for grid jobs. The copies are freed once the jobs complete.
  - Jobs are completed before a faction's cost grid is freed.
- **Result.** The forced completion right after `MapPreTick` is skipped, and the pathfinding workers run during the tick as in vanilla.
- **Kill switch.**
  - `MultiplayerGameComp.concurrentPathfinding` is saved with the game and defaults to on.
  - The synced dev action **Multiplayer → "Toggle concurrent pathfinding"** switches it for all players at once.

### 9. Ideology Archonexus "new colony" flow is synced (§B1)

**File:** new `Persistent/ArchonexusNewColony.cs`, plus English strings

- **Multifaction games.** The flow isn't supported. The quest step is cancelled with a message.
- **Otherwise, step by step:**
  1. A saved world session pauses the game. Any player can open the choice from the colonist-bar menu.
  2. The first confirmed choice is synced: things, stack counts, hit-point reset and the slave rule, plus removal of the escape ship.
  3. The player who confirmed sees the cinematic and picks the tile.
  4. The tile is synced, and `MoveColonyAndReset` runs inside that command on every client.
- **Cancelling.** A cancel anywhere cancels the flow for everyone.
- **Not supported.** The optional ideoligion reconfiguration (`Dialog_ConfigureIdeo`) is skipped in MP.
- **Not changed.** `ArchonexusCountdown` still counts down in real time; the game ends there.

### 10. Frame-keyed quest cache (§A4)

**Files:** `Patches/SimulationCaches.cs`, `AsyncTimeComp.PreContext`, `AsyncWorldTimeComp.PreContext`

- `PlayerItemAccessibilityUtility`'s cache is invalidated at the start of every simulation event.

## Findings from the review that were fixed

| Finding | Fix |
|---|---|
| The gravship wait could start at different points on different clients | Checked after every command |
| A stale or duplicate "cutscene ended" command could end a later flight | Flight id added |
| A join point could land mid-flight | Deferred until the flight ends |
| Standalone snapshots could leave the stat cache out of step between peers | No simulation stat cache on standalone |
| The Archonexus notice was hidden by the "not for me" message filter | Shown outside the command |
| An Archonexus session that couldn't be created left the quest stuck | Quest step cancelled instead |
| `SetOn` could be refused by the property setter | Writes the field directly |
| A faction's cost grid could be freed while a job still read it | Jobs completed first |
| Trace speed check was slow | Uses a cached world speed |

## Live checks (not yet run — use GABS, and ask first because it takes over the screen)

1. **§A1:**
   - Host at Hyperspeed with animals on the map.
   - `Player.log` should show no `IdleCallVolumeFactor`, and animal calls should be audible.
   - The speed buttons should still show Hyperspeed.
2. **§A2:** build 50 walls, then compare `tickListNormal` counts. Drop a corpse twice; it should tick once per tick.
3. **§A3:**
   - Two instances: host with gravship cutscenes off, client with them on.
   - Launch and land. There should be no desync, and both should end with the same map count.
   - Repeat with a player joining mid-flight: the join point should wait until the flight ends.
4. **§C2, §C3, §C4:**
   - Compare TPS at 25× before and after.
   - Force a desync (dev action). The report should contain the whole differing step.
5. **§C5:** run a large raid for a few in-game hours with concurrent pathfinding on. Then toggle it off and back on; there should be no desync.
6. **§B1:** dev-complete the third Archonexus core. Check each path: choose → pick tile → new colony on both clients. Also check cancel, and a join point during the choice.

---

# Follow-up: C7 and C8 (2026-09-17)

**Status: build and tests only, nothing tried in the game yet.**

- **Build:** builds in Release.
- **Tests:** all 158 Common tests pass.
- **Review:** an independent review was done, and its two findings are fixed (listed at the end).

> **Everyone must update.** `MpVersion.Protocol` went from 57 to 58, because the Superfast change below changes the simulation. Every player and the server need this build.

## C7: one tick-context patch

**Files:** `MultiplayerStatic.cs`, `Patches/ThingMethodPatches.cs`

- **What changed.**
  - A single prefix/finalizer on `Thing.DoTick` now sets the thing and faction context for the whole tick, including base calls.
  - This replaces the patches on every vanilla `Tick`, `TickRare`, `TickLong` and `TickInterval` override.
- **Why that's enough.** In 1.6 every vanilla thing tick goes through `DoTick`: tick lists, held things via `ThingOwner`, world pawns, trade ships, transporters and site parts.
- **Mod-added thing types.**
  - They keep per-override patches, in case a mod calls those methods directly.
  - Those patches skip the push when `DoTick` already set the context for the same thing.
- **Unchanged.** `SpawnSetup`, `TakeDamage` and `Kill` are still patched on every type.
- **Known minor difference.** `Corpse.TickRareInt` calls `InnerPawn.TickRare()` directly, so that code now runs under the corpse's context. It is still the same on every client and doesn't depend on faction.

## C8a: tick-loop overhead

**Files:** `Patches/TickPatch.cs`, `Util/Extensions.cs`, `AsyncTime/AsyncWorldTimeComp.cs`, `AsyncTime/AsyncTimeComp.cs`, `AsyncTime/MultiplayerAsyncQuest.cs`, `ConstantTicker.cs`

- **Tick loop.** `RunCmds`/`DoTick` no longer allocate the `AllTickables` iterator. The order stays world first, then maps from last to first, and `Find.Maps` is still read after the world runs.
- **Rate lookup.**
  - `TimePerTick` works out the rate once instead of twice.
  - The non-async rate, `MpComp()`, the world's `DesiredTimeSpeed` and the autosave check are plain loops now (no LINQ, no closures).
- **Quests.**
  - `TickQuests` reuses its snapshot buffers.
  - The quest-cache lookups are loops, with the same results as before.
- **"Nothing happening" check.**
  - The colonist scan now runs only if the world or a current map is at Superfast, the only speed that reads it. Otherwise the value is false.
  - Right after a switch to Superfast, the first step runs at 6× instead of possibly 12×. This is the simulation change behind the protocol bump.
- **Deliberately not cached per step.** The tick rate is still worked out for every game tick, because it can change within a timer step:
  - a pausing session can start (growth-moment letters pause on a tick deadline);
  - forced normal speed can kick in during a raid;
  - the "nothing happening" state is updated after every tick.

## C8b: per-frame pawn visuals

**Files:** new `AsyncTime/PostTickVisualsPerFrame.cs`, `AsyncTime/AsyncTimeComp.cs`

- **Still every tick, for every spawned pawn, as before:**
  - rotation;
  - jitter and lean, which are part of `DrawPos`, and verbs read `DrawPos` for projectile origin and facing;
  - the renderer step (animation reset, downed wiggler).
- **Now once per frame** in `Map.MapUpdate`, for pawns in view, with the ticks accumulated since the last frame (capped at 250), like vanilla:
  - footprints, breath puffs and water ripples;
  - these only spawn flecks, which already run with their own random state;
  - they are also the costly part: drawn position, terrain and snow lookups, and ambient temperature.
- **Glow, power, region and room updates stay per tick** (`UpdateManagers`). The simulation reads those grids, and each update does nothing unless something changed.
- **Side effect.** Mods that patch `Pawn.ProcessPostTickVisuals` no longer run in multiplayer.

## Findings from the review that were fixed

| Finding | Fix |
|---|---|
| Jitter and lean moved to per-frame and camera-dependent, which could desync projectile origins | Kept per tick |
| The Superfast check scanned `game.asyncTimeComps`, which keeps comps of removed maps on peers that haven't reloaded | Scans `Find.Maps` instead |

## Live checks

- **Speed:** compare ticks per second at 25× against the top-10 build.
- **Visuals:** footprints, breath and ripples should still appear on screen. Leaning shooters should still work.
- **Mods:** load with a mod that adds ticking buildings and check for errors in `Player.log`.
- **Desync test:** run two players for an hour of play at mixed speeds, including Superfast.
