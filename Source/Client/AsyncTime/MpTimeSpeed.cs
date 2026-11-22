using Multiplayer.Common;
using Verse;

namespace Multiplayer.Client;

internal static class MpTimeSpeed
{
    public const TimeSpeed Hyperspeed = TimeSpeed.Ultrafast + 1;
    public const float HyperspeedMultiplier = 25f;

    private static TickManager hyperspeedContext;

    public static TimeVote ToVote(TimeSpeed speed) => speed == Hyperspeed ? TimeVote.Hyperspeed : (TimeVote)speed;

    public static TimeSpeed FromVote(TimeVote vote) => vote switch
    {
        TimeVote.Hyperspeed => Hyperspeed,
        >= TimeVote.PlayerResetTickable => TimeSpeed.Paused,
        _ => (TimeSpeed)vote
    };

    public static TimeSpeed ToVanilla(TimeSpeed speed) => speed == Hyperspeed ? TimeSpeed.Ultrafast : speed;

    public static TimeSpeed GetFrom(TickManager tickManager)
    {
        var speed = tickManager.curTimeSpeed;
        return hyperspeedContext == tickManager && speed == TimeSpeed.Ultrafast ? Hyperspeed : speed;
    }

    public static void SetOn(TickManager tickManager, TimeSpeed speed)
    {
        // Vanilla audio and UI switch on curTimeSpeed and expect one of the vanilla values.
        tickManager.curTimeSpeed = ToVanilla(speed);
        hyperspeedContext = speed == Hyperspeed ? tickManager : null;
    }

    public static void ClearContext() => hyperspeedContext = null;
}
