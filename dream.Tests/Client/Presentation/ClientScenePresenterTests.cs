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


public class RemotePlayerInterpolationBufferTests
{
    [Fact]
    public void Sample_InterpolatesBetweenAuthoritativeRemoteSnapshots()
    {
        RemotePlayerInterpolationBuffer buffer = new();
        buffer.AddSample(new RemotePlayerSnapshotSample(10, "remote-1", 1, new Vector3(0f, 0f, 0f), Vector3.UnitZ, 1.0));
        buffer.AddSample(new RemotePlayerSnapshotSample(10, "remote-1", 2, new Vector3(10f, 0f, 0f), Vector3.UnitZ, 2.0));

        Assert.True(buffer.TrySample(1.5, out InterpolatedActorPose pose));
        Assert.Equal(5f, pose.Position.X, 4);
        Assert.Equal(0f, pose.Position.Y, 4);
        Assert.Equal(0f, pose.Position.Z, 4);
    }
}

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

        presenter.ApplySnapshot(snapshot, receivedAtSeconds: 1.0);
        presenter.ApplySnapshot(snapshot, receivedAtSeconds: 2.0);

        RemotePlayerPresentationState remote = Assert.Single(presenter.BuildRemotePlayerStates(renderTimeSeconds: 1.5));
        Assert.Equal("session-a", remote.SessionId);
        Assert.Equal("Host Player", remote.DisplayName);
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
                Array.Empty<DamageEventDelta>()),
            receivedAtSeconds: 1.0);
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
                Array.Empty<DamageEventDelta>()),
            receivedAtSeconds: 2.0);

        IReadOnlyList<RemotePlayerPresentationState> states = presenter.BuildRemotePlayerStates(renderTimeSeconds: 1.5);

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
                Array.Empty<DamageEventDelta>()),
            receivedAtSeconds: 2.0);

        PlayerRosterEntry remote = Assert.Single(
            presenter.BuildPlayerRoster(),
            entry => entry.SessionId == "session-b");
        Assert.Equal("Player B", remote.DisplayName);
    }
}
