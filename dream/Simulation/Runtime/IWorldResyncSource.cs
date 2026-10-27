using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;

// Optional capability of an authoritative host: export a full world snapshot for a client that
// reported a checksum mismatch, without draining the deltas pending for other clients.
public interface IWorldResyncSource
{
    ServerSnapshotEnvelope ExportWorldResync();
}
