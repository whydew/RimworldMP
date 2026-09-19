using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Multiplayer.Client.AsyncTime;
using Multiplayer.Client.Util;
using RimWorld.QuestGen;
using UnityEngine;
using Verse;
using Random = UnityEngine.Random;

namespace Multiplayer.Client.Patches
{
    [EarlyPatch]
    [HarmonyPatch(typeof(PawnTweener))]
    [HarmonyPatch(nameof(PawnTweener.TweenedPos), MethodType.Getter)]
    static class DrawPosPatch
    {
        public static bool returnTruePosition = false;

        static bool Prefix() => Multiplayer.Client == null || Multiplayer.InInterface || returnTruePosition;

        // Give the root position during ticking
        static void Postfix(PawnTweener __instance, ref Vector3 __result)
        {
            if (Multiplayer.Client == null || Multiplayer.InInterface || returnTruePosition) return;
            __result = __instance.TweenedPosRoot();
        }
    }

    [HarmonyPatch]
    static class FixApparelSort
    {
        static MethodBase TargetMethod() =>
            MpMethodUtil.GetLambda(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.SortWornApparelIntoDrawOrder));

        static void Postfix(Apparel a, Apparel b, ref int __result)
        {
            if (__result == 0)
                __result = a.thingIDNumber.CompareTo(b.thingIDNumber);
        }
    }

    [HarmonyPatch(typeof(Pawn_MeleeVerbs), nameof(Pawn_MeleeVerbs.TryGetMeleeVerb))]
    static class TryGetMeleeVerbPatch
    {
        static bool Cancel => Multiplayer.Client != null && Multiplayer.InInterface;

        static bool Prefix()
        {
            // Namely FloatMenuUtility.GetMeleeAttackAction
            return !Cancel;
        }

        static void Postfix(Pawn_MeleeVerbs __instance, Thing target, ref Verb __result)
        {
            if (Cancel)
                __result = __instance.GetUpdatedAvailableVerbsList(false).FirstOrDefault(ve => ve.GetSelectionWeight(target) != 0).verb;
        }
    }

    [HarmonyPatch(typeof(PawnBioAndNameGenerator), nameof(PawnBioAndNameGenerator.TryGetRandomUnusedSolidName))]
    static class GenerateNewPawnInternalPatch
    {
        static MethodBase FirstOrDefault = SymbolExtensions.GetMethodInfo<IEnumerable<NameTriple>>(
            e => e.FirstOrDefault()
        );

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> e)
        {
            List<CodeInstruction> insts = new List<CodeInstruction>(e);

            for (int i = insts.Count - 1; i >= 0; i--)
            {
                if (insts[i].operand as MethodBase == FirstOrDefault)
                    insts.Insert(
                       i + 1,
                       new CodeInstruction(OpCodes.Ldloc_1),
                       new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GenerateNewPawnInternalPatch), nameof(Unshuffle)).MakeGenericMethod(typeof(NameTriple)))
                   );
            }

            return insts;
        }

        public static void Unshuffle<T>(List<T> list)
        {
            uint iters = Rand.iterations;

            int i = 0;
            while (i < list.Count)
            {
                int index = Mathf.Abs(MurmurHash.GetInt(Rand.seed, iters--) % (i + 1));
                (list[index], list[i]) = (list[i], list[index]);
                i++;
            }
        }
    }

    [HarmonyPatch(typeof(WorldObjectSelectionUtility), nameof(WorldObjectSelectionUtility.VisibleToCameraNow))]
    static class CaravanVisibleToCameraPatch
    {
        static void Postfix(ref bool __result)
        {
            if (!Multiplayer.InInterface)
                __result = false;
        }
    }

    [HarmonyPatch(typeof(WealthWatcher), nameof(WealthWatcher.ForceRecount))]
    static class WealthWatcherRecalc
    {
        static bool Prefix() => Multiplayer.Client == null || !Multiplayer.InInterface;
    }

    [HarmonyPatch(typeof(FloodFillerFog), nameof(FloodFillerFog.FloodUnfog))]
    static class FloodUnfogPatch
    {
        static void Postfix(ref FloodUnfogResult __result)
        {
            if (Multiplayer.Client != null)
                __result.allOnScreen = false;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ProcessPostTickVisuals))]
    static class DrawTrackerTickPatch
    {
        static MethodInfo CellRectContains = AccessTools.Method(typeof(CellRect), nameof(CellRect.Contains));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var inst in insts)
            {
                yield return inst;

                if (inst.operand as MethodInfo == CellRectContains)
                {
                    yield return new CodeInstruction(OpCodes.Ldc_I4_1);
                    yield return new CodeInstruction(OpCodes.Or);
                }
            }
        }
    }

    public static class CellsShufflePatchShared
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts, FieldInfo cellsShuffledField)
        {
            bool found = false;
            foreach (CodeInstruction inst in insts)
            {
                yield return inst;
                if (!found && inst.operand as FieldInfo == cellsShuffledField)
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CellsShufflePatchShared), nameof(ShouldShuffle)));
                    yield return new CodeInstruction(OpCodes.Not);
                    yield return new CodeInstruction(OpCodes.Or);
                    found = true;
                }
            }
        }

        public static bool ShouldShuffle()
        {
            return Multiplayer.Client == null || Multiplayer.Ticking;
        }
    }

    [HarmonyPatch(typeof(Zone), nameof(Zone.Cells), MethodType.Getter)]
    static class ZoneCellsShufflePatch
    {
        static readonly FieldInfo CellsShuffled = AccessTools.Field(typeof(Zone), "cellsShuffled");
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
            => CellsShufflePatchShared.Transpiler(insts, CellsShuffled);
    }

    [HarmonyPatch(typeof(Plan), nameof(Plan.Cells), MethodType.Getter)]
    static class PlanCellsShufflePatch
    {
        static readonly FieldInfo CellsShuffled = AccessTools.Field(typeof(Plan), "cellsShuffled");
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
            => CellsShufflePatchShared.Transpiler(insts, CellsShuffled);
    }

    [HarmonyPatch]
    static class SortArchivablesById
    {
        static MethodBase TargetMethod()
        {
            return MpMethodUtil.GetLambda(typeof(Archive), nameof(Archive.Add));
        }

        static void Postfix(IArchivable x, ref int __result)
        {
            if (x is ArchivedDialog dialog)
                __result = dialog.ID;
            else if (x is Letter letter)
                __result = letter.ID;
            else if (x is Message msg)
                __result = msg.ID;
        }
    }

    [HarmonyPatch(typeof(DangerWatcher), nameof(DangerWatcher.DangerRating), MethodType.Getter)]
    static class DangerRatingPatch
    {
        static bool Prefix() => !Multiplayer.InInterface;

        static void Postfix(DangerWatcher __instance, ref StoryDanger __result)
        {
            if (Multiplayer.InInterface)
                __result = __instance.dangerRatingInt;
        }
    }

    [HarmonyPatch(typeof(Caravan), nameof(Caravan.ImmobilizedByMass), MethodType.Getter)]
    static class ImmobilizedByMass_Patch
    {
        static bool Prefix() => !Multiplayer.InInterface;
    }

    [HarmonyPatch(typeof(StoryWatcher_PopAdaptation), nameof(StoryWatcher_PopAdaptation.Notify_PawnEvent))]
    static class CancelStoryWatcherEventInInterface
    {
        static bool Prefix() => !Multiplayer.InInterface;
    }

    [HarmonyPatch(typeof(AutoSlaughterManager), nameof(AutoSlaughterManager.Notify_ConfigChanged))]
    static class CancelAutoslaughterDirtying
    {
        static bool Prefix() => !Multiplayer.InInterface;
    }

    [HarmonyPatch(typeof(Pawn_AbilityTracker), nameof(Pawn_AbilityTracker.AllAbilitiesForReading), MethodType.Getter)]
    static class DontRecacheAbilitiesInInterface
    {
        static bool Prefix() => !Multiplayer.InInterface;

        static void Postfix(Pawn_AbilityTracker __instance, ref List<Ability> __result)
        {
            // The result can be null only if the method gets cancelled by the prefix
            if (__result == null)
                __result = __instance.allAbilitiesCached;
        }
    }

    [HarmonyPatch(typeof(PriorityWork), nameof(PriorityWork.Clear))]
    static class PriorityWorkClearNoInterface
    {
        // This can get called in the UI but has side effects
        static bool Prefix(PriorityWork __instance)
        {
            return Multiplayer.Client == null || Multiplayer.ExecutingCmds;
        }
    }

    [HarmonyPatch(typeof(Pawn_GeneTracker), nameof(Pawn_GeneTracker.Notify_GenesChanged))]
    static class CheckWhetherBiotechIsActive
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var inst in insts)
            {
                if (inst.operand as MethodInfo == AccessTools.PropertyGetter(typeof(ModLister), nameof(ModLister.BiotechInstalled)))
                    inst.operand = AccessTools.PropertyGetter(typeof(ModsConfig), nameof(ModsConfig.BiotechActive));
                yield return inst;
            }
        }
    }

    [HarmonyPatch]
    static class UpdateWorldStateWhenTickingOnly
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            // Handles relation and retaliation for polluting the world
            yield return AccessTools.DeclaredMethod(typeof(CompDissolutionEffect_Goodwill), nameof(CompDissolutionEffect_Goodwill.WorldUpdate));
            // Handles increasing/decreasing world pollution
            yield return AccessTools.DeclaredMethod(typeof(CompDissolutionEffect_Pollution), nameof(CompDissolutionEffect_Pollution.WorldUpdate));
        }

        static bool Prefix()
        {
            // In MP only allow updates from MultiplayerWorldComp:Tick()
            return Multiplayer.Client == null || AsyncWorldTimeComp.tickingWorld;
        }
    }

    [HarmonyPatch(typeof(Pawn_RecordsTracker), nameof(Pawn_RecordsTracker.ExposeData))]
    static class RecordsTrackerExposePatch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            var battleActiveField =
                AccessTools.Field(typeof(Pawn_RecordsTracker), nameof(Pawn_RecordsTracker.battleActive));

            foreach (var inst in insts)
            {
                // Remove mutation of battleActive during saving which was a source of non-determinism
                if (inst.opcode == OpCodes.Stfld && inst.operand as FieldInfo == battleActiveField)
                {
                    yield return new CodeInstruction(OpCodes.Pop);
                    yield return new CodeInstruction(OpCodes.Pop);
                }
                else
                    yield return inst;
            }
        }
    }

    [HarmonyPatch(typeof(SituationalThoughtHandler), nameof(SituationalThoughtHandler.CheckRecalculateSocialThoughts))]
    static class DontRecalculateSocialThoughtsInInterface
    {
        static bool Prefix(SituationalThoughtHandler __instance, Pawn otherPawn)
        {
            if (Multiplayer.Client == null) return true;
            if (Multiplayer.Ticking || Multiplayer.ExecutingCmds) return true;

            // This initializer needs to always run (the method itself begins with it)
            if (!__instance.cachedSocialThoughts.TryGetValue(otherPawn, out var value))
            {
                value = new SituationalThoughtHandler.CachedSocialThoughts();
                __instance.cachedSocialThoughts.Add(otherPawn, value);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(SituationalThoughtHandler), nameof(SituationalThoughtHandler.AppendSocialThoughts))]
    static class DontUpdateThoughtQueryTickInInterface
    {
        private static FieldInfo queryTickField = AccessTools.Field(typeof(SituationalThoughtHandler.CachedSocialThoughts), nameof(SituationalThoughtHandler.CachedSocialThoughts.lastQueryTick));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var inst in insts)
            {
                if (inst.opcode == OpCodes.Stfld && inst.operand as FieldInfo == queryTickField)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DontUpdateThoughtQueryTickInInterface), nameof(NewQueryTick)));
                }

                yield return inst;
            }
        }

        private static int NewQueryTick(int ticks, SituationalThoughtHandler thoughtHandler, Pawn otherPawn)
        {
            return Multiplayer.Client == null || Multiplayer.Ticking || Multiplayer.ExecutingCmds ?
                ticks :
                thoughtHandler.cachedSocialThoughts[otherPawn].lastQueryTick;
        }
    }

    [HarmonyPatch(typeof(SituationalThoughtHandler), nameof(SituationalThoughtHandler.UpdateAllMoodThoughts))]
    static class DontRecalculateMoodThoughtsInInterface
    {
        static bool Prefix(SituationalThoughtHandler __instance)
        {
            if (Multiplayer.Client != null && !Multiplayer.Ticking && !Multiplayer.ExecutingCmds) return false;

            // Notify_SituationalThoughtsDirty was called
            if (__instance.thoughtsDirty)
                __instance.cachedThoughts.Clear();

            return true;
        }
    }

    [HarmonyPatch(typeof(SituationalThoughtHandler), nameof(SituationalThoughtHandler.Notify_SituationalThoughtsDirty))]
    static class NotifyThoughtsDirtyPatch
    {
        private static MethodInfo clearMethod =
            AccessTools.Method(typeof(List<Thought_Situational>), nameof(List<Thought_Situational>.Clear));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var inst in insts)
            {
                if (inst.operand as MethodInfo == clearMethod)
                    yield return new CodeInstruction(OpCodes.Pop);
                else
                    yield return inst;
            }
        }
    }

    [HarmonyPatch(typeof(PawnCapacitiesHandler), nameof(PawnCapacitiesHandler.GetLevel))]
    static class PawnCapacitiesHandlerGetLevelPatch
    {
        private static readonly PawnCapacitiesHandler.CacheStatus CachedInInterface = (PawnCapacitiesHandler.CacheStatus)3;

        private static FieldInfo statusField = AccessTools.Field(typeof(PawnCapacitiesHandler.CacheElement),
            nameof(PawnCapacitiesHandler.CacheElement.status));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            var matcher = new CodeMatcher(insts);

            // Modify cache update checking
            matcher.MatchEndForward(
                new CodeMatch(OpCodes.Ldfld, statusField),
                new CodeMatch(OpCodes.Brtrue_S)
            ).Insert(
                new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(PawnCapacitiesHandlerGetLevelPatch), nameof(ShouldUpdateCache))),
                new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Ceq)
            );

            // Modify status setter
            matcher.MatchEndForward(
                new CodeMatch(OpCodes.Ldc_I4_2),
                new CodeMatch(OpCodes.Stfld, statusField)
            ).Insert(
                new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(PawnCapacitiesHandlerGetLevelPatch), nameof(NewCacheStatus)))
            );

            return matcher.Instructions();
        }

        // --- P1a note (PawnCapacitiesHandler) ---
        // Unlike StatWorker, this cache is NOT tick-keyed: it is invalidated by a dirty flag
        // (Notify_CapacityLevelsDirty) whenever hediffs change, and the level is pure,
        // deterministic math over the pawn's hediffs (no RNG). The guard below already forces
        // the simulation to recompute any value the interface computed (CachedInInterface),
        // so during sim a capacity is always either freshly recomputed or a value the sim
        // itself computed earlier this dirty-cycle -- identical on every client regardless of
        // which map anyone is viewing. It is therefore already view-independent, and any stat
        // reads it performs go through the (now sim-recomputing) StatWorker path above.
        // We deliberately do NOT force a full recompute on every sim call here: GetLevel is an
        // extremely hot path and doing so would be a large perf regression for zero
        // determinism benefit. If instrumentation ever shows a capacity-level divergence,
        // revisit this method.
        private static bool ShouldUpdateCache(PawnCapacitiesHandler.CacheStatus status)
        {
            return status == PawnCapacitiesHandler.CacheStatus.Uncached || !Multiplayer.InInterface && status == CachedInInterface;
        }

        private static PawnCapacitiesHandler.CacheStatus NewCacheStatus(PawnCapacitiesHandler.CacheStatus _)
        {
            return Multiplayer.InInterface ? CachedInInterface : PawnCapacitiesHandler.CacheStatus.Cached;
        }
    }

    [HarmonyPatch(typeof(StatWorker), nameof(StatWorker.GetValue), typeof(Thing), typeof(bool), typeof(int))]
    static class StatWorkerGetValuePatch
    {
        // --- Stat cache split (replaces the P1a "never cache in simulation" fix) ---
        // StatWorker.temporaryStatCache is one dictionary per worker, stamped with
        // Find.TickManager.TicksGame. In MP the interface reads it with the *viewed* map's
        // time and the simulation with the *ticking* map's time, so an interface write could
        // change whether a later simulation read was a hit or a recompute, and that depended on
        // what each player was looking at (desync). P1a fixed it by never using the cache in
        // simulation, which made hot stat reads (MaxHitPoints, MaxNutrition, mental break
        // thresholds, comfortable temperature, ...) recompute every time.
        //
        // Now the simulation and the interface use separate dictionaries:
        //  - simulation (map/world ticks and commands) keeps vanilla's temporaryStatCache, so only
        //    simulation ever writes it and hits/misses are identical on every client;
        //  - the interface gets its own per-worker dictionary;
        //  - other non-interface contexts (loading, reloading, long events) don't cache at all,
        //    because they don't run identically on every client.
        // The simulation caches are cleared on game load and at every join point, so a peer that
        // didn't reload starts from the same (empty) state as one that did.
        private static readonly FieldInfo TemporaryStatCacheField =
            AccessTools.Field(typeof(StatWorker), nameof(StatWorker.temporaryStatCache));

        private static readonly Dictionary<StatWorker, Dictionary<Thing, StatCacheEntry>> interfaceCaches = new();

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts, MethodBase original)
        {
            var replaced = 0;
            var absPatched = false;
            var afterGameTickLoad = false;
            var cacheFor = AccessTools.Method(typeof(StatWorkerGetValuePatch), nameof(CacheFor));
            var gameTickField = AccessTools.Field(typeof(StatCacheEntry), nameof(StatCacheEntry.gameTick));
            var abs = AccessTools.Method(typeof(Math), nameof(Math.Abs), new[] { typeof(int) });

            foreach (var inst in insts)
            {
                if (inst.opcode == OpCodes.Ldfld && Equals(inst.operand, TemporaryStatCacheField))
                {
                    // Stack: StatWorker -> Dictionary (same shape as the field load)
                    inst.opcode = OpCodes.Call;
                    inst.operand = cacheFor;
                    replaced++;
                }

                yield return inst;

                if (!absPatched && inst.opcode == OpCodes.Ldfld && Equals(inst.operand, gameTickField))
                    afterGameTickLoad = true;

                // `ticksGame - entry.gameTick < cacheStaleAfterTicks` -> `Math.Abs(ticksGame - entry.gameTick) < ...`
                // Different maps (and the world) run on different clocks under async time, so an
                // entry stamped on a clock that is ahead must count as stale, not as fresh forever.
                if (afterGameTickLoad && inst.opcode == OpCodes.Sub)
                {
                    yield return new CodeInstruction(OpCodes.Call, abs);
                    afterGameTickLoad = false;
                    absPatched = true;
                }
            }

            if (replaced == 0 || !absPatched)
                Log.Error($"MP: {nameof(StatWorkerGetValuePatch)} didn't match {original} (cache loads: {replaced}, freshness check: {absPatched})");
        }

        internal static Dictionary<Thing, StatCacheEntry> CacheFor(StatWorker worker)
        {
            var vanilla = worker.temporaryStatCache;
            if (vanilla == null || Multiplayer.Client == null)
                return vanilla;

            if (Multiplayer.Ticking || Multiplayer.ExecutingCmds)
            {
                // Standalone servers hand joiners snapshots taken outside join points, so peers can't be
                // sure to start from the same (empty) cache. Don't cache simulation reads there.
                return Multiplayer.session?.ConnectedToStandaloneServer == true ? null : vanilla;
            }

            if (!Multiplayer.InInterface)
                return null;

            if (!interfaceCaches.TryGetValue(worker, out var ui))
                interfaceCaches[worker] = ui = new Dictionary<Thing, StatCacheEntry>();
            return ui;
        }

        /// <summary>Drops all cached stat values (simulation and interface).</summary>
        internal static void ClearAll()
        {
            interfaceCaches.Clear();

            foreach (var stat in DefDatabase<StatDef>.AllDefsListForReading)
                stat.workerInt?.temporaryStatCache?.Clear();
        }
    }

    [HarmonyPatch(typeof(GameConditionManager.MapBrightnessTracker), nameof(GameConditionManager.MapBrightnessTracker.Tick))]
    static class MapBrightnessLerpPatch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instr)
        {
            var patchCount = 0;

            // The method is using delta time for darkness changes,
            // which is not good for MP since it's tied to the FPS.
            var target = AccessTools.DeclaredPropertyGetter(typeof(Time), nameof(Time.deltaTime));

            foreach (var ci in instr)
            {
                if (ci.Calls(target))
                {
                    // Replace deltaTime with a constant value.
                    // We use 1/60 since 1 second at speed 1 the deltaTime
                    // should (in perfect situation) be 60 ticks.
                    ci.opcode = OpCodes.Ldc_R4;
                    ci.operand = 1f / GenTicks.TicksPerRealSecond;

                    patchCount++;
                }

                yield return ci;
            }

            const int expectedPatches = 1;
            if (patchCount != expectedPatches)
                Log.Error($"Replaced an incorrect amount of Time.deltaTime calls for GameConditionManager.MapBrightnessTracker:Tick (expected: {expectedPatches}, patched: {patchCount}). Was the original method changed?");
        }
    }

    [HarmonyPatch(typeof(UndercaveMapComponent), nameof(UndercaveMapComponent.MapComponentTick))]
    static class DeterministicUndercaveRockCollapse
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instr)
        {
            var target = MethodOf.Lambda(Rand.MTBEventOccurs);

            foreach (var ci in instr)
            {
                yield return ci;

                // Add "& false" to any call to Rand.MTBEventOccurs.
                // We'll handle those calls in our postfix.
                if (ci.Calls(target))
                {
                    yield return new CodeInstruction(OpCodes.Ldc_I4_0);
                    yield return new CodeInstruction(OpCodes.And);
                }
            }
        }

        static void Prefix() => Rand.PushState();

        static void Postfix(UndercaveMapComponent __instance)
        {
            // Pop the RNG state from the prefix
            Rand.PopState();

            // Make sure the pit gate is collapsing
            if (__instance.pitGate is not { IsCollapsing: true })
                return;

            // Check if the rocks should collapse
            var mtb = UndercaveMapComponent.HoursToShakeMTBTicksCurve.Evaluate(__instance.pitGate.TicksUntilCollapse / 2500f);
            if (!Rand.MTBEventOccurs(mtb, 1, 1))
                return;

            // Since the number of RNG calls will depend on numDustEffecters argument, we need to push/pop the RNG state.
            // The RNG calls related to simulation will happen first, followed by the one determined by amount of
            // effecters - it would not be MP safe, but since it happens last it will be fine once we pop the state.
            Rand.PushState();

            // If not looking at the map, trigger the collapse without shake/effecters (since it's not needed for current player).
            // The call to play a sound is handled by RW itself, since it targets a specific map already.
            if (Find.CurrentMap != __instance.map)
            {
                // Progress the RNG state, matching the RandomInRange call in other two cases
                Rand.RangeInclusive(0, 100);
                __instance.TriggerCollapseFX(0, 0);
            }
            // Else, follow vanilla shake/effecter rules
            else if (__instance.pitGate.CollapseStage == 1)
                __instance.TriggerCollapseFX(UndercaveMapComponent.StageOneShakeAmount, UndercaveMapComponent.StageOneNumCollapseEffects.RandomInRange);
            else
                __instance.TriggerCollapseFX(UndercaveMapComponent.StageTwoShakeAmount, UndercaveMapComponent.StageTwoNumCollapseEffects.RandomInRange);

            Rand.PopState();
        }
    }

    [HarmonyPatch(typeof(MoteMaker), nameof(MoteMaker.MakeStaticMote))]
    [HarmonyPatch([typeof(Vector3), typeof(Map), typeof(ThingDef), typeof(float), typeof(bool), typeof(float)])]
    static class FixNullMotes
    {
        // Make sure that motes will (almost) always spawn. We skip based to player-specific
        // data, and instead only allow the only fully deterministic checks (location is in map bounds).
        // We skip checks to current map and current mote counter saturation, as those may vary between players.

        static void Prefix(ref bool makeOffscreen)
        {
            // Skip a call to GenView.ShouldSpawnMotesAt, forcing
            // each call to check GenGrid.InBounds instead.
            // ShouldSpawnMotesAt would check for bounds, but also
            // include a current map check as well, which we don't want.
            makeOffscreen = true;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instr)
        {
            var targetCall = AccessTools.DeclaredPropertyGetter(typeof(MoteCounter), nameof(MoteCounter.Saturated));

            foreach (var ci in instr)
            {
                yield return ci;

                // Add "& false" to any call to MoteCounter.Saturated to get a deterministic result.
                // Not a perfect solution performance-wise, but will prevent desyncs due to
                // MoteMaker.MakeStaticMote being non-deterministic without this change.
                if (ci.Calls(targetCall))
                {
                    yield return new CodeInstruction(OpCodes.Ldc_I4_0);
                    yield return new CodeInstruction(OpCodes.And);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Building_BioferriteHarvester), nameof(Building_BioferriteHarvester.SpawnSetup))]
    static class AlwaysRebuildBioferriteHarvesterCables
    {
        static void Postfix(Building_BioferriteHarvester __instance)
        {
            // After reloading the game the initialize method will generally be called
            // during rendering, ensure it's called on respawning as well.
            if (!__instance.initalized)
            {
                // We need to call ExecuteWhenFinished, as it will crash otherwise.
                LongEventHandler.ExecuteWhenFinished(__instance.Initialize);
            }
        }
    }

    [HarmonyPatch(typeof(Building_Electroharvester), nameof(Building_Electroharvester.SpawnSetup))]
    static class AlwaysRebuildElectroharvesterCables
    {
        static void Postfix(Building_Electroharvester __instance)
        {
            // After reloading the game the initialize method will generally be called
            // during rendering, ensure it's called on respawning as well.
            if (!__instance.initalized)
            {
                // We need to call ExecuteWhenFinished, as it will crash otherwise.
                LongEventHandler.ExecuteWhenFinished(__instance.Initialize);
            }
        }
    }

    [HarmonyPatch(typeof(MainTabWindow), nameof(MainTabWindow.SetInitialSizeAndPosition))]
    static class MainTabWindow_NoResizingInSimulation
    {
        static bool Prefix()
        {
            return Multiplayer.Client == null || Multiplayer.InInterface;
        }
    }

    [HarmonyPatch(typeof(MainTabWindow_PawnTable), nameof(MainTabWindow_PawnTable.DoWindowContents))]
    static class MainTabWindow_ResizeIfDirty
    {
        static void Prefix(MainTabWindow_PawnTable __instance)
        {
            if (__instance.table.dirty)
                __instance.SetInitialSizeAndPosition();
        }
    }

    [HarmonyPatch(typeof(PawnsArrivalModeWorker_EmergeFromWater), nameof(PawnsArrivalModeWorker_EmergeFromWater.Arrive))]
    static class PawnsArrivalModeWorker_EmergeFromWater_FixCurrentMapUsage
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instr)
        {
            // Due to an oversight, the vanilla method is using Find.CurrentMap rather than using (Map)parms.target.
            // This causes bugs in the game, like mechanoids emerging from void/sand/random locations rather than water.
            // For us, it currently causes desyncs with multiple maps active, so we need to fix it.

            var currentMapMethod = typeof(Find).DeclaredPropertyGetter(nameof(Find.CurrentMap));
            var targetField = typeof(IncidentParms).DeclaredField(nameof(IncidentParms.target));

            var patched = 0;

            foreach (var ci in instr)
            {
                yield return ci;

                // Replace the Find.CurrentMap calls with (Map)parms.target
                if (ci.Calls(currentMapMethod))
                {
                    // Change the current instruction to load the 2nd argument (IncidentParms)
                    ci.opcode = OpCodes.Ldarg_2;
                    ci.operand = null;

                    // Load the IncidentParms.target field
                    yield return new CodeInstruction(OpCodes.Ldfld, targetField);

                    // Cast the field's value to Map
                    yield return new CodeInstruction(OpCodes.Castclass, typeof(Map));

                    patched++;
                }
            }

            const int expectedPatches = 2;
            if (patched != expectedPatches)
                Log.Error($"Patching emerge from water arrival mode failed. Expected patches: {expectedPatches}, actual patches: {patched}. There was either an issue or the bug was fixed in RimWorld itself.");
        }
    }

    // FastTileFinder.ComputeQueryJob uses Interlocked.Increment to race-fill a 50-slot result array
    // across parallel Unity Job batches. Thread scheduling differs between machines, so clients get
    // different candidate tile sets. Force single-batch execution in MP so tiles are processed in
    // tileId order, making the first 50 valid tiles consistent across all clients.
    [HarmonyPatch(typeof(FastTileFinder), nameof(FastTileFinder.Query))]
    static class FastTileFinderQueryDeterminismPatch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getIdealBatchCount = AccessTools.Method(typeof(UnityData), nameof(UnityData.GetIdealBatchCount));
            var getBatchCount = AccessTools.Method(typeof(FastTileFinderQueryDeterminismPatch), nameof(GetBatchCount));

            foreach (var instr in instructions)
            {
                if (instr.Calls(getIdealBatchCount))
                    yield return new CodeInstruction(OpCodes.Call, getBatchCount);
                else
                    yield return instr;
            }
        }

        static int GetBatchCount(int length) =>
            Multiplayer.Client != null ? length : UnityData.GetIdealBatchCount(length);
    }

    // --- Animal idle calls ---
    // Pawn_CallTracker.IdleCallVolumeFactor switches on TickManager.CurTimeSpeed and throws
    // NotImplementedException for unknown values. The old exception flood came from MP's own
    // Hyperspeed tier ((TimeSpeed)5) being written into the TickManager; that value is now
    // clamped to Ultrafast (MpTimeSpeed.SetOn), so idle calls run as in vanilla again. The call
    // interval RNG is consumed identically on every client, and the sound itself runs under
    // MP's existing sound RNG isolation. This prefix is only a safety net for any other code
    // (e.g. another mod) that leaves an out-of-range speed in the TickManager.
    [HarmonyPatch(typeof(Pawn_CallTracker), nameof(Pawn_CallTracker.IdleCallVolumeFactor), MethodType.Getter)]
    static class IdleCallVolumeFactorGuard
    {
        static bool Prefix(ref float __result)
        {
            if (Find.TickManager.curTimeSpeed <= TimeSpeed.Ultrafast) return true;
            __result = 0.25f;
            return false;
        }
    }

}
