using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence;

namespace Dream.Tests.Simulation.Runtime;

// M0 gaps: the seat a leaving session vacates must come back clean, a new seat with no stored state
// starts at the world spawn, and a detach that resolves no target saves no profile.
public sealed class SeatLifecycleGapTests
{
    private static readonly Vector3 WorldSpawn = new(5f, 1.5f, 5f);
    private static readonly PlayerSimulationTuning Tuning = PlayerSimulationTuning.AuthoritativeDefault;

    [Fact]
    public void DetachRemoteSession_AfterNoclipAndFreeze_NextPrimaryOccupantHasNormalPhysics()
    {
        using WorldSessionRuntime runtime = new(16, WorldSpawn, new SceneEventHub());
        runtime.Enemies.Clear();
        PlayerSessionId first = new("session-a");
        PlayerSessionId next = new("session-b");
        runtime.AttachRemoteSession(first);

        // Freeze, then noclip on top of it (both are edge triggered).
        runtime.Player.Update(SimulationClock.FixedDeltaSeconds, Input(new MovementCommand(Vector3.Zero, FreezePressed: true)), Tuning);
        runtime.Player.Update(
            SimulationClock.FixedDeltaSeconds,
            Input(new MovementCommand(Vector3.Zero, FreezePressed: true, ToggleNoclipPressed: true)),
            Tuning);
        Assert.True(runtime.Player.IsNoclipEnabled);
        Assert.Equal(0f, runtime.Player.PhysicsBody.InvMass);

        runtime.DetachRemoteSession(first);
        runtime.AttachRemoteSession(next);

        Assert.Equal(next, runtime.PrimaryRemoteSessionId);
        Assert.False(runtime.Player.IsNoclipEnabled);
        Assert.True(runtime.Player.PhysicsBody.InvMass > 0f, "the body kept the frozen zero mass");
        Assert.Equal(
            BulletSharp.CollisionFlags.None,
            runtime.Player.PhysicsBody.CollisionFlags & BulletSharp.CollisionFlags.NoContactResponse);
        // Re-adding the unparked body to the world applies the world's own gravity (-9.8), a hair
        // from the motor's -9.81; either is ordinary downward gravity, a frozen/noclip body has 0.
        Assert.InRange(runtime.Player.PhysicsBody.Gravity.Y, -9.9f, -9.7f);

        // And it behaves: dropped from height, the new occupant falls.
        runtime.Player.SetWorldPosition(new Vector3(8f, 6.5f, 8f));
        float startY = runtime.Player.GetWorldPosition().Y;
        for (ulong tick = 1; tick <= 32; tick++)
        {
            runtime.AdvanceAuthoritativeTick(tick, SimulationClock.FixedDeltaSeconds, (PlayerCommandFrame?)null, Tuning);
        }

        Assert.True(runtime.Player.GetWorldPosition().Y < startY - 1f, $"y={runtime.Player.GetWorldPosition().Y}");
    }

    [Fact]
    public void RestorePersistedPlayerState_PrimarySeatWithoutWorldStateRow_SpawnsAtWorldSpawn()
    {
        using PersistenceTestDatabase database = new();
        using WorldSessionRuntime runtime = CreatePersistentRuntime(database);
        PlayerSessionId session = new("session-new");
        runtime.AttachRemoteSession(session, "NoRowPrimary");
        // Where the previous occupant stood.
        runtime.Player.SetWorldPosition(new Vector3(13f, 1.5f, 13f));

        runtime.RestorePersistedPlayerState(session, "NoRowPrimary");

        Vector3 position = runtime.Player.GetWorldPosition();
        Assert.Equal(WorldSpawn.X, position.X, 1);
        Assert.Equal(WorldSpawn.Z, position.Z, 1);
    }

    [Fact]
    public void SecondarySeats_SpawnInLanesBasedOnTheWorldSpawn_NotOnThePrimaryPosition()
    {
        using WorldSessionRuntime runtime = new(16, WorldSpawn, new SceneEventHub());
        runtime.Enemies.Clear();
        PlayerSessionId primary = new("session-a");
        PlayerSessionId second = new("session-b");
        PlayerSessionId third = new("session-c");
        runtime.AttachRemoteSession(primary);
        runtime.Player.SetWorldPosition(new Vector3(13f, 1.5f, 13f));

        runtime.AttachRemoteSession(second);
        runtime.AttachRemoteSession(third);

        Vector3 secondPosition = Secondary(runtime, second).Position;
        Vector3 thirdPosition = Secondary(runtime, third).Position;
        Assert.Equal(WorldSpawn.X + 2f, secondPosition.X, 1);
        Assert.Equal(WorldSpawn.Z + 2f, secondPosition.Z, 1);
        Assert.Equal(WorldSpawn.X + 3f, thirdPosition.X, 1);
        Assert.Equal(WorldSpawn.Z + 2f, thirdPosition.Z, 1);
    }

    [Fact]
    public void DetachRemoteSession_WithUnresolvedTarget_SkipsTheProfileSave()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository repository = new(database.Schema);
        using WorldSessionRuntime runtime = new(
            12,
            WorldSpawn,
            new SceneEventHub(),
            playerProfiles: repository,
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            persistenceWrites: PersistenceWriteQueue.Background());
        PersistenceWriteQueue writes = runtime.PersistenceWrites!;
        PlayerSessionId session = new("session-a");
        runtime.AttachRemoteSession(session, "Detached");
        runtime.RestorePersistedPlayerState(session, "Detached");
        runtime.Player.Combatant.ApplyProgression(3, 10);
        runtime.DetachRemoteSession(session);
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(3, repository.TryGet("Detached")!.Value.Stats.Level);

        // The session is despawned now, so its target no longer resolves. The primary Combatant
        // (level changed below) must not stand in for it.
        runtime.Player.Combatant.ApplyProgression(9, 0);
        runtime.DetachRemoteSession(session);
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));

        Assert.Equal(3, repository.TryGet("Detached")!.Value.Stats.Level);
    }

    private static WorldSessionRuntime CreatePersistentRuntime(PersistenceTestDatabase database)
    {
        return new WorldSessionRuntime(
            16,
            WorldSpawn,
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            persistenceWrites: PersistenceWriteQueue.Background());
    }

    private static PlayerSimulationInput Input(MovementCommand movement) =>
        new(movement, Vector2.Zero, AllowGameplayInput: true);

    private static RemoteSessionPlayerState Secondary(WorldSessionRuntime runtime, PlayerSessionId sessionId)
    {
        System.Reflection.FieldInfo field = typeof(WorldSessionRuntime).GetField(
            "_secondaryRemotePlayers",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var players = (Dictionary<PlayerSessionId, RemoteSessionPlayerState>)field.GetValue(runtime)!;
        return players[sessionId];
    }
}
