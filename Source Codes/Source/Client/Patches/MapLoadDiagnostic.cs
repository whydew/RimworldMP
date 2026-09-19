using System.Collections.Generic;
using Multiplayer.Client.Util;
using Verse;

namespace Multiplayer.Client
{
    // ---------------------------------------------------------------------------
    // TEMPORARY P3 DIAGNOSTIC — not a fix.
    //
    // Desyncs are 100% reproducible on new-map load. This logs, on BOTH clients,
    // the new map's setup values and its first ~20 ticks so one reproduction shows
    // whether the first host/client divergence is in the map's SETUP/SEED (-> P2:
    // mapTicks / randState / forceNormalSpeedUntil differ at creation) or only once
    // it starts TICKING (-> P1: caches / current-map view leak).
    //
    // Lines are prefixed [MPMAPLOAD] and keyed by (map uniqueID, mapTicks), so the
    // two players' MpLogs/MpDiagnostics.log files diff directly. Off by default:
    // turn on "Diagnostic logging" in the MP settings (dev mode).
    // Remove this file (and its two call sites in MapSetup.SetupMap and
    // AsyncTimeComp.Tick) once the map-load fix is confirmed.
    // ---------------------------------------------------------------------------
    public static class MapLoadDiagnostic
    {
        public static bool Enabled => MpDiagLog.Enabled;
        private const int TicksToLog = 20;

        // map.uniqueID -> remaining first-ticks to log
        private static readonly Dictionary<int, int> tracked = new();

        public static void OnMapSetup(Map map, AsyncTimeComp async)
        {
            if (!Enabled || Multiplayer.Client == null || map == null || async == null) return;
            try
            {
                tracked[map.uniqueID] = TicksToLog;
                MpDiagLog.Write($"[MPMAPLOAD] setup map={map.uniqueID} startMapTicks={async.mapTicks} " +
                            $"gameStartAbsTick={async.GameStartAbsTick} randState={async.randState} " +
                            $"desiredSpeed={async.DesiredTimeSpeed} curMap={CurMap()} timer={TickPatch.Timer}");
            }
            catch { }
        }

        public static void OnTick(AsyncTimeComp async)
        {
            if (!Enabled || Multiplayer.Client == null || async?.map == null) return;
            try
            {
                if (!tracked.TryGetValue(async.map.uniqueID, out var left) || left <= 0) return;
                tracked[async.map.uniqueID] = left - 1;
                MpDiagLog.Write($"[MPMAPLOAD] tick map={async.map.uniqueID} mapTicks={async.mapTicks} " +
                            $"randState={async.randState} forceNormalUntil={async.slower.forceNormalSpeedUntil} " +
                            $"curMap={CurMap()} timer={TickPatch.Timer}");
            }
            catch { }
        }

        private static int CurMap()
        {
            try { return Find.CurrentMap?.uniqueID ?? -1; } catch { return -2; }
        }
    }
}
