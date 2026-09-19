using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Gilzoide.ManagedJobs;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using Verse;

namespace Multiplayer.Client.Patches
{
    /// <summary>
    /// Lets 1.6's pathfinding jobs run in parallel with the map tick again, deterministically.
    ///
    /// Vanilla schedules path jobs at the end of MapPreTick and completes them at the start of the next
    /// one, so worker threads run while the main thread ticks. MP used to force-complete them right away
    /// (AsyncTimeComp.Tick) because some job inputs are live data the tick changes, which made results
    /// depend on thread timing. Instead, those inputs are now fixed at schedule time:
    ///  - PathGridDoorsBlockedJob (a managed job that reads doors, cost providers and pawn positions) runs
    ///    synchronously on the main thread when it's scheduled, before any thing ticks;
    ///  - the native grids a grid job reads that the tick can change (avoid grid, which regenerates
    ///    lazily; lord walk grid; request customizer such as the breaching grid) are copied.
    /// Everything else the Burst jobs read is owned by PathFinderMapData and only changes in GatherData,
    /// which vanilla only calls after completing all scheduled jobs.
    ///
    /// Toggle with the synced debug action "Toggle concurrent pathfinding" (Multiplayer category).
    /// </summary>
    public static class ConcurrentPathfinding
    {
        public static bool Active =>
            Multiplayer.Client != null && Multiplayer.game?.gameComp is { concurrentPathfinding: true };

        private static readonly Dictionary<Map, List<IDisposable>> snapshots = new();

        internal static void AddSnapshot(Map map, IDisposable snapshot)
        {
            if (!snapshots.TryGetValue(map, out var list))
                snapshots[map] = list = new List<IDisposable>();
            list.Add(snapshot);
        }

        /// <summary>Must only be called once every job that could read the snapshots has completed.</summary>
        internal static void DisposeSnapshots(Map map)
        {
            if (map == null || !snapshots.TryGetValue(map, out var list)) return;

            foreach (var snapshot in list)
                snapshot.Dispose();
            list.Clear();
            snapshots.Remove(map);
        }

        internal static JobHandle RunDoorsJobNow(ref ManagedJob job, JobHandle dependsOn)
        {
            if (!Active)
                return job.Schedule(dependsOn);

            // The doors job doesn't depend on the grid/path jobs before it in the chain; it only
            // writes this request's own providerCost/blocked arrays, which the path job reads next.
            job.Run();
            return dependsOn;
        }
    }

    [HarmonyPatch(typeof(PathFinder), nameof(PathFinder.ScheduleBatchedPathJobs))]
    static class PathFinderRunDoorsJobOnMainThread
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts, MethodBase original)
        {
            var schedule = AccessTools.Method(typeof(ManagedJob), nameof(ManagedJob.Schedule));
            var replacement = AccessTools.Method(typeof(ConcurrentPathfinding), nameof(ConcurrentPathfinding.RunDoorsJobNow));
            var found = 0;

            foreach (var inst in insts)
            {
                if (inst.Calls(schedule))
                {
                    // Instance call on a struct address (ref ManagedJob, JobHandle) -> static (ref ManagedJob, JobHandle)
                    inst.opcode = OpCodes.Call;
                    inst.operand = replacement;
                    found++;
                }

                yield return inst;
            }

            if (found != 1)
                Log.Error($"MP: {nameof(PathFinderRunDoorsJobOnMainThread)} expected 1 ManagedJob.Schedule call in {original}, found {found}");
        }
    }

    [HarmonyPatch(typeof(PathFinderMapData), nameof(PathFinderMapData.ParameterizeGridJob))]
    static class PathFinderSnapshotGridJobInputs
    {
        static void Postfix(PathFinderMapData __instance, ref PathGridJob job)
        {
            if (!ConcurrentPathfinding.Active) return;

            var map = __instance.map;

            if (job.avoidGrid.Length > 0)
            {
                var copy = new NativeArray<byte>(job.avoidGrid.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                NativeArray<byte>.Copy(job.avoidGrid, copy);
                job.avoidGrid = copy.AsReadOnly();
                ConcurrentPathfinding.AddSnapshot(map, copy);
            }

            if (job.custom.Length > 0)
            {
                var copy = new NativeArray<ushort>(job.custom.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                NativeArray<ushort>.Copy(job.custom, copy);
                job.custom = copy.AsReadOnly();
                ConcurrentPathfinding.AddSnapshot(map, copy);
            }

            if (job.lordGrid.Length > 0)
            {
                var src = job.lordGrid;
                var copy = new NativeBitArray(src.Length, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                for (int i = 0; i < src.Length; i++)
                    if (src.IsSet(i)) copy.Set(i, true);
                job.lordGrid = copy.AsReadOnly();
                ConcurrentPathfinding.AddSnapshot(map, copy);
            }
        }
    }

    [HarmonyPatch(typeof(PathFinder), nameof(PathFinder.ForceCompleteScheduledJobs))]
    static class PathFinderDisposeSnapshotsAfterComplete
    {
        static void Postfix(PathFinder __instance) => ConcurrentPathfinding.DisposeSnapshots(__instance.map);
    }

    // FactionSource disposes that faction's cost grid immediately; make sure no grid job still reads it.
    [HarmonyPatch(typeof(PathFinderMapData), nameof(PathFinderMapData.Notify_FactionRemoved))]
    static class PathFinderCompleteBeforeFactionRemoved
    {
        static void Prefix(PathFinderMapData __instance)
        {
            if (ConcurrentPathfinding.Active)
                __instance.map?.pathFinder?.ForceCompleteScheduledJobs();
        }
    }

    [HarmonyPatch(typeof(PathFinder), nameof(PathFinder.Dispose))]
    static class PathFinderDisposeSnapshotsOnDispose
    {
        // Vanilla Dispose completes all scheduled jobs first.
        static void Postfix(PathFinder __instance) => ConcurrentPathfinding.DisposeSnapshots(__instance.map);
    }
}
