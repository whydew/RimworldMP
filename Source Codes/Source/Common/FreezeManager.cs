using System.Linq;
using Multiplayer.Common.Networking.Packet;
using Verse;

namespace Multiplayer.Common
{
    public class FreezeManager
    {
        private bool frozen;

        public bool Frozen
        {
            get => frozen;
            private set
            {
                frozen = value;
                Server.SendToPlaying(new ServerFreezePacket(frozen, Server.gameTimer));
            }
        }

        public MultiplayerServer Server { get; }

        public FreezeManager(MultiplayerServer server)
        {
            Server = server;
        }

        // How long to keep the game frozen for other players after the host unfroze. Gravship cutscenes
        // take 10+ s plus capture time, and clients with the cutscene on finish after a host with it off,
        // so this must be well above the cutscene length.
        private const int MaxFreezeWaitTime = GenTicks.TicksPerRealSecond * 60; // 60 seconds

        public void Tick()
        {
            var hostPlayer = Server.PlayingPlayers.FirstOrDefault(p => p.IsHost);

            if (hostPlayer != null)
            {
                // Host is present: freeze/unfreeze follows the host's state
                if (!Frozen && hostPlayer.frozen)
                    Frozen = true;

                if (Frozen && !hostPlayer.frozen && (!Server.PlayingPlayers.Any(p => p.frozen) || Server.NetTimer - hostPlayer.unfrozenAt > MaxFreezeWaitTime))
                    Frozen = false;
            }
            else
            {
                // Host is absent: unfreeze if any player is active
                if (Frozen && Server.PlayingPlayers.Any())
                    Frozen = false;
            }
        }
    }
}
