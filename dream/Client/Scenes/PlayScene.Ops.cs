using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;

namespace Dream.Client.Scenes;

// Owner-only progress on the client (M8 workstream C). Other players' experience and stats no longer ride
// in actor snapshots; the server sends the owner's own as ServerPlayerProgress.
public partial class PlayScene
{
    private void ApplyOwnerProgress(ServerPlayerProgress progress)
    {
        ClientCorrectionApplier.ApplyOwnerProgress(_runtime, progress);
    }
}
