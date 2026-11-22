namespace Multiplayer.Common;

public enum TimeVote : byte
{
    Paused,
    Normal,
    Fast,
    Superfast,
    Ultrafast,

    PlayerResetTickable,
    PlayerResetGlobal,
    ResetTickable,
    ResetGlobal,

    // Replays store votes as bytes, so existing values must keep their positions.
    Hyperspeed
}
