using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Verse;

namespace Multiplayer.Client
{

    public class ClientSyncOpinion(int startTick)
    {
        public bool isLocalClientsOpinion;

        public int startTick = startTick;
        public List<uint> commandRandomStates = new();
        public List<uint> worldRandomStates = new();
        public List<MapRandomStateData> mapStates = new();

        // todo Unused for now
        public List<int> pawnCapacityHashes = new();
        public List<int> pawnStatHashes = new();
        public List<int> pawnNeedHashes = new();

        // Serialized only after a desync to reduce bandwidth usage in regular gameplay (only the hashes are used) and
        // help with debugging in case something goes wrong.
        public List<StackTraceLogItem> desyncStackTraces = new();
        // Local only: one hash per trace, in order. Not sent over the network.
        public List<int> desyncStackTraceHashes = new();
        // Networked: rolling hash per timer step, as pairs [traceCount, hash, ...] (see SyncOpinion.traceStepHashes).
        public List<int> traceStepHashes = new();
        private int lastTraceStepTimer = int.MinValue;
        public bool simulating;
        public RoundModeEnum roundMode;

        public string CheckForDesync(ClientSyncOpinion other)
        {
            if (roundMode != other.roundMode)
                return $"FP round mode doesn't match: {roundMode} != {other.roundMode}";

            // Map instances must match by id and order. Every opinion has unique map ids (GetRandomStatesForMap
            // dedupes, and ids are map.uniqueID), so comparing index-aligned is equivalent to the previous
            // Select(mapId).SequenceEqual + join-by-id, but allocates no LINQ iterators/lookups.
            if (mapStates.Count != other.mapStates.Count)
                return "Map instances don't match";
            for (int i = 0; i < mapStates.Count; i++)
                if (mapStates[i].mapId != other.mapStates[i].mapId)
                    return "Map instances don't match";

            // Ids/order verified equal above, so map i here corresponds to map i in the other opinion.
            for (int i = 0; i < mapStates.Count; i++)
                if (!ListsEqual(mapStates[i].randomStates, other.mapStates[i].randomStates))
                    return $"Wrong random state on map {mapStates[i].mapId}";

            if (!ListsEqual(worldRandomStates, other.worldRandomStates))
                return "Wrong random state for the world";

            if (!ListsEqual(commandRandomStates, other.commandRandomStates))
                return "Random state from commands doesn't match";

            if (!simulating && !other.simulating && traceStepHashes.Count > 0 && other.traceStepHashes.Count > 0 && !ListsEqual(traceStepHashes, other.traceStepHashes))
                return "Trace hashes don't match";

            return null;
        }

        // Allocation-free equivalent of Enumerable.SequenceEqual for lists (no boxed enumerators, short-circuits on
        // count). Uses the default equality comparer, matching SequenceEqual's semantics exactly.
        private static bool ListsEqual<T>(List<T> a, List<T> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;

            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < a.Count; i++)
                if (!comparer.Equals(a[i], b[i]))
                    return false;

            return true;
        }

        public void AddTraceHash(int hash)
        {
            desyncStackTraceHashes.Add(hash);

            var timer = TickPatch.Timer;
            if (timer != lastTraceStepTimer || traceStepHashes.Count == 0)
            {
                lastTraceStepTimer = timer;
                traceStepHashes.Add(0);
                traceStepHashes.Add(Gen.HashCombineInt(unchecked((int)0x6d707472), timer));
            }

            var last = traceStepHashes.Count - 1;
            traceStepHashes[last - 1]++;
            traceStepHashes[last] = Gen.HashCombineInt(traceStepHashes[last], hash);
        }

        public int TraceStepCount => traceStepHashes.Count / 2;
        public int TraceStepSize(int step) => traceStepHashes[step * 2];
        public int TraceStepHash(int step) => traceStepHashes[step * 2 + 1];

        /// <summary>Total number of traces recorded (for remote opinions, where the per-trace list isn't sent).</summary>
        public int TraceCount
        {
            get
            {
                if (desyncStackTraceHashes.Count > 0) return desyncStackTraceHashes.Count;
                var total = 0;
                for (int i = 0; i < TraceStepCount; i++) total += TraceStepSize(i);
                return total;
            }
        }

        public List<uint> GetRandomStatesForMap(int mapId)
        {
            // Called once per per-map RNG state append (hot). Manual scan avoids the closure allocation that
            // List.Find(m => m.mapId == mapId) incurs on every call.
            for (int i = 0; i < mapStates.Count; i++)
                if (mapStates[i].mapId == mapId)
                    return mapStates[i].randomStates;

            var result = new MapRandomStateData(mapId);
            mapStates.Add(result);
            return result.randomStates;
        }

