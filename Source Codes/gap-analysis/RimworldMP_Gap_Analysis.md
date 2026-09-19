# RimworldMP fork vs. RimWorld 1.6: gap analysis (performance, correctness, features)

**Date:** 2026-09-16
**Subject:** `whydew/RimworldMP` at `d23f4d4` (the local `main` HEAD matches it), compared with the `RimWorldDecompiled-master` folder
**Type:** Static analysis only. I changed no code and ran nothing in-game.

---

## 0. Scope, baselines and caveats

| Item | Value |
|---|---|
| Fork base | Upstream `rwmt/Multiplayer` **0.11.5** (`4a3be27`, 2026-04-29) plus about 1,900 changed lines (hyperspeed tier, auto-degrade, map-load/self-tend fixes, standalone streaming, diagnostics) |
| Game code compared against | `RimWorldDecompiled-master` = **1.6.4633** (`AssemblyVersion 1.6.9438.38202`, built 2025-11-03). It is byte-identical to the public Chillu1 decompile. |
| Game version the fork compiles against | `Krafs.Rimworld.Ref` **1.6.4850**. That is newer than the decompile. |
| Size of the version gap | Of **1,144** `nameof(Type.Member)` references in MP's code, only **1** is missing from the decompile: `GenConstruct.CanPlaceBlueprintAt_NewTemp`, which `Factions/Blueprints.cs` patches. Method *names* are nearly identical between the two builds, but method *bodies* may have changed. Anything below that depends on a vanilla body is marked as needing a check against the live game. |
| Harmony | The workshop Harmony mod is **2.4.2**, the same version as `Lib.Harmony 2.4.2` in the fork's build, so they match. |

**Method.** I read the core subsystems by hand: async time, tick loop, determinism patches, sync registrations, sessions, save/reload, and the server loop. I also wrote a few scanners (Python, kept in `gap-scan-tools/`):

1. **Lambda-ordinal audit.** MP hooks button and menu actions by their position inside the vanilla method (the "lambda ordinal", e.g. "the 3rd lambda in `GetGizmos`"). The scanner models how the C# compiler numbers those lambdas, then checks each of MP's **314** registrations against the vanilla body at that position.
   - The model is checked against 73 registrations whose correct target I confirmed by reading the vanilla code; it gets all 73 right.
   - It needs two compiler details to get there: scope-by-scope numbering, and treating object initializers as scopes.
   - Those two details are what make `CompPilotConsole` #4/#5 and `CompPlantable` #0/#3 resolve correctly.
2. **Unsynced-action scan.** Checks every gizmo, float-menu, `ProcessInput`, `DoWindowContents` and `FillTab` lambda (987 in total) for game-state changes that don't go through a synced method. About 120 candidates came out; I sorted through each by hand.
3. **Coverage scans.**
   - Every vanilla subclass of `Window`, `ITab`, `PawnColumnWorker`, `Designator`, `Gizmo` and `FloatMenuOptionProvider` (all 53), checked against the MP source.
   - Every frame-count or real-time dependency in simulation code.
   - Every `Find.CurrentMap` read on a tick path.
   - Every call MP makes to vanilla members by name (the version-gap check above).

**Priority key:** 🔴 high (desync, gameplay-breaking or a large performance cost) · 🟠 medium · 🟡 low/info. **Confidence:** ✅ confirmed in code · ⚠️ needs a live check.

---

## 1. Top 10 (fix these first)

