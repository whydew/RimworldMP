using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Multiplayer.Client.Util;

namespace Multiplayer.Client
{
    // ---------------------------------------------------------------------------
    // TEMPORARY DIAGNOSTIC v2 — not a fix.
    //
    // Test #1 proved the divergence is the tend *eligibility* decision (whether
    // WorkGiver_Tend issues a self-tend job), not the medicine count/split:
    // Desync-75, pawn 45554, tick 63362 — the machine VIEWING the pawn's map
    // issued a self-tend job (GetNextJobID) while the machine NOT viewing it did
    // not. Medicine was null (medId=-1), so no split was involved.
    //
    // v2 keeps the v1 logging and adds the eligibility inputs at the decision
    // point, so the next capture pins the exact value that flips with the view:
    //   - HealthAIUtility.ShouldBeTendedNowByPlayer(pawn)
    //   - pawn.health.HasHediffsNeedingTend()
    //   - count of hediffs that are TendableNow
    //
    // Only logs during simulation (not the interface). Off by default: turn on
    // "Diagnostic logging" in the MP settings (dev mode). Lines go to
    // MpLogs/MpDiagnostics.log on each player (no stack traces, and they don't count
    // toward RimWorld's 10k log-message limit). Collect it from BOTH players.
    // Remove this file once the fix is in.
    // ---------------------------------------------------------------------------
    public static class MpSelfTendDiag
    {
        public static bool Enabled => MpDiagLog.Enabled;

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

        public static int TendableNowCount(Pawn p)
        {
            try
            {
                int n = 0;
                var hs = p?.health?.hediffSet?.hediffs;
                if (hs == null) return -1;
                for (int i = 0; i < hs.Count; i++)
                    if (hs[i].TendableNow()) n++;
                return n;
            }
            catch { return -2; }
        }
    }

    // NEW in v2 — the eligibility gate. This is the value that flipped with the view.
    [HarmonyPatch(typeof(HealthAIUtility), nameof(HealthAIUtility.ShouldBeTendedNowByPlayer))]
    static class MpSelfTendDiag_ShouldBeTended
    {
        static void Postfix(Pawn pawn, bool __result)
        {
            try
            {
                if (!MpSelfTendDiag.ShouldLog(pawn)) return;
                bool needsTend = false;
                try { needsTend = pawn.health.HasHediffsNeedingTend(); } catch { }
                MpDiagLog.Write($"[MPSELFTEND] ShouldBeTendedNowByPlayer pawn={pawn.thingIDNumber} " +
                            $"result={__result} needsTendNow={needsTend} tendableNow={MpSelfTendDiag.TendableNowCount(pawn)} " +
                            MpSelfTendDiag.Ctx(pawn));
            }
            catch { }
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
                MpDiagLog.Write($"[MPSELFTEND] GetMedicineCountToFullyHeal pawn={pawn.thingIDNumber} " +
                            $"count={__result} {MpSelfTendDiag.Ctx(pawn)}");
            }
            catch { }
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

                MpDiagLog.Write($"[MPSELFTEND] JobOnThing worker={pawn.thingIDNumber} patient={t?.thingIDNumber} " +
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

                MpDiagLog.Write($"[MPSELFTEND] TryStartCarry carrier={pawn.thingIDNumber} medId={item.thingIDNumber} " +
                            $"medStack={item.stackCount} count={count} willSplit={willSplit} " +
                            MpSelfTendDiag.Ctx(pawn));
            }
            catch { }
        }
    }
}
