namespace Multiplayer.Common;

public enum TimeVote : byte
{
    Paused,
    Normal,
    Fast,
    Superfast,
    Ultrafast,
    Hyperspeed, // New 6th tier. Value (5) MUST match MpTimeSpeed.Hyperspeed == (TimeSpeed)5, because the time
                // control UI casts directly between TimeSpeed and TimeVote. The reset markers below shift up.

    PlayerResetTickable,
    PlayerResetGlobal,
    ResetTickable,
    ResetGlobal
}
