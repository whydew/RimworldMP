using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client;

/// <summary>
/// Wild (factionless) animals can never open doors — not even via the vanilla
/// "unclaimed door" exception (<c>Building_Door.PawnCanOpen</c> returns
/// <c>p.RaceProps.canOpenFactionlessDoors</c> when the door itself has no faction),
/// and not via any other branch that would otherwise let a factionless critter
/// through a player-owned door.
///
/// This is a deliberate gameplay-rules change, not a determinism fix: it removes
/// door-opening from wildlife entirely, in both single-player and multiplayer.
///
/// A postfix is used rather than a rewrite of the vanilla branch so the change is
/// robust across game versions and other door mods: it only ever downgrades
/// true -> false, and never grants access vanilla denied.
///
/// Deterministic and side-effect free, so it is safe both for multiplayer sync and
/// for the worker threads that evaluate door blocking during pathfinding.
///
/// NOTE: this stops an animal from *opening* a closed door. A door that is already
/// standing open (FreePassage) stays walkable to everything, as in vanilla.
/// </summary>
[HarmonyPatch(typeof(Building_Door), nameof(Building_Door.PawnCanOpen))]
public static class Patch_Building_Door_BlockWildAnimals
{
    static void Postfix(Pawn p, ref bool __result)
    {
        if (!__result)
            return;

        // Wild == no faction. Tamed/colony animals, raider war animals, insectoids,
        // mechanoids and Anomaly entities all carry a faction and are unaffected,
        // as are wildmen (humanlike, not RaceProps.Animal).
        if (p is { Faction: null } && p.RaceProps is { Animal: true })
            __result = false;
    }
}
