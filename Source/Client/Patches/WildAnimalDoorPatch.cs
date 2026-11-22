using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client;

[HarmonyPatch(typeof(Building_Door), nameof(Building_Door.PawnCanOpen))]
public static class WildAnimalDoorPatch
{
    static void Postfix(Pawn p, ref bool __result)
    {
        // Vanilla can let factionless wildlife open unclaimed doors.
        if (__result && p is { Faction: null, RaceProps.Animal: true })
            __result = false;
    }
}
