using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Presentation;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Presentation;

// The presenter runs every render frame (remote states, roster) and on every snapshot. Before
// MEM-7, with three remote players and two enemies, it allocated (bytes per call, Debug, with
// diagnostics off): BuildRemotePlayerStates 280, BuildPlayerRoster 1032, ApplySnapshot 1152.
public class ClientScenePresenterAllocationTests
{
    private const string LocalSessionId = "local-session";

    private readonly ITestOutputHelper _output;

    public ClientScenePresenterAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void BuildRemotePlayerStates_SecondCall_AllocatesNothing()
    {
        ClientScenePresenter presenter = CreatePresenterWithRemotePlayers(out _);
        presenter.BuildRemotePlayerStates(renderTick: 2.5);

        long bytes = AllocationAssert.AtMost(0, () => presenter.BuildRemotePlayerStates(renderTick: 2.5));

        _output.WriteLine($"BuildRemotePlayerStates: {bytes} B per call");
        Assert.Equal(3, presenter.BuildRemotePlayerStates(renderTick: 2.5).Count);
    }

    [Fact]
    public void BuildPlayerRoster_SecondCall_AllocatesNothing()
    {
        ClientScenePresenter presenter = CreatePresenterWithRemotePlayers(out _);
        presenter.BuildPlayerRoster();

        long bytes = AllocationAssert.AtMost(0, () => presenter.BuildPlayerRoster());

        _output.WriteLine($"BuildPlayerRoster: {bytes} B per call");
        Assert.Equal(4, presenter.BuildPlayerRoster().Count);
    }

    [Fact]
    public void ApplySnapshot_SteadyStateWithDiagnosticsOff_AllocatesNothing()
    {
        ClientScenePresenter presenter = CreatePresenterWithRemotePlayers(out ServerSnapshotEnvelope snapshot);
        ulong tick = 10;

        long bytes = AllocationAssert.AtMost(
            0,
            () => presenter.ApplySnapshot(snapshot with { ServerTick = tick++ }));

        _output.WriteLine($"ApplySnapshot: {bytes} B per call");
    }

    [Fact]
    public void ApplySnapshot_UnchangedRoster_KeepsRosterContents()
    {
        ClientScenePresenter presenter = CreatePresenterWithRemotePlayers(out ServerSnapshotEnvelope snapshot);
        PlayerRosterEntry[] before = presenter.BuildPlayerRoster().ToArray();

        presenter.ApplySnapshot(snapshot with { ServerTick = 50 });

        Assert.Equal(before, presenter.BuildPlayerRoster().ToArray());
    }

    private static ClientScenePresenter CreatePresenterWithRemotePlayers(out ServerSnapshotEnvelope snapshot)
    {
        ClientScenePresenter presenter = new(LocalSessionId);
        snapshot = new ServerSnapshotEnvelope(
            1,
            new Dictionary<string, ulong>(),
            [
                Player(1, LocalSessionId, "Local", new Vector3(1f, 1.5f, 1f)),
                Player(2, "remote-b", "bravo", new Vector3(2f, 1.5f, 2f)),
                Player(3, "remote-a", "Alpha", new Vector3(3f, 1.5f, 3f)),
                Player(4, "remote-c", "Charlie", new Vector3(4f, 1.5f, 4f)),
                new ActorSnapshot(2000, CreatureKind.Enemy, null, new Vector3(6f, 0f, 6f), Vector3.UnitX, 50, 50, true, DisplayName: "Wolf"),
                new ActorSnapshot(2001, CreatureKind.Enemy, null, new Vector3(7f, 0f, 6f), Vector3.UnitX, 50, 50, true, DisplayName: "Wolf")
            ],
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        for (ulong tick = 1; tick <= 4; tick++)
        {
            presenter.ApplySnapshot(snapshot with { ServerTick = tick });
        }

        return presenter;
    }

    private static ActorSnapshot Player(int actorId, string sessionId, string displayName, Vector3 position)
    {
        return new ActorSnapshot(
            actorId,
            CreatureKind.Player,
            sessionId,
            position,
            -Vector3.UnitZ,
            100,
            100,
            true,
            DisplayName: displayName);
    }
}
