using HarmonyLib;
using Multiplayer.Client.AsyncTime;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    /// <summary>
    /// Vanilla runs PostTickVisuals once per frame (Map.MapUpdate) with the number of ticks done that frame.
    /// MP runs it at the end of every game tick for every spawned pawn (DrawTrackerTickPatch removes the
    /// camera check), because part of it feeds the simulation. It is split in two:
    ///  - still every tick, for every spawned pawn, exactly as before: rotation (pawn.Rotation), jitter and
    ///    lean (both part of Pawn_DrawTracker.DrawPos, which verbs read for projectile origins and facing)
    ///    and the renderer step (animation reset, downed wiggler);
    ///  - once per frame in Map.MapUpdate, for pawns in view, with the ticks accumulated since the last
    ///    frame (like vanilla): footprints, breath puffs and water ripples. These only spawn flecks (which
    ///    run with their own random state) and keep purely visual state, and they are the costly part
    ///    (drawn position, terrain, snow and ambient temperature lookups).
    /// </summary>
    [HarmonyPatch(typeof(PostTickVisuals), nameof(PostTickVisuals.ProcessPostTickVisuals))]
    public static class PostTickVisualsPerFrame
    {
        // Larger counts behave the same for these effects; this only bounds catch-up frames.
        private const int MaxTicksPerFrame = 250;

        public static void TickSimulationVisuals(Map map)
        {
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                var pawn = pawns[i];
                if (pawn.Suspended || !pawn.Spawned) continue;

                var drawer = pawn.Drawer;
                drawer.jitterer.ProcessPostTickVisuals(1);
                drawer.leaner.ProcessPostTickVisuals(1);
                drawer.renderer.ProcessPostTickVisuals(1);
                pawn.rotationTracker.ProcessPostTickVisuals(1);
            }
        }

        static bool Prefix(PostTickVisuals __instance)
        {
            if (Multiplayer.Client == null) return true;

            // Outside Map.MapUpdate (e.g. another mod calling it) there is nothing to do in MP:
            // rotations were already updated in the tick.
            if (!MapUpdateMarker.updating) return false;

            var map = __instance.map;
            var comp = map?.AsyncTime();
            if (comp == null || comp.pendingVisualTicks <= 0) return false;

            int ticks = Mathf.Min(comp.pendingVisualTicks, MaxTicksPerFrame);
            comp.pendingVisualTicks = 0;

            var viewRect = Find.CameraDriver.CurrentViewRect.ExpandedBy(3);
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                var pawn = pawns[i];
                if (pawn.Suspended || !pawn.Spawned) continue;
                if (Current.ProgramState != ProgramState.Playing || viewRect.Contains(pawn.Position))
                {
                    var drawer = pawn.Drawer;
                    drawer.footprintMaker.ProcessPostTickVisuals(ticks);
                    drawer.breathMoteMaker.ProcessPostTickVisuals(ticks);
                    drawer.waterRippleMaker.ProcessPostTickVisuals(ticks);
                }
            }

            return false;
        }
    }
}