| # | Pri | Finding | Kind | Effort |
|---|---|---|---|---|
| 1 | 🔴 | The `IdleCallVolumeFactor` exception flood is caused by the fork's own `(TimeSpeed)5`, not by Prepatcher (§A1) | Correctness | S |
| 2 | 🔴 | The temporary self-tend diagnostic is on by default and floods `Log.Message` with stack traces. After 10k messages RimWorld turns off Unity logging (§C1). | Perf + debugging | XS |
| 3 | 🔴 | `TickListRemove` doesn't match `TickListAdd` for `IThingHolder` things. Corpses and minified things get ticked twice, and destroyed frames and corpses pile up in the tick list (§A2). | Correctness + perf | XS |
| 4 | 🔴 | Gravship takeoff/landing finishes on a per-frame timer. The freeze lifts **10 s after the host** is done, even if clients are still in the cutscene (§A3). | Desync (Odyssey) | M |
| 5 | 🔴 | The P1a fix switches off vanilla's temporary stat cache for **all** simulation reads (§C2) | Perf | M |
| 6 | 🔴 | Desync tracing is on by default: a native stack walk on every simulation `Rand` call, plus every trace hash sent every 30 steps (§C3) | Perf + bandwidth | M |
| 7 | 🔴 | 1.6's variable tick rate is effectively off on any map a player is looking at: every Thing runs `TickInterval` every tick (§C4) | Perf | M |
| 8 | 🟠 | The pathfinder is force-completed right after it is scheduled, every map tick. This throws away 1.6's concurrent pathing (§C5). | Perf | L |
| 9 | 🔴 | The Ideology Archonexus "new colony" flow has no multiplayer handling at all (§B1) | Feature/desync | L |
| 10 | 🟠 | Quest generation uses a cache keyed on the frame counter (`PlayerItemAccessibilityUtility`), which breaks under catch-up (§A4) | Desync | S |

---

## A. Correctness and determinism gaps

### A1. 🔴✅ The "unexplained" `IdleCallVolumeFactor` exception flood comes from the Hyperspeed tier

- **Vanilla:** `Verse/Pawn_CallTracker.cs:40-48`
  ```csharp
  private float IdleCallVolumeFactor => Find.TickManager.CurTimeSpeed switch {
      TimeSpeed.Paused => 1f, TimeSpeed.Normal => 1f, TimeSpeed.Fast => 1f,
      TimeSpeed.Superfast => 0.25f, TimeSpeed.Ultrafast => 0.25f,
      _ => throw new NotImplementedException(), };
  ```
- **The fork's side:**
  - Hyperspeed is `(TimeSpeed)5` (`AsyncTime/MpTimeSpeed.cs:18`).
  - That value is written into the vanilla `TickManager` whenever a map or the world ticks:
    - `SetMapTime.cs:240`: `tickManager.CurTimeSpeed = mapComp.DesiredTimeSpeed`
    - `AsyncWorldTimeComp.cs:147`
  - So at Hyperspeed, every animal idle call hits `_ => throw`.
- **Why it matters.**
  - The throw aborts `Pawn.TickInterval` right after `caller` (Pawn.cs order). The pawn then skips `skills`, `drafter`, `relations`, `psychicEntropy`, `guest` (recruitment), `ideo`, `genes`, `royalty` and everything after them.
  - Because the throw also skips `ResetTicksToNextCall`, the same pawn throws again on every interval.
  - Earlier project write-ups blamed Prepatcher or a DLC (`current-desync-analysis-post-rimhud.md`, `desync-selftend-log-analysis.md`). The "KILL ALLL save only" pattern fits a save that was being played at Hyperspeed.
- **What the fork does today.** `Determinism.cs:795` `DeterministicAnimalIdleCalls` skips `CallTrackerTickInterval` in MP **at every speed**. That stops the throw, but idle vocalizations are now gone for everyone, including at 1×.
- **Other leaks of value 5:**
  - `SubSoundDef.cs:155`: `gameSpeedRange.Includes((int)CurTimeSpeed)` (sounds whose range tops out at 4 go quiet).
  - Any **mod** that switches on or indexes by `CurTimeSpeed`.
- **Fix:**
  - Never write `(TimeSpeed)5` into `TickManager.curTimeSpeed`. Clamp it to `Ultrafast` in `TimeSnapshot.GetAndSetFromMap` and in `AsyncWorldTimeComp.PreContext`.
  - Have `TickRatePatch` read Hyperspeed from MP's own state, i.e. `DesiredTimeSpeed` on the current tickable.
  - Then remove `DeterministicAnimalIdleCalls`, or limit it to a null-guard.

### A2. 🔴✅ The tick-list remove path doesn't mirror the add path for `IThingHolder`

