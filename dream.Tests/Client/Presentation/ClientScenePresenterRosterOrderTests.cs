using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Client.Presentation;

// BuildPlayerRoster used to sort with LINQ on every call. It now caches a list sorted with a
// comparer; the old expression is kept here as the oracle.
public class ClientScenePresenterRosterOrderTests
{
    private static readonly string[] DisplayNames =
    [
        "alpha", "Alpha", "ALPHA", "bravo", "Bravo", "charlie", "Zulu", "zulu", "  ", "émile", "Emile"
    ];

    private static readonly string[] SessionIds =
    [
        "session-a", "session-B", "session-b", "session-c", "Session-c", "s1", "s10", "s2", "x", "y", "z", "w"
    ];

    [Fact]
    public void BuildPlayerRoster_RandomRostersWithTies_MatchesTheOldLinqOrder()
    {
        Random random = new(20261027);
        for (int trial = 0; trial < 300; trial++)
        {
            string localSessionId = SessionIds[random.Next(SessionIds.Length)];
            ClientScenePresenter presenter = new(localSessionId);
            presenter.ApplyJoinAccepted(new JoinAcceptedMessage(
                localSessionId,
                1,
                0,
                InitialWorld: null,
                Roster: CreateRoster(random)));
            AssertMatchesOracle(presenter, localSessionId);

            // Later snapshots rename, add and remove players; the cached order must follow.
            for (int step = 0; step < 4; step++)
            {
                presenter.ApplySnapshot(CreateSnapshot(random, (ulong)(step + 1)));
                AssertMatchesOracle(presenter, localSessionId);
            }
        }
    }

    [Fact]
    public void BuildPlayerRoster_EqualNamesDifferingOnlyInCase_OrderBySessionOrdinal()
    {
        ClientScenePresenter presenter = new("session-z");
        presenter.ApplyJoinAccepted(new JoinAcceptedMessage(
            "session-z",
            1,
            0,
            InitialWorld: null,
            Roster:
            [
                new PlayerRosterEntry("session-b", 2, "alpha"),
                new PlayerRosterEntry("session-a", 3, "ALPHA"),
                new PlayerRosterEntry("session-z", 1, "zed"),
                new PlayerRosterEntry("Session-c", 4, "Alpha")
            ]));

        Assert.Equal(
            ["session-z", "Session-c", "session-a", "session-b"],
            presenter.BuildPlayerRoster().Select(entry => entry.SessionId).ToArray());
    }

    private static void AssertMatchesOracle(ClientScenePresenter presenter, string localSessionId)
    {
        PlayerRosterEntry[] actual = presenter.BuildPlayerRoster().ToArray();
        PlayerRosterEntry[] expected = actual
            .OrderByDescending(entry => string.Equals(entry.SessionId, localSessionId, StringComparison.Ordinal))
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SessionId, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal(actual.Length, actual.Select(entry => entry.SessionId).Distinct(StringComparer.Ordinal).Count());
    }

    private static PlayerRosterEntry[] CreateRoster(Random random)
    {
        int count = random.Next(0, SessionIds.Length + 1);
        return Enumerable.Range(0, count)
            .Select(index => new PlayerRosterEntry(
                SessionIds[random.Next(SessionIds.Length)],
                index + 1,
                DisplayNames[random.Next(DisplayNames.Length)]))
            .ToArray();
    }

    private static ServerSnapshotEnvelope CreateSnapshot(Random random, ulong tick)
    {
        int count = random.Next(0, SessionIds.Length + 1);
        ActorSnapshot[] actors = Enumerable.Range(0, count)
            .Select(index => new ActorSnapshot(
                index + 100,
                CreatureKind.Player,
                SessionIds[random.Next(SessionIds.Length)],
                new Vector3(index, 1.5f, index),
                -Vector3.UnitZ,
                100,
                100,
                true,
                DisplayName: DisplayNames[random.Next(DisplayNames.Length)]))
            .ToArray();
        return new ServerSnapshotEnvelope(
            tick,
            new Dictionary<string, ulong>(),
            actors,
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());
    }
}
