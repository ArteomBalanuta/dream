using Dream.Multiplayer.Protocol;
using Dream.Simulation.World;

namespace Dream.Multiplayer.Diagnostics;

public interface IMultiplayerDiagnosticsSink
{
    void RecordSnapshotApplied(ServerSnapshotEnvelope snapshot, string sessionId);

    void RecordActionResult(AuthoritativeActionResult result, bool correctionApplied);
}
