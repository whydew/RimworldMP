using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    /// <summary>
    /// Multiplayer-owned speed-button icons, indexed by TimeSpeed: vanilla's five (0..4) plus Hyperspeed at
    /// <see cref="MpTimeSpeed.Hyperspeed"/> (5). Multiplayer's time controls draw from this array; vanilla's
    /// <see cref="TexButton.SpeedButtonTextures"/> is never modified.
    ///
    /// WHY NOT EXTEND VANILLA'S ARRAY
    ///
    /// This used to build a 6-element copy and install it over TexButton.SpeedButtonTextures via reflection.
    /// That field is `static initonly`, and Mono's JIT may embed the value of an initonly static of an
    /// already-initialised class directly into code it compiles. Any method JIT-compiled before the
    /// reflection write - including the MP time-control drawers themselves, which are compiled before the
    /// registration call at their top ever runs - kept indexing the original 5-element array. In a hosted
    /// session that meant IndexOutOfRangeException at index 5 on every frame, thrown between
    /// Widgets.BeginGroup and EndGroup ("pushing more GUIClips than you are popping"). Whether it happened
    /// depended on JIT order, so it came and went between sessions.
    ///
    /// Icons still resolve lazily, on the first draw frame: reading TexButton too early during startup can
    /// pull ContentFinder lookups forward and get nulls back. The attribute below is only there because
    /// RimWorld warns about any static Texture2D field without it; the static constructor it runs just
    /// allocates the empty array and touches no content.
    /// </summary>
    [StaticConstructorOnStartup]
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

        private static readonly Texture2D[] textures = new Texture2D[(int)MpTimeSpeed.Hyperspeed + 1];
        private static bool complete;

        /// <summary>Never null and never contains null; a missing icon is BadTex until it resolves.
        /// Cheap once everything has resolved: a single bool read.</summary>
        public static Texture2D[] Textures
        {
            get
            {
                if (!complete) Resolve();
                return textures;
            }
        }

        private static void Resolve()
        {
            Texture2D[] vanilla = TexButton.SpeedButtonTextures;
            bool allReal = true;

            for (int i = 0; i < textures.Length; i++)
            {
                // Unity-aware test: a destroyed texture is a "fake null" that still compares non-null by reference.
                if (textures[i] && textures[i] != BaseContent.BadTex) continue;

                Texture2D tex;
                if (i == (int)MpTimeSpeed.Hyperspeed)
                    tex = ContentFinder<Texture2D>.Get(HyperspeedTexPath, false);
                else if (vanilla != null && i < vanilla.Length && vanilla[i])
                    tex = vanilla[i];
                else
                    tex = ContentFinder<Texture2D>.Get(VanillaTexPaths[i], false);

                if (!tex)
                {
                    allReal = false;
                    tex = BaseContent.BadTex;
                }

                textures[i] = tex;
            }

            if (allReal)
            {
                complete = true;
                Log.Message($"[Multiplayer] Registered Hyperspeed time-speed tier ({MpTimeSpeed.HyperspeedMultiplier}x).");
            }
        }
    }
}
