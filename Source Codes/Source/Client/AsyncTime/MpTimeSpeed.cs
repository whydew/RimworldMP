using Verse;

namespace Multiplayer.Client
{
    /// <summary>
    /// Defines a 6th time-speed tier above vanilla Ultrafast, baked directly into Multiplayer so players
    /// don't need Smart Speed (or any speed mod) for faster-than-vanilla multiplayer.
    ///
    /// RimWorld's <see cref="Verse.TimeSpeed"/> is a vanilla enum (Paused=0, Normal=1, Fast=2, Superfast=3,
    /// Ultrafast=4) that we can't extend, so the new tier reuses the next integer value (5) and is handled
    /// explicitly in every place that maps a speed to a tick rate or draws the speed UI. Because the value is
    /// a single compile-time constant shared by all clients, it stays fully deterministic — every player runs
    /// the same multiplier — as long as everyone uses this same Multiplayer build.
    /// </summary>
    public static class MpTimeSpeed
    {
        /// <summary>The new speed tier, one step above <see cref="TimeSpeed.Ultrafast"/>.</summary>
        public const TimeSpeed Hyperspeed = (TimeSpeed)5;

        /// <summary>
        /// Tick-rate multiplier for the Hyperspeed tier. For reference, Multiplayer's other speeds are
        /// Normal=1x, Fast=3x, Superfast=6x, Ultrafast=15x. 25x is clearly faster than Ultrafast while
        /// staying bounded. Change this one constant to retune the tier.
        /// </summary>
        public const float HyperspeedMultiplier = 25f;

        /// <summary>Highest selectable speed value (used as the upper bound for UI/hotkey stepping).</summary>
        public const TimeSpeed Highest = Hyperspeed;

        // Vanilla code switches on TickManager.CurTimeSpeed and throws or misbehaves on values it
        // doesn't know (e.g. Pawn_CallTracker.IdleCallVolumeFactor throws NotImplementedException,
        // SubSoundDef.gameSpeedRange silences sounds). So (TimeSpeed)5 must never be written into
        // the vanilla TickManager. Hyperspeed is stored there as Ultrafast, and this flag remembers
        // that the Ultrafast currently in the TickManager is really Hyperspeed.
        private static bool vanillaHoldsHyperspeed;

        public static bool VanillaHoldsHyperspeed => vanillaHoldsHyperspeed;

        /// <summary>The value that is safe to store in vanilla's TickManager.</summary>
        public static TimeSpeed ToVanilla(TimeSpeed speed) =>
            speed > TimeSpeed.Ultrafast ? TimeSpeed.Ultrafast : speed;

        /// <summary>
        /// Sets the vanilla TickManager's speed, clamping Hyperspeed to Ultrafast. Writes the field directly:
        /// the CurTimeSpeed setter refuses (and posts a message) while the player can't control time, but
        /// this only switches the time context to the ticking map/world.
        /// </summary>
        public static void SetOn(TickManager tickManager, TimeSpeed speed)
        {
            tickManager.curTimeSpeed = ToVanilla(speed);
            vanillaHoldsHyperspeed = speed > TimeSpeed.Ultrafast;
        }

        /// <summary>Raw restore used by TimeSnapshot (bypasses the PlayerCanControl check, like the original).</summary>
        internal static void Restore(TickManager tickManager, TimeSpeed vanillaSpeed, bool holdsHyperspeed)
        {
            tickManager.curTimeSpeed = ToVanilla(vanillaSpeed);
            vanillaHoldsHyperspeed = holdsHyperspeed;
        }

        /// <summary>The MP speed currently represented by the vanilla TickManager (Hyperspeed-aware).</summary>
        public static TimeSpeed GetFrom(TickManager tickManager)
        {
            var speed = tickManager.curTimeSpeed;
            return vanillaHoldsHyperspeed && speed == TimeSpeed.Ultrafast ? Hyperspeed : speed;
        }
    }
}
