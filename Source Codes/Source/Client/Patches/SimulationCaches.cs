using RimWorld;

namespace Multiplayer.Client
{
    /// <summary>
    /// Vanilla caches that are keyed on the rendered frame (RealTime.frameCount) are not safe in
    /// simulation: during catch-up (joining, reconnecting, TickPatch.Simulating) many timer steps
    /// run in a single frame, so a joiner would reuse a cache the host rebuilt in between and
    /// generate different results. Every simulation event (map/world tick or command) starts by
    /// dropping those caches, so the first use inside the event rebuilds from the current,
    /// synchronized state on every client. Calls later in the same event still hit the cache.
    /// </summary>
    public static class SimulationCaches
    {
        /// <summary>
        /// Called on game load and when a join point is created (on every peer, including
        /// standalone peers that don't reload), so all peers continue from empty caches.
        /// </summary>
        public static void ClearAll()
        {
            InvalidateFrameKeyed();
            Patches.StatWorkerGetValuePatch.ClearAll();
        }

        public static void InvalidateFrameKeyed()
        {
            // Used by quest generation (QuestNode_Root_Beggars, QuestNode_TradeRequest_GetRequestedThing).
            PlayerItemAccessibilityUtility.cachedAccessibleThingsForFrame = -1;
        }
    }
}
