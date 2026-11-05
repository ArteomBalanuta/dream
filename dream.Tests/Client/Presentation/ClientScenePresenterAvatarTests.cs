using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Client.Presentation;

// What the presenter tells the avatar renderer about a remote player: its held items, swings it
// can infer, hits, falls, speed and death.
public class ClientScenePresenterAvatarTests
{
    private const string Local = "local-player";
    private const string Remote = "remote-1";

    private static ActorSnapshot LocalActor() =>
        new(1, CreatureKind.Player, Local, Vector3.Zero, -Vector3.UnitZ, 100, 100, true);

    private static ActorSnapshot RemoteActor(
        Vector3? position = null,
        Vector3? facing = null,
        int health = 100,
        bool alive = true,
        PlayerInteractionMode? mode = null,
        string? visualKey = null) =>
        new(
            2,
            CreatureKind.Player,
            Remote,
            position ?? new Vector3(10f, 1.5f, 10f),
            facing ?? -Vector3.UnitZ,
            health,
            100,
            alive,
            mode,
            null,
            "Remote One",
            VisualKey: visualKey);

    private static ActorSnapshot Wolf(Vector3 position) =>
        new(50, CreatureKind.Enemy, null, position, Vector3.UnitZ, 20, 30, true);

    private static ServerSnapshotEnvelope Snapshot(
        ulong tick,
        ActorSnapshot remote,
        ActorSnapshot[]? extra = null,
        DamageEventDelta[]? damage = null)
    {
        List<ActorSnapshot> actors = [LocalActor(), remote];
        if (extra != null)
        {
            actors.AddRange(extra);
        }

        return new ServerSnapshotEnvelope(
            tick,
            new Dictionary<string, ulong>(),
            actors,
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            damage ?? Array.Empty<DamageEventDelta>());
    }

    private static DamageEventDelta WolfHit() => new(50, CreatureKind.Enemy, 5, false, 15, 30);

    private static RemotePlayerPresentationState Remote0(ClientScenePresenter presenter, double tick) =>
        Assert.Single(presenter.BuildRemotePlayerStates(tick));

    [Fact]
    public void VisualKey_FillsTheHeldItems_AndKeepsThemWhenADeltaOmitsIt()
    {
        ClientScenePresenter presenter = new(Local);

        presenter.ApplySnapshot(Snapshot(10, RemoteActor(visualKey: "held.weapon.sword|held.shield.kite")));
        RemotePlayerPresentationState state = Remote0(presenter, 10);
        Assert.Equal("held.weapon.sword", state.Equipment.MainHand);
        Assert.Equal("held.shield.kite", state.Equipment.OffHand);

        presenter.ApplySnapshot(Snapshot(12, RemoteActor(visualKey: null)));
        state = Remote0(presenter, 12);
        Assert.Equal("held.weapon.sword", state.Equipment.MainHand);
    }

    [Fact]
    public void VisualKey_ChangesAndClears()
    {
        ClientScenePresenter presenter = new(Local);
        presenter.ApplySnapshot(Snapshot(10, RemoteActor(visualKey: "held.weapon.axe|")));
        presenter.ApplySnapshot(Snapshot(12, RemoteActor(visualKey: "|held.shield.tower")));

        RemotePlayerPresentationState state = Remote0(presenter, 12);

        Assert.Null(state.Equipment.MainHand);
        Assert.Equal("held.shield.tower", state.Equipment.OffHand);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("a|b|c")]
    [InlineData("HELD.WEAPON.SWORD|<script>")]
    public void MalformedVisualKey_ShowsNothingAndDoesNotThrow(string key)
    {
        ClientScenePresenter presenter = new(Local);

        presenter.ApplySnapshot(Snapshot(10, RemoteActor(visualKey: key)));

        RemotePlayerPresentationState state = Remote0(presenter, 10);
        Assert.Null(state.Equipment.MainHand);
        Assert.Null(state.Equipment.OffHand);
    }

    [Fact]
    public void HealthLoss_CountsAHit_ButTheFirstSnapshotAndRegenDoNot()
    {
        ClientScenePresenter presenter = new(Local);
        presenter.ApplySnapshot(Snapshot(10, RemoteActor(health: 60)));
        Assert.Equal(0, Remote0(presenter, 10).HitSerial);

        presenter.ApplySnapshot(Snapshot(12, RemoteActor(health: 80)));
        Assert.Equal(0, Remote0(presenter, 12).HitSerial);

        presenter.ApplySnapshot(Snapshot(14, RemoteActor(health: 70)));
        Assert.Equal(1, Remote0(presenter, 14).HitSerial);
    }