- **Where:** `AsyncTime/AsyncTimePatches.cs:86` vs `:107`.
- **What vanilla does.** `TickManager.TickListFor` sends **every `IThingHolder`** to `tickListNormal`, for both registering and deregistering.
- **What MP does.**
  - `TickListAdd` matches vanilla: `t is IThingHolder || Normal` goes to Normal.
  - `TickListRemove` only removes from Normal `if (tickerType == Normal)`.
- **What is affected.** Holders whose def isn't `Normal`:
  - **Every corpse.** `ThingDefGenerator_Corpses.cs:127` sets `TickerType.Rare`.
  - **Every construction frame.** Generated with the default `Never`.
  - Graves, sarcophagi and other `Building_Casket`s, minified things, bookcases, etc., depending on their def.
- **Effects:**
  1. **Destroyed frames and corpses are never removed.** They stay in each map's `tickListNormal` until the next save+reload. `TickList.Tick` checks `thing.Destroyed` for every one of them on every tick (×25 at Hyperspeed). In a build-heavy session that is thousands of dead entries, and the memory they hold isn't freed.
  2. **Double ticking.**
     - A corpse that is picked up, buried, or put in a grave or sarcophagus stays in the list *and* gets ticked by its holder (1.6 `Thing.DoTick` ticks held contents). So `TickRare` runs twice every 250 ticks: rot, `Hediff_DeathRefusal`, flesh-beast dessication.
     - If it is dropped again it is registered a **second** time. It then `DoTick`s twice per tick, and its inner pawn is ticked twice too. This affects Anomaly timers such as shambler rise and death refusal.
     - An uninstalled grave keeps ticking while it sits inside its `MinifiedThing`.
  3. This is not a desync, because every client reloads at each join point (`CreateJoinPointAndSendIfHost` reloads on all peers). It is still a behaviour difference from vanilla that grows over time and resets only at autosave or join.
- **Fix:** use the same condition in both paths (`if (t is IThingHolder || tickerType == TickerType.Normal) comp.tickListNormal.DeregisterThing(t)`). This is also an upstream bug, so it's worth sending as a PR to `rwmt/Multiplayer`.

### A3. 🔴✅/⚠️ Gravship takeoff/landing finishes outside the synced tick

- **Where:** `Verse/WorldComponent_GravshipController.cs:372-409` (vanilla) and `Common/FreezeManager.cs:28,40` (MP).
- **Vanilla flow.** `WorldComponentUpdate` runs every frame. It:
  - counts `timeLeft -= Time.deltaTime` over a 10 s cutscene,
  - skips the countdown entirely when the local setting `Prefs.GravshipCutscenes` is off,
  - waits for `gravshipCapturer.IsCaptureComplete` (a GPU capture),
  - then calls `TakeoffEnded()` or `LandingEnded()`. These make big state changes: `AbandonMap`, `TravelTo`, landing outcome rolls, `Scenario.PostGravshipLanded`.
- **What MP does.**
  - It freezes the timeline when takeoff or landing starts (`GravshipTravelSessionPatches.cs:176`).
  - It unfreezes in the `TakeoffEnded`/`LandingEnded` prefixes.
  - The server unfreezes **10 seconds after the host unfreezes, even if other players are still frozen**.
- **The race.**
  - A host with cutscenes **off**, or with a faster GPU capture, finishes almost at once.
  - A client with cutscenes on needs ≥10 s plus capture time.
  - When the freeze lifts first, the client applies `TakeoffEnded`/`LandingEnded` *after* ticks have resumed, at a different tick than the host.
- **Randomness:**
  - `LandingEnded` gets MP's `Rand.PushState(map.randState)` wrapper (good). The map's `randState` isn't advanced, which is fine as long as every peer does the same.
  - `TakeoffEnded` gets **no** randomness isolation. MP's own `TODO (#638)` is next to this code.
- **Fix:**
  - Make the end of the cutscene a synced event: the host sends a "cutscene finished" command, and each client plays its cutscene but applies the state change only when that command runs.
  - Or force every peer to skip the cutscene (patch `Prefs.GravshipCutscenes` to false in MP).
  - And don't release the freeze until every frozen player has reported done, or at least raise the 10 s cap well above the cutscene length.

### A4. 🟠✅ Frame-keyed cache used during quest generation

