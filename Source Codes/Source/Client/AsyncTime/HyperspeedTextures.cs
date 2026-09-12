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
    /// WHY THIS IS NOT A [StaticConstructorOnStartup] ANY MORE
    ///
    /// It used to be, and it read TexButton.SpeedButtonTextures from inside that static constructor with
    /// a comment explaining that touching the field "forces TexButton's own static constructor to run
    /// first, so the vanilla array is already populated before we read it". That is true, and it is the
    /// bug.
    ///
    /// TexButton's static constructor loads roughly sixty textures through ContentFinder. RimWorld runs
    /// [StaticConstructorOnStartup] types in whatever order reflection hands them back, which is not
    /// stable between runs. When this class happened to run early, touching that field dragged
    /// TexButton's initialiser forward with it - ahead of the point RimWorld itself would have triggered
    /// it - and some of those ContentFinder lookups came back null. Those nulls were then copied
    /// faithfully into the extended array and installed over the real one.
    ///
    /// The symptom was a per-session coin flip. In a bad session every frame that drew the time controls
    /// passed a null Texture2D to Widgets.ButtonImage and on to GUI.DrawTexture: about ten thousand
    /// "null texture passed to GUI.DrawTexture" warnings in eleven seconds, which tripped RimWorld's
    /// "Reached max messages limit" and killed logging for the rest of the session, and then - not every
    /// time, but often enough - a hard crash to desktop inside UnityPlayer. Two were captured, both with
    /// the same stack, both in SINGLEPLAYER: GlobalControls.GlobalControlsOnGUI ->
    /// TimeControlPatch.DoTimeControlsGUI -> TimeControls.DoTimeControlsGUI -> Widgets.ButtonImage ->
    /// GUI.DrawTexture. Note that singleplayer never draws the Hyperspeed button at all - ModifyRect
    /// returns early without Multiplayer.Client, so vanilla only iterates speeds 0..4. The nulls being
    /// drawn were VANILLA's own speed icons, collateral from having forced TexButton up too early.
    ///
    /// So registration now happens lazily, on the first frame that actually needs the array, by which
    /// point content is long since loaded and TexButton can initialise on its own schedule. Nothing
    /// forces it any more.
    /// </summary>
    public static class HyperspeedTextures
    {
        public const string HyperspeedTexPath = "UI/TimeControls/TimeSpeedButton_Hyperspeed";

        /// <summary>Vanilla's own icon paths, indexed by TimeSpeed. Ultrafast deliberately reuses
        /// Superfast's icon - that is what vanilla does, not an oversight here.</summary>
        private static readonly string[] VanillaTexPaths =
        {
            "UI/TimeControls/TimeSpeedButton_Pause",
            "UI/TimeControls/TimeSpeedButton_Normal",
            "UI/TimeControls/TimeSpeedButton_Fast",
            "UI/TimeControls/TimeSpeedButton_Superfast",
            "UI/TimeControls/TimeSpeedButton_Superfast"
        };

        private static bool complete;
        private static bool loggedOnce;

        /// <summary>
        /// Makes sure the speed-button array is long enough for Hyperspeed and contains no nulls.
        ///
        /// Safe and cheap to call every frame: once everything has resolved for real it is a single bool
        /// read. Until then it retries, because "not resolved yet" and "never going to resolve" look the
        /// same on any one frame and only one of them is worth giving up on.
        /// </summary>
        public static void EnsureRegistered()
        {
            if (complete) return;

            try
            {
                // Reading the field here is what triggers TexButton's initialiser if it has not run yet -
                // but we are on a draw frame now, not in the startup static-constructor pass, so it runs
                // with content fully loaded.
                Texture2D[] existing = TexButton.SpeedButtonTextures;
                if (existing == null) return; // not ready; try again next frame

                int needed = (int)MpTimeSpeed.Hyperspeed + 1;

                Texture2D[] target = existing;
                if (existing.Length < needed)
                {
                    target = new Texture2D[needed];
                    Array.Copy(existing, target, existing.Length);
                }

                bool allReal = true;

                for (int i = 0; i < target.Length; i++)
                {
                    // Unity-aware emptiness test. Texture2D is a UnityEngine.Object with an overloaded
                    // equality operator, so a destroyed texture is a "fake null" that still compares
                    // non-null by reference - precisely the case the old `?? fallback` could not catch.
                    if (target[i]) continue;

                    string path = i == (int)MpTimeSpeed.Hyperspeed
                        ? HyperspeedTexPath
                        : (i < VanillaTexPaths.Length ? VanillaTexPaths[i] : null);

                    Texture2D tex = path != null ? ContentFinder<Texture2D>.Get(path, false) : null;

                    if (!tex)
                    {
                        // Never leave a null in an array the UI indexes blind. A visible magenta square
                        // is a bug report; a null is an access violation in native code.
                        allReal = false;
                        tex = BaseContent.BadTex;
                    }

                    target[i] = tex;
                }

                if (!ReferenceEquals(target, existing))
                {
                    AccessTools.Field(typeof(TexButton), nameof(TexButton.SpeedButtonTextures))
                        .SetValue(null, target);
                }

                if (allReal)
                {
                    complete = true;
                    if (!loggedOnce)
                    {
                        loggedOnce = true;
                        Log.Message("[Multiplayer] Registered Hyperspeed time-speed tier " +
                                    $"({MpTimeSpeed.HyperspeedMultiplier}x).");
                    }
                }
            }
            catch (Exception e)
            {
                // Stop retrying on a real failure - one line in the log, not one per frame.
                complete = true;
                Log.Error("[Multiplayer] Failed to register Hyperspeed button texture: " + e);
            }
        }
    }
}
