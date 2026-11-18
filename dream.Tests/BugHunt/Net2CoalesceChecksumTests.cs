using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-NET2-2. When a lagging connection's merged backlog passes the delta caps, ServerConnection
// drops the merged world mutations and damage events and arranges a full world resync instead.
// The periodic world checksum covers exactly those dropped mutations, so it has to go with them:
// a client that still receives it acknowledges mutation sequences it never applied, which both
// advances its applied watermark past the gap and makes IsWorldOutOfSync fire on the same
// snapshot, so it asks for a second full world on top of the one already being arranged.
//
// The unmerged path (ServerConnection.WithoutDeltas) has always dropped the checksum; these tests
// pin that the merged path agrees with it.
public class Net2CoalesceChecksumTests
{
    private static ServerSnapshotEnvelope Snapshot(
        ulong tick,
        WorldMutationDelta[] mutations,
        ulong? checksum = null,
        ulong checksumSequence = 0)
    {
        return new ServerSnapshotEnvelope(
            tick,
            new Dictionary<string, ulong> { ["session-a"] = tick },
            [],
            PlayerInteractionMode.Combat,
            null,
            mutations,
            [],
            checksum,
            checksumSequence);
    }

    private static WorldMutationDelta Mutation(ulong sequence)
    {
        return new WorldMutationDelta(WorldMutationKind.CubePlaced, default, Sequence: sequence);
    }

    [Fact]
    public void DropDeltas_AlsoDropsTheWorldChecksumThatCoveredThem()
    {
        ServerSnapshotCoalesceBuffer buffer = new();
        buffer.Begin(Snapshot(10, [Mutation(1), Mutation(2)]));
        buffer.Append(Snapshot(11, [Mutation(3)], checksum: 0xABCDEF, checksumSequence: 3));

        buffer.DropDeltas();
        ServerSnapshotEnvelope merged = buffer.ToEnvelope();

        Assert.Empty(merged.WorldMutations);
        Assert.Empty(merged.DamageEvents);
        // Without the fix the merged snapshot still claims the world matches checksum 0xABCDEF at
        // mutation sequence 3, while the three mutations that got it there were just discarded.
        Assert.Null(merged.WorldChecksum);
        Assert.Equal(0UL, merged.WorldChecksumSequence);
        // Everything the drop is supposed to keep is still there.
        Assert.Equal(11UL, merged.ServerTick);
        Assert.Equal(11UL, merged.LastAcceptedCommandSequences["session-a"]);
    }

    [Fact]
    public void MergedDrop_AgreesWithTheUnmergedWithoutDeltasPath()
    {
        ServerSnapshotEnvelope newest = Snapshot(11, [Mutation(3)], checksum: 0xABCDEF, checksumSequence: 3);
        ServerSnapshotCoalesceBuffer buffer = new();
        buffer.Begin(Snapshot(10, [Mutation(1), Mutation(2)]));
        buffer.Append(newest);
        buffer.DropDeltas();

        ServerSnapshotEnvelope merged = buffer.ToEnvelope();
        ServerSnapshotEnvelope unmerged = ServerConnection.WithoutDeltas(new ServerSnapshotMessage(newest)).Snapshot;

        Assert.Equal(unmerged.WorldChecksum, merged.WorldChecksum);
        Assert.Equal(unmerged.WorldChecksumSequence, merged.WorldChecksumSequence);
        Assert.Equal(unmerged.WorldMutations.Count, merged.WorldMutations.Count);
        Assert.Equal(unmerged.DamageEvents.Count, merged.DamageEvents.Count);
        // A dropped merge never claims deltas the connection has to be resynced for.
        Assert.False(ServerConnection.HasDeltas(merged));
    }

    // A merge that does not overflow keeps its checksum: the drop is the only thing that clears it.
    [Fact]
    public void AMergeWithoutADrop_KeepsItsWorldChecksum()
    {
        ServerSnapshotCoalesceBuffer buffer = new();
        buffer.Begin(Snapshot(10, [Mutation(1)]));
        buffer.Append(Snapshot(11, [Mutation(2)], checksum: 0xABCDEF, checksumSequence: 2));

        ServerSnapshotEnvelope merged = buffer.ToEnvelope();

        Assert.Equal(2, merged.WorldMutations.Count);
        Assert.Equal(0xABCDEFUL, merged.WorldChecksum);
        Assert.Equal(2UL, merged.WorldChecksumSequence);
    }
}
