using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Multiplayer.Diagnostics;

public class NetworkDiagnosticsLogTests
{
    [Fact]
    public void DescribeActors_IncludesPlayerSessionsNamesAndEnemyCount()
    {
        ActorSnapshot[] actors =
        [
            new(
                7,
                CreatureKind.Player,
                "session-a",
                new Vector3(1f, 2f, 3f),
                -Vector3.UnitZ,
                80,
                100,
                true,
                DisplayName: "Alice"),
            new(
                2000,
                CreatureKind.Enemy,
                null,
                new Vector3(5f, 1f, 6f),
                Vector3.UnitX,
                100,
                100,
                true,
                DisplayName: "Wolf")
        ];

        string description = NetworkDiagnosticsLog.DescribeActors(actors);

        Assert.Contains("players=1", description);
        Assert.Contains("session-a#7/Alice", description);
        Assert.Contains("(1.00,2.00,3.00)", description);
        Assert.Contains("enemies=1", description);
        Assert.Contains("actors=2", description);
    }

    [Fact]
    public void DescribeJoin_IncludesRosterInitialActorsWorldAndInventoryFlags()
    {
        JoinAcceptedMessage accepted = new(
            "session-b",
            9,
            42,
            InitialWorld: null,
            Roster:
            [
                new PlayerRosterEntry("session-a", 7, "Alice"),
                new PlayerRosterEntry("session-b", 9, "Bob")
            ],
            Inventory: null,
            InitialActors:
            [
                new ActorSnapshot(
                    7,
                    CreatureKind.Player,
                    "session-a",
                    Vector3.One,
                    -Vector3.UnitZ,
                    100,
                    100,
                    true,
                    DisplayName: "Alice")
            ]);

        string description = NetworkDiagnosticsLog.DescribeJoin(accepted);

        Assert.Contains("session=session-b", description);
        Assert.Contains("actor=9", description);
        Assert.Contains("roster=[session-a#7/Alice, session-b#9/Bob]", description);
        Assert.Contains("initial=players=1", description);
        Assert.Contains("world=no", description);
        Assert.Contains("inventory=no", description);
    }
}
