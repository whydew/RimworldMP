using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Client
{
    // ---------------------------------------------------------------------------
    // TEMPORARY DIAGNOSTIC — not a fix.
    //
    // Purpose: pin the exact value that diverges between host and client during an
    // automatic self-tend, so the real determinism fix can target the right line.
    //
    // It logs, on BOTH machines, the three tend decision points that feed the
    // Thing-ID-vs-Job-ID divergence seen at Desync-69 seq 36:
    //   1. Medicine.GetMedicineCountToFullyHeal  -> the medicine "count"
    //   2. WorkGiver_Tend.JobOnThing             -> chosen medicine + count
    //   3. Pawn_CarryTracker.TryStartCarry       -> the actual split decision
    //      (count < stackCount => a new Thing ID is allocated)
    //
    // Every line also records Find.CurrentMap vs the acting pawn's map and the
    // ticking map's mapTicks, so we can confirm the divergence correlates with
    // which map each player is viewing (the confirmed trigger).
    //
    // Only logs during simulation (not the interface), so the two players' logs
    // line up by tick and can be diffed directly.
    //
    // Toggle with the in-game dev console:  MpSelfTendDiag.Enabled = false;
    // Remove this whole file once the fix is in.
    // ---------------------------------------------------------------------------
    public static class MpSelfTendDiag
    {
        public static bool Enabled = true;

        // Only log for humanlike player-faction pawns to cut spam.
        public static bool ShouldLog(Pawn p)
        {
            try
            {
                if (!Enabled) return false;
                if (Multiplayer.Client == null) return false;
                if (Multiplayer.InInterface) return false;       // sim only
                if (p == null || !p.Spawned || p.Map == null) return false;
                if (p.RaceProps == null || !p.RaceProps.Humanlike) return false;
                if (p.Faction == null || !p.Faction.IsPlayer) return false;
                return true;
            }
            catch { return false; }
        }

        public static string Ctx(Pawn p)
        {
            try
            {
                var curMap = Find.CurrentMap;
                var pawnMap = p?.Map;
                int mapTicks = -1;
                try { mapTicks = pawnMap != null ? pawnMap.AsyncTime().mapTicks : -1; } catch { }
                int curId = curMap != null ? curMap.uniqueID : -1;
                int pawnMapId = pawnMap != null ? pawnMap.uniqueID : -1;
                bool onCurrentMap = curMap != null && pawnMap != null && curMap == pawnMap;
                return $"timer={TickPatch.Timer} mapTicks={mapTicks} pawnMap={pawnMapId} curMap={curId} onViewedMap={onCurrentMap}";
            }
            catch (Exception e) { return "ctx-error:" + e.Message; }
        }
    }

    [HarmonyPatch(typeof(Medicine), nameof(Medicine.GetMedicineCountToFullyHeal))]
    static class MpSelfTendDiag_MedicineCount
    {
        static void Postfix(Pawn pawn, int __result)
        {
            try
            {
                if (!MpSelfTendDiag.ShouldLog(pawn)) return;
                Log.Message($"[MPSELFTEND] GetMedicineCountToFullyHeal pawn={pawn.thingIDNumber} " +
                            $"count={__result} {MpSelfTendDiag.Ctx(pawn)}");
            }
            catch { /* never throw into the sim */ }
        }
    }

    [HarmonyPatch(typeof(WorkGiver_Tend), nameof(WorkGiver_Tend.JobOnThing))]
    static class MpSelfTendDiag_TendJob
    {
        static void Postfix(Pawn pawn, Thing t, Job __result)
        {
            try
            {
                if (__result == null) return;
                if (!MpSelfTendDiag.ShouldLog(pawn)) return;

                bool selfTend = t == pawn;
                var med = __result.targetB.Thing;
                int medId = med != null ? med.thingIDNumber : -1;
                int medStack = med != null ? med.stackCount : -1;
                int count = __result.count;
                bool willSplit = med != null && count >= 0 && count < medStack;

                Log.Message($"[MPSELFTEND] JobOnThing worker={pawn.thingIDNumber} patient={t?.thingIDNumber} " +
                            $"selfTend={selfTend} medId={medId} medStack={medStack} count={count} willSplit={willSplit} " +
                            MpSelfTendDiag.Ctx(pawn));
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry),
        new[] { typeof(Thing), typeof(int), typeof(bool) })]
    static class MpSelfTendDiag_Carry
    {
        static void Prefix(Pawn_CarryTracker __instance, Thing item, int count)
        {
            try
            {
                if (item == null || item.def == null || !item.def.IsMedicine) return;
                var pawn = __instance.pawn;
                if (!MpSelfTendDiag.ShouldLog(pawn)) return;

                bool willSplit = count < item.stackCount;

                Log.Message($"[MPSELFTEND] TryStartCarry carrier={pawn.thingIDNumber} medId={item.thingIDNumber} " +
                            $"medStack={item.stackCount} count={count} willSplit={willSplit} " +
                            MpSelfTendDiag.Ctx(pawn));
            }
            catch { }
        }
    }
}