    [Fact]
    public void ACombatPlayerFacingAStruckEnemyInReach_IsSwinging()
    {
        ClientScenePresenter presenter = new(Local);
        Vector3 player = new(10f, 1.5f, 10f);
        ActorSnapshot wolf = Wolf(new Vector3(10f, 1.5f, 8.5f));
        presenter.ApplySnapshot(Snapshot(10, RemoteActor(player, mode: PlayerInteractionMode.Combat), [wolf]));

        presenter.ApplySnapshot(Snapshot(
            12,
            RemoteActor(player, mode: PlayerInteractionMode.Combat),
            [wolf],
            [WolfHit()]));

        Assert.Equal(1, Remote0(presenter, 12).AttackSerial);
    }

    [Theory]
    [InlineData(10f, 1.5f, 4f, PlayerInteractionMode.Combat)] // out of reach
    [InlineData(10f, 1.5f, 11.5f, PlayerInteractionMode.Combat)] // behind the player
    [InlineData(10f, 1.5f, 8.5f, PlayerInteractionMode.Building)] // not fighting
    public void NoSwingIsInferred_WhenTheStruckEnemyIsFarBehindOrThePlayerIsBuilding(float x, float y, float z, PlayerInteractionMode mode)
    {
        ClientScenePresenter presenter = new(Local);
        Vector3 player = new(10f, 1.5f, 10f);
        ActorSnapshot wolf = Wolf(new Vector3(x, y, z));
        presenter.ApplySnapshot(Snapshot(10, RemoteActor(player, mode: mode), [wolf]));

        presenter.ApplySnapshot(Snapshot(12, RemoteActor(player, mode: mode), [wolf], [WolfHit()]));

        Assert.Equal(0, Remote0(presenter, 12).AttackSerial);
    }

    [Fact]
    public void NoSwingIsInferred_WithoutDamageThatSnapshot()
    {
        ClientScenePresenter presenter = new(Local);
        ActorSnapshot wolf = Wolf(new Vector3(10f, 1.5f, 8.5f));
        presenter.ApplySnapshot(Snapshot(10, RemoteActor(mode: PlayerInteractionMode.Combat), [wolf]));
        presenter.ApplySnapshot(Snapshot(12, RemoteActor(mode: PlayerInteractionMode.Combat), [wolf]));

        Assert.Equal(0, Remote0(presenter, 12).AttackSerial);
    }

    [Fact]
    public void Speed_AndAirborne_FollowTheMovementBetweenSnapshots()
    {
        ClientScenePresenter presenter = new(Local);
        presenter.ApplySnapshot(Snapshot(10, RemoteActor(new Vector3(10f, 1.5f, 10f))));

        // Two ticks (1/32 s) later: 0.1875 m sideways is 6 m/s, flat.
        for (ulong tick = 12; tick <= 40; tick += 2)
        {
            presenter.ApplySnapshot(Snapshot(tick, RemoteActor(new Vector3(10f + ((tick - 10) / 2 * 0.1875f), 1.5f, 10f))));
        }

        RemotePlayerPresentationState walking = Remote0(presenter, 40);
        Assert.True(walking.IsMoving);
        Assert.InRange(walking.HorizontalSpeed, 5.5f, 6.5f);
        Assert.False(walking.Airborne);

        presenter.ApplySnapshot(Snapshot(42, RemoteActor(new Vector3(10f + (15 * 0.1875f), 1.7f, 10f))));
        Assert.True(Remote0(presenter, 42).Airborne);

        presenter.ApplySnapshot(Snapshot(44, RemoteActor(new Vector3(10f + (15 * 0.1875f), 1.7f, 10f))));
        RemotePlayerPresentationState landed = Remote0(presenter, 44);
        Assert.False(landed.Airborne);
    }

    [Fact]
    public void SeenDying_ReportsADeathAge_ThatRestartsAfterRespawn()
    {
        ClientScenePresenter presenter = new(Local);
        presenter.ApplySnapshot(Snapshot(10, RemoteActor()));
        Assert.True(float.IsPositiveInfinity(Remote0(presenter, 10).DeathAgeSeconds));

        presenter.ApplySnapshot(Snapshot(12, RemoteActor(health: 0, alive: false)));
        RemotePlayerPresentationState dying = Remote0(presenter, 12);
        Assert.False(dying.IsAlive);
        Assert.InRange(dying.DeathAgeSeconds, 0f, 2f);

        presenter.ApplySnapshot(Snapshot(400, RemoteActor()));
        Assert.True(float.IsPositiveInfinity(Remote0(presenter, 400).DeathAgeSeconds));
    }

    [Fact]
    public void FirstSeenAlreadyDead_HasNoDeathAnimationToPlay()
    {
        ClientScenePresenter presenter = new(Local);

        presenter.ApplySnapshot(Snapshot(10, RemoteActor(health: 0, alive: false)));

        Assert.True(float.IsPositiveInfinity(Remote0(presenter, 10).DeathAgeSeconds));
    }
}