- **Where:** `RimWorld/PlayerItemAccessibilityUtility.cs:67,225`. The cache is valid while `RealTime.frameCount` is unchanged.
- **Who uses it in simulation code:** `QuestNode_Root_Beggars`, `QuestNode_TradeRequest_GetRequestedThing`, `ThingSetMaker_Techprints`, `ThingSetMaker_RandomOption` (reward generation) and `FactionDialogMaker`.
- **Why it can desync.**
  - In normal play every peer runs one timer step per frame, so cache hits line up across peers.
  - During **catch-up** (join, reconnect, `Simulating`), `DoUpdate` runs many steps in one frame. The joiner then reuses a stale cache across ticks where the host rebuilt it, and generates different quest contents.
- **Precedent.** This is the same class of bug MP already fixes for `Pawn_JobTracker.StartJob` (`Patches.cs` `JobTrackerStartFixFrames` swaps `frameCount` for `eventCount`).
- **Fix:** apply the same transpiler to `CacheAccessibleThings`, or clear the cache at the start of every tick.

### A5. 🟠✅ Transport ships tick on the world clock (async time)

- **Where:** `TickManager.DoSingleTick` → `Find.TransportShipManager.ShipObjectsTick()` (`TickManager.cs:492`). MP runs this only from the **world** tickable.
- **What goes wrong:**
  - `ShipJob_WaitTime.cs:17` compares world `TicksGame` against a `startTick` that was recorded while a map was ticking.
  - Shuttles on a **paused** map keep running their jobs (waiting, leaving) at world speed.
  - Everything happens under the world's randomness and faction context, with `Find.CurrentMap` set to whatever the player is viewing.
- **Fix:** move the ship ticks for each ship into its map's `AsyncTimeComp.Tick`, with the map's time snapshot.

### A6. 🟠✅ Odyssey's default scenario (`ScenPart_PursuingMechanoids`) under async time

- **Where:** `ScenPart_PursuingMechanoids.cs:147-194`.
- **What goes wrong:**
  - The timers are set with whatever `TicksGame` is active at the time (map ticks inside a map command or map generation).
  - They are checked from the **world** tick with **exact equality** (`TicksGame == TimerIntervalTick(...)`) at 2,500-tick boundaries.
  - With async time on, map ticks and world ticks drift apart. The warning letter and the mech raids can then fire at the wrong time or never.
- **Fix:** tick this scenario part per map (with `SetMapTime`), or change the comparison to "passed since last check".

### A7. 🟡✅ World-first, batched tick order

- MP runs the world M times, then each map M times, inside one timer step (`TickPatch.DoTick`). Vanilla interleaves the world and the maps on every tick.
- At 25× that is up to a 24-tick offset inside a step between world and map clocks.
- It is deterministic, but it widens A5 and A6 and any world↔map timestamp comparison (e.g., `lastLaunchTick`, caravan arrivals).

### A8. 🟡✅ Brittle compiler-generated names

- `Patches/Plans.cs:16` hard-codes `"Verse.Plan+<>c__DisplayClass45_0"` / `"<GetGizmos>b__1"`. The `45` shifts whenever Ludeon adds a method above it. The patch would then quietly not apply, and plan colour changes would stop being synced.
- `SyncActions.cs:24` hard-codes `"<>c__DisplayClass0_1\`1"`.
- **Fix:** use `MpMethodUtil.GetLambda(typeof(Plan), nameof(Plan.GetGizmos), lambdaOrdinal: 1)`. It already handles display classes.

### A9. 🟡✅ Robustness nits

- `SyncFieldUtil.FieldWatchPostfix`: an exception inside a watched method leaves stack markers behind. The code has its own `todo`.
- `Patch_CompCauseGameCondition_GetConditionInstance` (`SetMapTime.cs`) runs a LINQ search with a closure on a per-tick path.

---

## B. Functional gaps (features missing or unsynced in MP)

### B1. 🔴✅ Ideology Archonexus "found a new colony" flow

