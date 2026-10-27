using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Client.Presentation;


public class ClientScenePresenterTests
{
    [Fact]
    public void ApplyJoinAccepted_RosterHydratesClientOwnedRosterCache()
    {
        ClientScenePresenter presenter = new(localSessionId: "session-a");

        presenter.ApplyJoinAccepted(new JoinAcceptedMessage(
            "session-a",
            101,
            0,
            InitialWorld: null,
            Roster:
            [
                new PlayerRosterEntry("session-a", 101, "Player A"),
                new PlayerRosterEntry("session-b", 102, "Player B")
            ]));

        Assert.Collection(
            presenter.BuildPlayerRoster(),
            first =>
            {
                Assert.Equal("session-a", first.SessionId);
                Assert.Equal("Player A", first.DisplayName);
            },
            second =>
            {
                Assert.Equal("session-b", second.SessionId);
                Assert.Equal("Player B", second.DisplayName);
            });
    }

    [Fact]
    public void ApplyJoinAccepted_UsesAcceptedSessionAsLocalPlayerFilter()
    {
        ClientScenePresenter presenter = new(localSessionId: "temporary-client-id");

        presenter.ApplyJoinAccepted(new JoinAcceptedMessage(
            "session-b",
            102,
            0,
            InitialWorld: null,
            Roster:
            [
                new PlayerRosterEntry("session-a", 101, "Host Player"),
                new PlayerRosterEntry("session-b", 102, "Second Player")
            ]));
        ServerSnapshotEnvelope snapshot = new(
            1,
            new Dictionary<string, ulong>(),
            [
                new ActorSnapshot(101, CreatureKind.Player, "session-a", new Vector3(5f, 1.5f, 5f), -Vector3.UnitZ, 100, 100, true, null, null, "Host Player"),
                new ActorSnapshot(102, CreatureKind.Player, "session-b", new Vector3(7f, 1.5f, 7f), -Vector3.UnitZ, 100, 100, true, null, null, "Second Player")
            ],
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        presenter.ApplySnapshot(snapshot);
        presenter.ApplySnapshot(snapshot);

        RemotePlayerPresentationState remote = Assert.Single(presenter.BuildRemotePlayerStates(renderTick: 1.5));
        Assert.Equal("session-a", remote.SessionId);
        Assert.Equal("Host Player", remote.DisplayName);
    }

    [Fact]
    public void ApplyJoinAccepted_InitialActorsHydrateVisibleRemotePlayersForLateJoiner()
    {
        ClientScenePresenter presenter = new(localSessionId: "temporary-client-id");

        presenter.ApplyJoinAccepted(new JoinAcceptedMessage(
            "session-b",
            102,
            12,
            InitialWorld: null,
            Roster:
            [
                new PlayerRosterEntry("session-a", 101, "Host Player"),
                new PlayerRosterEntry("session-b", 102, "Second Player")
            ],
            Inventory: null,
            InitialActors:
            [
                new ActorSnapshot(101, CreatureKind.Player, "session-a", new Vector3(5f, 1.5f, 5f), -Vector3.UnitZ, 100, 100, true, null, null, "Host Player"),
                new ActorSnapshot(102, CreatureKind.Player, "session-b", new Vector3(7f, 1.5f, 7f), -Vector3.UnitZ, 100, 100, true, null, null, "Second Player")
            ]));

        RemotePlayerPresentationState remote = Assert.Single(
            presenter.BuildRemotePlayerStates(renderTick: 12d - ServerTickClock.DefaultInterpolationDelayTicks));
        Assert.Equal("session-a", remote.SessionId);
        Assert.Equal("Host Player", remote.DisplayName);
        Assert.Equal(5f, remote.Pose.Position.X, 3);
    }

    [Fact]
    public void BuildRemotePlayerStates_ExcludesLocalPlayerAndProjectsRemotePlayers()
    {
        ClientScenePresenter presenter = new(localSessionId: "local-player");
        presenter.ApplySnapshot(
            new ServerSnapshotEnvelope(
                1,
                new Dictionary<string, ulong>(),
                [
                    new ActorSnapshot(1, CreatureKind.Player, "local-player", new Vector3(1f, 0f, 1f), -Vector3.UnitZ, 100, 100, true),
                    new ActorSnapshot(2, CreatureKind.Player, "remote-1", new Vector3(2f, 0f, 2f), -Vector3.UnitZ, 90, 100, true, null, null, "Remote One")
                ],
                PlayerInteractionMode.Building,
                null,
                Array.Empty<WorldMutationDelta>(),
                Array.Empty<DamageEventDelta>()));
        presenter.ApplySnapshot(
            new ServerSnapshotEnvelope(
                2,
                new Dictionary<string, ulong>(),
                [
                    new ActorSnapshot(1, CreatureKind.Player, "local-player", new Vector3(1f, 0f, 1f), -Vector3.UnitZ, 100, 100, true),
                    new ActorSnapshot(2, CreatureKind.Player, "remote-1", new Vector3(4f, 0f, 2f), -Vector3.UnitZ, 90, 100, true, null, null, "Remote One")
                ],
                PlayerInteractionMode.Building,
                null,
                Array.Empty<WorldMutationDelta>(),
                Array.Empty<DamageEventDelta>()));

        IReadOnlyList<RemotePlayerPresentationState> states = presenter.BuildRemotePlayerStates(renderTick: 1.5);

        RemotePlayerPresentationState remote = Assert.Single(states);
        Assert.Equal(2, remote.ActorId);
        Assert.Equal("remote-1", remote.SessionId);
        Assert.Equal("Remote One", remote.DisplayName);
        Assert.Equal(3f, remote.Pose.Position.X, 4);
        Assert.Equal(90, remote.CurrentHealth);
        Assert.True(remote.IsAlive);
        Assert.True(remote.IsMoving);
        Assert.Equal(PlayerModelAssetDefinition.WalkClip, PlayerRenderState.CreateRemote(remote).ClipName);
    }

    [Fact]
    public void ApplySnapshot_RefreshesRosterDisplayNameForObservedPlayer()
    {
        ClientScenePresenter presenter = new(localSessionId: "session-a");
        presenter.ApplyJoinAccepted(new JoinAcceptedMessage(
            "session-a",
            101,
            0,
            InitialWorld: null,
            Roster:
            [
                new PlayerRosterEntry("session-a", 101, "Player A"),
                new PlayerRosterEntry("session-b", 102, "Old Name")
            ]));

        presenter.ApplySnapshot(
            new ServerSnapshotEnvelope(
                2,
                new Dictionary<string, ulong>(),
                [
                    new ActorSnapshot(101, CreatureKind.Player, "session-a", new Vector3(1f, 0f, 1f), -Vector3.UnitZ, 100, 100, true, null, null, "Player A"),
                    new ActorSnapshot(102, CreatureKind.Player, "session-b", new Vector3(4f, 0f, 2f), -Vector3.UnitZ, 90, 100, true, null, null, "Player B")
                ],
                PlayerInteractionMode.Building,
                null,
                Array.Empty<WorldMutationDelta>(),
                Array.Empty<DamageEventDelta>()));

        PlayerRosterEntry remote = Assert.Single(
            presenter.BuildPlayerRoster(),
            entry => entry.SessionId == "session-b");
        Assert.Equal("Player B", remote.DisplayName);
    }

    [Fact]
    public void ApplySnapshot_SameFrameBurst_InterpolatesByServerTick()
    {
        ClientScenePresenter presenter = new(localSessionId: "local-player");
        presenter.ApplySnapshot(CreateRemoteSnapshot(20, new Vector3(0f, 0f, 0f)));
        presenter.ApplySnapshot(CreateRemoteSnapshot(21, new Vector3(2f, 0f, 0f)));
        presenter.ApplySnapshot(CreateRemoteSnapshot(22, new Vector3(4f, 0f, 0f)));

        RemotePlayerPresentationState remote = Assert.Single(presenter.BuildRemotePlayerStates(renderTick: 20.5));
        Assert.Equal(1f, remote.Pose.Position.X, 4);
        Assert.Equal(1, presenter.CaptureDebugState(renderTick: 20.5).SampledRemoteCount);
    }

    [Fact]
    public void ApplyJoinAccepted_StartsNewTickTimelineForRemotePlayers()
    {
        ClientScenePresenter presenter = new(localSessionId: "local-player");
        presenter.ApplySnapshot(CreateRemoteSnapshot(5000, new Vector3(9f, 0f, 0f)));

        presenter.ApplyJoinAccepted(new JoinAcceptedMessage(
            "local-player",
            1,
            3,
            InitialWorld: null,
            Roster: [new PlayerRosterEntry("remote-1", 2, "Remote One")]));
        presenter.ApplySnapshot(CreateRemoteSnapshot(4, new Vector3(1f, 0f, 0f)));

        RemotePlayerPresentationState remote = Assert.Single(presenter.BuildRemotePlayerStates(renderTick: 4));
        Assert.Equal(1f, remote.Pose.Position.X, 4);
    }

    private static ServerSnapshotEnvelope CreateRemoteSnapshot(ulong serverTick, Vector3 remotePosition)
    {
        return new ServerSnapshotEnvelope(
            serverTick,
            new Dictionary<string, ulong>(),
            [
                new ActorSnapshot(1, CreatureKind.Player, "local-player", new Vector3(1f, 0f, 1f), -Vector3.UnitZ, 100, 100, true),
                new ActorSnapshot(2, CreatureKind.Player, "remote-1", remotePosition, -Vector3.UnitZ, 100, 100, true, null, null, "Remote One")
            ],
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());
    }
}
