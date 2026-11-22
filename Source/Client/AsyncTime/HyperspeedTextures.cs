using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client.AsyncTime;

[StaticConstructorOnStartup]
internal static class HyperspeedTextures
{
    private static Texture2D hyperspeedIcon;

    public static Texture2D ForSpeed(TimeSpeed speed)
    {
        if (speed != MpTimeSpeed.Hyperspeed)
            return TexButton.SpeedButtonTextures[(int)speed];

        // Resolve on first draw, after the game's content is ready.
        return hyperspeedIcon ??= ContentFinder<Texture2D>.Get("UI/TimeControls/TimeSpeedButton_Hyperspeed", false)
                                  ?? TexButton.SpeedButtonTextures[(int)TimeSpeed.Ultrafast];
    }
}
