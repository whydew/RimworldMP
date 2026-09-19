using System.Collections.Generic;

namespace Multiplayer.Common.Networking.Packet;

[PacketDefinition(Packets.Client_SyncInfo, allowFragmented: true)]
public record struct ClientSyncInfoPacket : IPacket
{
    // 8MiB. For some big saves, the sync opinion can reach ~2.5MiB, so this includes some leeway.
    public const int MaxLength = 1 << 23;
    public byte[] rawSyncOpinion;

    public SyncOpinion SyncOpinion
    {
        get => SyncOpinionBinder.Deserialize(rawSyncOpinion);
        set => rawSyncOpinion = SyncOpinionBinder.Serialize(value);
    }

    public void Bind(PacketBuffer buf)
    {
        buf.BindRemaining(ref rawSyncOpinion, maxLength: MaxLength);
    }

    private static readonly Binder<SyncOpinion> SyncOpinionBinder = BinderOf.Identity<SyncOpinion>();
}

[PacketDefinition(Packets.Server_SyncInfo, allowFragmented: true)]
public record struct ServerSyncInfoPacket : IPacket
{
    public byte[] rawSyncOpinion;
    public SyncOpinion SyncOpinion
    {
        get => SyncOpinionBinder.Deserialize(rawSyncOpinion);
        set => rawSyncOpinion = SyncOpinionBinder.Serialize(value);
    }

    public void Bind(PacketBuffer buf)
    {
        buf.BindRemaining(ref rawSyncOpinion, maxLength: ClientSyncInfoPacket.MaxLength);
    }

    private static readonly Binder<SyncOpinion> SyncOpinionBinder = BinderOf.Identity<SyncOpinion>();
}

public record struct SyncOpinion : IPacketBufferable
{
    public int startTick;
    public List<uint> commandRandomStates;
    public List<uint> worldRandomStates;
    public List<MapRandomState> mapRandomStates;
    // Trace hashes rolled up per timer step, as pairs: [traceCount0, hash0, traceCount1, hash1, ...].
    // One pair per step that produced traces (at most 30 per opinion), instead of one int per traced
    // RNG call. The full per-call list stays on each client and is only exchanged after a desync.
    public List<int> traceStepHashes;
    public bool simulating;
    public RoundModeEnum roundMode;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref startTick);

        buf.Bind(ref commandRandomStates, BinderOf.UInt());
        buf.Bind(ref worldRandomStates, BinderOf.UInt());
        buf.Bind(ref mapRandomStates, BinderOf.Identity<MapRandomState>());
        // Two ints per timer step with traces. Opinions normally cover 30 steps; the limit leaves room for
        // longer opinions without allowing unbounded allocations.
        buf.Bind(ref traceStepHashes, BinderOf.Int(), maxLength: 1<<16);

        buf.Bind(ref simulating);
        buf.BindEnum(ref roundMode);
    }
}

public record struct MapRandomState : IPacketBufferable
{
    public int mapId;
    public List<uint> randomStates;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref mapId);
        buf.Bind(ref randomStates, BinderOf.UInt());
    }
}
