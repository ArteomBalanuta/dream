using System.Globalization;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Simulation.Enemies;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.Actors;

public class ActorWorldLabelAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void HealthLabelCache_MatchesFormatHealthLabel()
    {
        ActorHealthLabelCache cache = new();
        for (int max = -2; max <= 120; max += 7)
        {
            for (int current = -3; current <= max + 3; current++)
            {
                string first = cache.Get(current, max);
                Assert.Equal(ActorWorldLabelBuilder.FormatHealthLabel(current, max), first);
                Assert.Same(first, cache.Get(current, max));
            }
        }
    }

    [Fact]
    public void HealthLabelCache_RebuildsWhenHealthChanges()
    {
        ActorHealthLabelCache cache = new();

        string full = cache.Get(100, 100);
        string hurt = cache.Get(87, 100);
        string raisedMax = cache.Get(87, 120);

        Assert.Equal("100/100", full);
        Assert.Equal("87/100", hurt);
        Assert.Equal("87/120", raisedMax);
        Assert.Same(hurt, cache.Get(87, 100));
    }

    [Fact]
    public void HealthLabelCache_StaysBounded()
    {
        ActorHealthLabelCache cache = new();
        for (int current = 0; current < ActorHealthLabelCache.MaxEntries * 2; current++)
        {
            Assert.Equal(ActorWorldLabelBuilder.FormatHealthLabel(current, 50000), cache.Get(current, 50000));
        }

        Assert.True(cache.Count <= ActorHealthLabelCache.MaxEntries);
    }

    [Fact]
    public void HealthLabelCache_FollowsCultureChanges()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo minusSign = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            minusSign.NumberFormat.NegativeSign = "~";
            ActorHealthLabelCache cache = new();

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal("-1/10", cache.Get(-1, 10));

            CultureInfo.CurrentCulture = minusSign;
            Assert.Equal(ActorWorldLabelBuilder.FormatHealthLabel(-1, 10), cache.Get(-1, 10));
            Assert.Equal("~1/10", cache.Get(-1, 10));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void AppendLabels_MatchCreateLabels()
    {
        RemotePlayerPresentationState[] players = CreateRemotePlayers();
        EnemyActor[] enemies = CreateEnemies();
        ActorHealthLabelCache cache = new();
        List<WorldActorLabelState> labels = [];

        ActorWorldLabelBuilder.AppendRemotePlayerLabels(players, labels, cache);
        ActorWorldLabelBuilder.AppendEnemyLabels(enemies, labels, cache);

        WorldActorLabelState[] expected =
            [.. ActorWorldLabelBuilder.CreateRemotePlayerLabels(players), .. ActorWorldLabelBuilder.CreateEnemyLabels(enemies)];
        Assert.Equal(expected, labels);
        Assert.Equal(players.Length + enemies.Count(enemy => enemy.IsActive), labels.Count);
    }

    [Fact]
    public void AppendLabels_AllocateNothingAfterWarmUp()
    {
        RemotePlayerPresentationState[] players = CreateRemotePlayers();
        EnemyActor[] enemies = CreateEnemies();
        ActorHealthLabelCache cache = new();
        List<WorldActorLabelState> labels = [];

        long before = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            IReadOnlyList<WorldActorLabelState> created =
                [.. ActorWorldLabelBuilder.CreateRemotePlayerLabels(players), .. ActorWorldLabelBuilder.CreateEnemyLabels(enemies)];
            GC.KeepAlive(created);
        });

        long after = AllocationAssert.AtMost(0, () =>
        {
            labels.Clear();
            ActorWorldLabelBuilder.AppendRemotePlayerLabels(players, labels, cache);
            ActorWorldLabelBuilder.AppendEnemyLabels(enemies, labels, cache);
        });

        output.WriteLine($"World actor labels ({players.Length} players, {enemies.Length} enemies): before {before} B/frame, after {after} B/frame");
    }

    private static RemotePlayerPresentationState[] CreateRemotePlayers()
    {
        return
        [
            .. Enumerable.Range(0, 4).Select(index => new RemotePlayerPresentationState(
                index,
                $"session-{index}",
                $"Player {index}",
                new InterpolatedActorPose(new Vector3(index, 1.5f, -4f), -Vector3.UnitZ),
                80 + index,
                100,
                true))
        ];
    }

    private static EnemyActor[] CreateEnemies()
    {
        EnemyActor[] enemies =
        [
            .. Enumerable.Range(0, 8).Select(index =>
            {
                Vector3 position = new(index, 0f, -6f);
                EnemyCombatant combatant = new(() => position, actorId: 2000 + index, maxHealth: 100, attackDamage: 10, attackIntervalSeconds: 1f);
                return new EnemyActor(combatant, position, aggroRange: 5f, attackRange: 1.25f, moveSpeed: 3f, displayName: $"Wolf {index}");
            })
        ];
        enemies[1].Combatant.ApplyDamage(13);
        enemies[2].Combatant.ApplyDamage(1000);
        return enemies;
    }
}
