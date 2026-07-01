using Dream.Multiplayer.Protocol;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;

public interface IAuthoritativeSessionHost
{
    JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName);

    AuthoritativeActionResult ProcessAction(PlayerActionMessage message);

    ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false);
}