- **Where:** `QuestPart_NewColony` → `Dialog_ChooseThingsForNewColony` → `Screen_ArchonexusSettlementCinematics` → `MoveColonyUtility.PickNewColonyTile` → (non-classic mode) `Dialog_ConfigureIdeo` → `MoveColonyUtility.MoveColonyAndReset`.
- **What goes wrong:**
  - The chain starts from a quest signal, so the dialog opens on **every** peer.
  - Each peer then picks its own things and tile, and a huge state reset runs locally inside UI callbacks.
  - MP has no handling for any of these types (none appear anywhere in the source).
- **Also:** `ArchonexusCountdown.ArchonexusCountdownUpdate` still counts down with `Time.deltaTime`. MP moved `ShipCountdown` into `ConstantTicker` but not this one. This matters less, since the game ends.
- **Fix:** add a persistent session in the same style as the existing Caravan-forming and Map-portal sessions: synced item and tile selection, then a synced `MoveColonyAndReset`.

### B2. 🟠✅ Bill style selection (Ideology classic mode)

- **Where:** `Dialog_BillConfig.cs:353-392`.
- **What goes wrong:** the FloatMenu delegates write `bill.style`, `bill.globalStyle` and `bill.graphicIndexOverride` directly. MP watches many bill fields (`SyncFields.cs:148-176`) but not these, and puts no watcher around the menu options. The choice stays local, and players get different product styles, which affects style-dominance mood.
- **Fix:** add `Sync.Fields(typeof(Bill_Production), style, globalStyle, graphicIndexOverride)` and wrap the options with `WatchMenuOptions`.

### B3. 🟠✅ `Designator_Build` serializer is missing 1.6 fields

- **Where:** `SyncDictRimWorld.cs:650`.
- **What goes wrong:** the serializer writes `PlacingDef`, `placingRot`, `stuffDef` and `sourcePrecept`, but not `styleDef`, `styleOverridden` or `glowerColorOverride`. "Copy" or "Build similar" of a **coloured light** (`BuildCopyCommandUtility.cs:40`) or of a classic-mode style override therefore loses its colour or style for everyone.
- **Fix:** serialize those three fields.

### B4. 🟠✅ Multifaction: managers that aren't swapped per faction

- **What is swapped.** `FactionMapData` (`Comp/Map/FactionMapData.cs`) swaps designations, areas, zones, plans, haul destinations, `listerHaulables`, `resourceCounter`, filth and mergeables.
- **What is still shared:**
  - `autoSlaughterManager` (auto-slaughter settings)
  - `animalPenManager`
  - `storageGroups` (linked storage)
  - `wealthWatcher` and `storyState` (raid points use combined map wealth)
  - world-level singletons such as `Find.Anomaly`
- **Effect:** these will clash or leak between player factions that share a map.

### B5. 🟡✅ Vanilla features MP turns off on purpose

