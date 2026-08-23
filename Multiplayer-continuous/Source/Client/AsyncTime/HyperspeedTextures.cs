using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    /// <summary>
    /// Adds a 6th entry to <see cref="TexButton.SpeedButtonTextures"/> so the Hyperspeed tier
    /// (<see cref="MpTimeSpeed.Hyperspeed"/> == index 5) has an icon and, crucially, so that any code that
    /// indexes the array by the current speed can never go out of range for the new value. Vanilla ships
    /// exactly 5 icons (indices 0..4); we append ours at index 5.
    ///
    /// The icon is loaded from this mod's Textures folder; if it can't be found we fall back to the vanilla
    /// Ultrafast icon so the button still renders.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class HyperspeedTextures
    {
        public const string HyperspeedTexPath = "UI/TimeControls/TimeSpeedButton_Hyperspeed";

        static HyperspeedTextures()
        {
            try
            {
                // Touching the field forces TexButton's own static constructor to run first, so the vanilla
                // array is already populated before we read it.
                Texture2D[] existing = TexButton.SpeedButtonTextures;
                if (existing == null)
                    return;

                // Already extended (e.g. static ctor somehow ran twice) -> nothing to do.
                if (existing.Length > (int)MpTimeSpeed.Hyperspeed)
                    return;

                Texture2D hyperTex = ContentFinder<Texture2D>.Get(HyperspeedTexPath, false)
                                     ?? existing[(int)TimeSpeed.Ultrafast];

                var extended = new Texture2D[(int)MpTimeSpeed.Hyperspeed + 1]; // 6 entries: 0..5
                Array.Copy(existing, extended, existing.Length);
                extended[(int)MpTimeSpeed.Hyperspeed] = hyperTex;

                // TexButton.SpeedButtonTextures is a static readonly field; set it via reflection.
                AccessTools.Field(typeof(TexButton), nameof(TexButton.SpeedButtonTextures))
                    .SetValue(null, extended);

                Log.Message("[Multiplayer] Registered Hyperspeed time-speed tier " +
                            $"({MpTimeSpeed.HyperspeedMultiplier}x).");
            }
            catch (Exception e)
            {
                Log.Error("[Multiplayer] Failed to register Hyperspeed button texture: " + e);
            }
        }
    }
}
