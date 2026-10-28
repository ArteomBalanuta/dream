using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Server.Persistence;

// A synchronous join whose read timed out leaves its session with default stats, which must not be
// saved over the player's row. That mark used to be keyed by display name and never removed, so a
// healthy session sharing the name skipped its own save on release.
public sealed class UnrestoredPlayerSessionTests
{
    private const string PlayerName = "Alpha";

    [Fact]
    public void FailedJoin_ForAName_DoesNotStopAHealthySessionWithThatNameFromSaving_AndIsForgottenOnRelease()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository repository = new(database.Schema);
        using WorldSessionRuntime runtime = new(
            12,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: repository,
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            persistenceWrites: PersistenceWriteQueue.Background());
        PersistenceWriteQueue writes = runtime.PersistenceWrites!;
        PlayerSessionId healthy = new("session-healthy");
        PlayerSessionId failed = new("session-failed");

        // The healthy session joins (it takes the primary seat) and levels up.
        runtime.AttachRemoteSession(healthy, PlayerName);
        runtime.RestorePersistedPlayerState(healthy, PlayerName);
        Assert.Equal(healthy, runtime.PrimaryRemoteSessionId);
        runtime.Player.Combatant.ApplyProgression(4, 25);

        // Another session with the same name joins while a write is stuck: its read times out.
        using ManualResetEventSlim gate = new(initialState: false);
        writes.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));
        try
        {
            runtime.AttachRemoteSession(failed, PlayerName);
            Assert.Throws<InvalidOperationException>(() => runtime.RestorePersistedPlayerState(failed, PlayerName));
        }
        finally
        {
            gate.Set();
        }

        Assert.Equal(1, runtime.UnrestoredPlayerSessionCount);

        // The healthy session's release still saves its progress.
        runtime.DetachRemoteSession(healthy);
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(4, repository.TryGet(PlayerName)!.Value.Stats.Level);

        // The failed session's release saves nothing over it, and forgets the mark.
        runtime.DetachRemoteSession(failed);
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(4, repository.TryGet(PlayerName)!.Value.Stats.Level);
        Assert.Equal(0, runtime.UnrestoredPlayerSessionCount);
    }
}