- `GameEnder.CheckOrUpdateGameOver`: no game-over screen and no "new wanderers" (`Dialog_ChooseNewWanderers` can't be reached).
- Naming your faction or settlement (`CanNameAnythingNow`).
- The first-time gravship naming popup (`Odyssey.cs:40`). Manual rename is synced through `IRenameable`.
- Vanilla auto-pause and the Autosaver.

These are deliberate, but players should know about them.

### B6. 🟡✅ Local-only cosmetic state

These never reach other players' simulation, but saves only keep the host's value:

- Area colour picker (`Dialog_AllowedAreaColorPicker.SaveColor` → `Area_Allowed.SetColor`)
- Hide zone / hide plan (`Command_Hide`)
- Substructure overlay toggle (`CompSubstructureFootprint.displaySubstructureOverlay`, which is saved)
- `ColorClipboard`

### B7. 🟡✅ World-only speed boost is missing

- Vanilla returns **18×** (Superfast) and **150×** (Ultrafast) when `Find.Maps.Count == 0` (`TickManager.cs:95-107`), e.g., when everyone is travelling by caravan or gravship.
- `AsyncWorldTimeComp.TickRateMultiplier` is fixed at 6× / 15×.

### B8. 🟡✅ Missing `SetDebugOnly`

- `SyncMethods.cs:336` registers `CompEggLayer` "DEV: LayEgg" without `.SetDebugOnly()`. A player with dev mode on can use it even if the host doesn't allow debug.

### B9. 🟡 Standalone + multifaction + async: world map opens trigger reloads

- `VTRSyncPatch.cs:149` requests a join point every time a player opens the world map. Each join point is a full save+reload for **every** peer, limited only by a 30-tick cooldown (`WorldData.cs:43`).
- This doesn't affect hosted play.

---

## C. Performance gaps

### C1. 🔴✅ Leftover diagnostics are on by default

- **Where:** `Patches/SelfTendDesyncDiagnostic.cs:30` (`Enabled = true`) and `Patches/MapLoadDiagnostic.cs:22`.
- **Cost:**
  - The self-tend patches call `Log.Message($"...")` **on every** `ShouldBeTendedNowByPlayer`, `WorkGiver_Tend.JobOnThing` and medicine `TryStartCarry` for player humanlikes during simulation. Every doctor's work scan evaluates each patient, so that is many calls per second, ×25 at Hyperspeed.
  - Each `Log.Message` captures `StackTraceUtility.ExtractStackTrace()` (expensive) and builds strings.
- **Worse:** RimWorld stops logging after **10,000 messages** and sets `Debug.unityLogger.logEnabled = false` (`Verse/Log.cs:40`). Any **later desync or error output is lost**, which undermines the desync investigation these files were added for.
- **Fix:** default both to `false`, gate them behind an MP setting, and write to a separate file without stack traces.

### C2. 🔴✅ Stat cache disabled for all simulation reads (the P1a fix)

- **Where:** `Determinism.cs:521`. `HasValueInCache` returns `false` whenever `!Multiplayer.InInterface`.
- **What that costs.** Vanilla's `temporaryStatCache` backs very frequent reads, which now all recompute in full:
  - `Thing.MaxHitPoints` (10 t)
  - `Need_Food` MaxNutrition (15 t)
  - `MentalBreaker` thresholds (5 t)
  - `GenTemperature` comfort range (1 t)
  - `ThoughtWorker_Hot`/`Cold` (10 t)
  - `Pawn` vacuum checks (60 t)
  - `MassUtility` (1 t)
  - `CompEntityHolder` (15 t)
- **The underlying bug is real.** Upstream's version lets a UI write replace a simulation entry, so peers can take different cache-hit paths.
- **Better fix:** keep **separate** simulation and interface caches. For example, redirect interface reads to a second dictionary in the transpiler. Simulation entries are then only written by simulation, stamped with the ticking map's `mapTicks`, and valid for the vanilla duration. That keeps the determinism fix and brings back vanilla's cache speed.

### C3. 🔴✅ Desync tracing on every RNG call, on by default

- **Where:** `ServerSettings.cs:24` (`desyncTraces = true`), `MpSettings.cs:34` (`Fast`), `DeferredStackTracing.cs:23-24` (postfix on `Rand.Value`/`Rand.Int`).
- **What each simulation RNG call does:**
  - a native stack walk
  - a hash
  - a pooled log item
  - **one int appended to `desyncStackTraceHashes`**
- Spawns, despawns, ID allocation and `EndCurrentJob` (with string interpolation) add more.
- **What gets sent.** Every 30 timer steps the host serializes the **whole list** (`ClientSyncOpinion.cs:108`). It uses `SendFragmented` (`ConstantTicker.cs:123`), and the server passes it on to every client. The size grows with RNG calls × speed multiplier.
- **Fix:**
  - Send one rolling hash per tick or step instead of one per call.
  - Keep the full per-call list only locally, and fetch or compare it after a mismatch (the desync handler already exchanges traces).
  - Add a host setting such as "Performance mode", and default traces off at Hyperspeed.

### C4. 🔴✅ The variable tick rate is effectively off on viewed maps

- **Where:** `VTRSyncPatch.cs:20,63` and `AsyncTimeComp.cs:101`: `VTR => CurrentPlayerCount > 0 ? 1 : 15`.
- **Vanilla:** `GenTicks.GetCameraUpdateRate` gives things in view `zoom+1` (1–5) and things out of view **15**.
- **MP:** any map with a player on it runs `TickInterval` **every tick for every Thing**. That is the 1.6 worst case, applied to the whole map. The code says this is on purpose, to keep animation timing matched.
- **Fix:** use a synced but coarser value (e.g., 3–5 for viewed maps and 15 otherwise). Vanilla already pins the classes that need 1: `PawnFlyer`, `Projectile` (MP handles this one), `Fire` (15).

### C5. 🟠✅ 1.6 concurrent pathfinding thrown away

- **Where:** `AsyncTimeComp.cs:144` calls `map.pathFinder.ForceCompleteScheduledJobs()` right after `MapPreTick`, which has just scheduled the path jobs. `PathFinderPatch.cs` (the snapshot approach) is `#if false`.
- **Effect:** the main thread waits for path jobs on every tick (×M). The worker threads still run in parallel with each other, but no longer in parallel with the tick.
- **Fix:** finish the snapshot approach: take position and collision snapshots before dispatch, then complete the jobs at the *next* `MapPreTick` like vanilla does.

### C6. 🟠✅ Map → comp lookups on hot paths

- **Where:** `Util/Extensions.cs:39,49`.
  - `MpComp()` is LINQ `FirstOrDefault` **with a closure allocation on every call**.
  - `AsyncTime()` is a linear scan.
- **Hot callers:**
  - `AsyncTimeComp.TickRateMultiplier`, which runs on every micro-tick. In non-async mode it runs once per map (§C8).
  - `TickManager.Paused` / `TickRateMultiplier` postfixes. These are hit by `FleckMaker`, `Sample.Update` (several reads), `PawnRenderUtility` and `PawnTweener`, every frame.
  - `PopFaction(map)`.
- **Fix:** MP already requires Prepatcher, so add `[PrepatcherField]` backing fields on `Map`.

### C7. 🟠✅ Patching every `Tick*` override instead of `Thing.DoTick`

- **Where:** `MultiplayerStatic.cs` puts a prefix plus finalizer on every declared `Tick`, `TickRare`, `TickLong`, `TickInterval` and `SpawnSetup` on **all** Thing subtypes, including modded ones.
  - Vanilla alone has 169 of these overrides on Thing subclasses.
  - Base calls re-push the context, so `Building_TurretGun.Tick` → `base.Tick` pushes 2–3 times.
- **Better:** 1.6 has one entry point, `Thing.DoTick`. A single patch there, plus `SpawnSetup`, would cover it with a fraction of the cost.

### C8. 🟠✅ Async-time hot spots from the earlier review are still unfixed

`claude/async-performance-review.md` §2.1–2.3 still applies to the current code:

- `TimePerTick` is recomputed on every micro-tick (`TickPatch.TickTickable`).
- Non-async `ActualRateMultiplier` loops over all maps. Each map does two session scans plus a `MpComp` LINQ call.
- `AsyncWorldTimeComp.DesiredTimeSpeed` is a LINQ chain that allocates.
- `CacheNothingHappening` scans every colonist on every tick.
- `MultiplayerAsyncQuest.TickQuests` calls `ToList()` on every tick.
- Glow, power, region and room updates plus `ProcessPostTickVisuals` run on every tick instead of every frame.

### C9. 🟠✅ Join points and autosaves reload the whole game on every peer

- **What happens.** `CreateJoinPointAndSendIfHost` → `SaveReloadAndCreateSnapshot` runs as a synced command, so **everyone** reloads the full world.
- **What is already improved.** The fork's `ReloadOptimization` cuts map-drawer rebuilds to one.
- **What isn't used.** Upstream's cached `WorldGrid` and map drawer reuse is **dead code**: upstream's `copyFrom` assignments are commented out, and the fork deleted `CacheForReloading.cs`. 1.6's planet layers make world rebuilds costly.
- **Double work.** `Autosaving.DoAutosave` serializes once for the file, and then the join point serializes and reloads again.
- **Fix:** bring back and test the world grid and drawer reuse, and reuse the join-point snapshot for the autosave file.

### C10. 🟡✅ Smaller items

- `TipSignalCtor` wraps every tooltip in a new closure.
- `AllTickables` is a `yield` iterator created several times per step, which matters most during catch-up.
- In debug builds, `EndCurrentJobPatch` builds `$"EndCurrentJob for {pawn}: {job}"` on every simulation job end.
- `ConstantTicker` runs LINQ `Any()` every step.

---

## D. What I checked and found OK

- **Lambda-ordinal registrations (314).**
  - All resolve to a vanilla body that matches their comment in 1.6.4633. This includes the gravship picker cancel/confirm (#4/#5) and `CompPlantable` #0 (transpiler) / #3 (confirmation).
  - Only harmless **comment mix-ups** turned up. All the affected lambdas are registered anyway:
    - `CompTreeConnection` #2/#3 (the ±10% labels are swapped)
    - `CompMechCarrier` #2–4
    - `Building_SubcoreScanner` #6/#7
  - The full table is in `lambda_ordinal_audit.csv`.
- **FloatMenuOptionProviders (53).** Everything goes through `TryTakeOrderedJob` or a synced helper. The multi-pawn goto `EndCurrentJob` branch is **already fixed** in the fork (`Feedback.cs` `CustomEndCurrentJob`).
- **PawnColumnWorkers.** 1.6's `animalDig` and `animalForage` are synced (`SyncFields.cs:111-118`). Medical care and hostility response are watched.
- **Designators.** Handled generically, including Plan add/copy/paste, `Designator_Paint.colorDef` and `Designator_MoveGravship`. The one gap is `Designator_Build` (§B3).
- **Windows and ITabs.**
  - All reachable state-changing dialogs are covered: trade, caravan, transporters, portals, rituals, psychic rituals, growth moments, styling, gene assembler, xenogerm selection, storyteller (host-only), rename via `IRenameable`.
  - `Dialog_AnomalySettings` is only reachable at game creation.
- **`Find.CurrentMap` reads in ticks.** Covered by the fork's "P1b" change, which sets the ticked map as current. Specific patches also exist for `UndercaveMapComponent`, `PawnsArrivalModeWorker_EmergeFromWater`, wind and fish-shadow hashes, `FastTileFinder` and `MapBrightnessTracker`.
- **1.6 variable-tick-rate hash offsets.** `WorldObject` uses `ID`, and `Thing` overrides `GetHashCode`. No issue.
- **`WealthWatcher.ForceRecount`.** Only blocked from the interface. That is correct.

---

## E. Environment notes

- A copy of `TimeSpeedButton_Hyperspeed.png` sits in the **Harmony** workshop folder (`2009463077/Current/Textures/UI/TimeControls/`, dated 2026-09-05). The fork ships its own copy under `Textures/`, so this one is a leftover. Steam will delete it on the next Harmony update, which is harmless. Removing it avoids having the same texture path defined twice.
- AGENTS.md says Workshop item `2606448745` must stay disabled. That still applies.

---

## F. Suggested order and how to verify

1. **Quick wins, no design change:**
   - §C1 turn the diagnostics off
   - §A2 one-line tick-list fix
   - §A1 clamp `curTimeSpeed` and remove the blanket call-tracker disable
   - §B8 add `SetDebugOnly`
   - §A8 switch to `GetLambda`
2. **Determinism:** §A4 frame cache, §A3 gravship end sync, §A5/§A6 async-time world/map split.
3. **Performance:** §C2 split caches → §C6 Prepatcher fields → §C3 trace redesign → §C4 VTR → §C7 → §C5 → §C8/§C9.
4. **Features:** §B2, §B3, then §B1 (a new session type), then §B4.

**Live checks with GABS/RimBridge.** These need the game running, so ask before launching; it takes over the screen.

- **§A1.** Host at Hyperspeed with `DeterministicAnimalIdleCalls` removed. Check that `Player.log` no longer shows `IdleCallVolumeFactor` after the clamp.
- **§A2.** Build 50 walls, then compare the size of `map.AsyncTime().tickListNormal` before and after. Also check that a corpse dropped twice ticks once per tick.
- **§A3.** Run two instances, with the host's gravship cutscenes off and the client's on. Launch, and watch for a "Random state" desync or a mismatch in map count.
- **§C1–C4.** Compare `SimpleProfiler` and TPS at 25× with the stat-cache split, tracing off, and VTR 3.