        public byte[] Serialize()
        {
            var writer = new ByteWriter();

            writer.WriteInt32(startTick);
            writer.WritePrefixedUInts(commandRandomStates);
            writer.WritePrefixedUInts(worldRandomStates);

            writer.WriteInt32(mapStates.Count);
            foreach (var map in mapStates)
            {
                writer.WriteInt32(map.mapId);
                writer.WritePrefixedUInts(map.randomStates);
            }

            writer.WritePrefixedInts(traceStepHashes);
            writer.WriteBool(simulating);
            writer.WriteShort((short)roundMode);

            return writer.ToArray();
        }

        public static ClientSyncOpinion FromNet(SyncOpinion sync) => new(sync.startTick)
        {
            commandRandomStates = sync.commandRandomStates,
            worldRandomStates = sync.worldRandomStates,
            mapStates = sync.mapRandomStates.Select(state => new MapRandomStateData(state.mapId)
                { randomStates = state.randomStates }).ToList(),
            traceStepHashes = sync.traceStepHashes,
            simulating = sync.simulating,
            roundMode = sync.roundMode
        };

        public SyncOpinion ToNet() => new()
        {
            startTick = startTick,
            commandRandomStates = commandRandomStates,
            worldRandomStates = worldRandomStates,
            mapRandomStates = mapStates.Select(state => new MapRandomState
                { mapId = state.mapId, randomStates = state.randomStates }).ToList(),
            traceStepHashes = traceStepHashes,
            simulating = simulating,
            roundMode = roundMode
        };

        public static ClientSyncOpinion Deserialize(ByteReader data)
        {
            var startTick = data.ReadInt32();

            var cmds = new List<uint>(data.ReadPrefixedUInts());
            var world = new List<uint>(data.ReadPrefixedUInts());

            var maps = new List<MapRandomStateData>();
            int mapCount = data.ReadInt32();
            for (int i = 0; i < mapCount; i++)
            {
                int mapId = data.ReadInt32();
                var mapData = new List<uint>(data.ReadPrefixedUInts());
                maps.Add(new MapRandomStateData(mapId) { randomStates = mapData });
            }

            var traceStepHashes = new List<int>(data.ReadPrefixedInts());
            var simulating = data.ReadBool();
            var roundMode = data.ReadShort();

            return new ClientSyncOpinion(startTick)
            {
                commandRandomStates = cmds,
                worldRandomStates = world,
                mapStates = maps,
                traceStepHashes = traceStepHashes,
                simulating = simulating,
                roundMode = (RoundModeEnum)roundMode
            };
        }

        private int StepSizeStartingAt(int traceIndex)
        {
            if (traceIndex < 0) return 0;
            var index = 0;
            for (int i = 0; i < TraceStepCount; i++)
            {
                if (index == traceIndex) return TraceStepSize(i);
                index += TraceStepSize(i);
                if (index > traceIndex) return 0;
            }
            return 0;
        }

        public void TryMarkSimulating()
        {
            if (TickPatch.Simulating)
                simulating = true;
        }

        private const int MaxTracesInReport = 4000;

        public string GetFormattedStackTracesForRange(int diffAt)
        {
            var radius = Multiplayer.settings.desyncTracesRadius;
            var start = Math.Max(0, diffAt - radius);
            var end = diffAt + radius;

            // Hashes are compared per timer step, so diffAt is the first trace of the first step that
            // differs. The actual divergence can be anywhere in that step: include the whole step.
            var stepSize = StepSizeStartingAt(diffAt);
            if (stepSize > 0)
                end = Math.Max(end, diffAt + stepSize + radius);
            end = Math.Min(end, start + MaxTracesInReport);

            var traceId = start;

            return
                $"Trace count: {desyncStackTraces.Count}\n" +
                (stepSize > 0 ? $"Traces are compared per timer step; the first differing step has {stepSize} traces starting at {diffAt}.\n" : "") +
                $"Trace of first desynced map random state:\n{diffAt} {desyncStackTraces.ElementAtOrDefault(diffAt)}" +
                "\n\nContext traces:\n" +
                desyncStackTraces
                .Skip(start)
                .Take(end - start)
                .Join(a => traceId++ + " " + a, "\n\n");
        }

        public void Clear()
        {
            for (int i = 0; i < desyncStackTraces.Count; i++)
                desyncStackTraces[i].Dispose();
        }
    }
}