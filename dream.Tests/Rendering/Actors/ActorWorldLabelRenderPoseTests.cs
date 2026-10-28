using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Actors;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.Actors;

// Enemy name/health labels sit over the pose the wolf model draws at (the interpolated pose on
// the server-tick render timeline), not over the authoritative position that leads it.
public class ActorWorldLabelRenderPoseTests(ITestOutputHelper output)
{
    private const int WolfId = 2000;

    [Fact]
    public void EnemyLabel_WithAPoseSource_SitsOverTheInterpolatedRenderPose()
    {
        ClientScenePresenter presenter = new("local-session");
        EnemyRegistry registry = new();
        for (ulong tick = 1; tick <= 8; tick++)
        {
            ServerSnapshotEnvelope snapshot = Snapshot(tick, Wolf(new Vector3(tick * 0.25f, 0f, -3f)));
            RemoteEnemyStateApplier.ApplyActorSnapshots(registry, snapshot.Actors);
            presenter.ApplySnapshot(snapshot);
        }

        Assert.True(registry.TryGetEnemyByActorId(WolfId, out EnemyActor? enemy));
        double renderTick = 5.5d;
        List<WorldActorLabelState> labels = [];

        ActorWorldLabelBuilder.AppendEnemyLabels([enemy!], labels, new ActorHealthLabelCache(), presenter, renderTick);

        WorldActorLabelState label = Assert.Single(labels);
        Vector3 renderPosition = EnemyRenderer.ResolveRenderPose(enemy!, presenter, renderTick).Position;
        Vector3 labelOffset = new(0f, enemy!.CollisionHeight + 0.25f, 0f);
        Assert.Equal(1.375f, renderPosition.X, 4);
        Assert.Equal(renderPosition + labelOffset, label.WorldAnchor);
        // The authoritative position (tick 8) leads the model; the label no longer follows it.
        Assert.Equal(2f, enemy.Position.X, 4);
        Assert.NotEqual(enemy.Position + labelOffset, label.WorldAnchor);
    }

    [Fact]
    public void EnemyLabel_WithoutAPoseSource_OrWithoutAPose_FallsBackToTheAuthoritativePosition()
    {
        EnemyActor enemy = CreateEnemy(new Vector3(4f, 0f, -2f));
        Vector3 expected = enemy.Position + new Vector3(0f, enemy.CollisionHeight + 0.25f, 0f);
        List<WorldActorLabelState> labels = [];

        ActorWorldLabelBuilder.AppendEnemyLabels([enemy], labels, healthLabels: null);
        ActorWorldLabelBuilder.AppendEnemyLabels([enemy], labels, healthLabels: null, new FixedPoseSource(null), 12d);

        Assert.Equal(2, labels.Count);
        Assert.All(labels, label => Assert.Equal(expected, label.WorldAnchor));
        Assert.Equal(ActorWorldLabelBuilder.CreateEnemyLabels([enemy]), labels.Take(1));
    }

    [Fact]
    public void EnemyLabel_UsesTheRenderTickItIsGiven()
    {
        EnemyActor enemy = CreateEnemy(new Vector3(4f, 0f, -2f));
        FixedPoseSource source = new(new Vector3(1f, 0f, 1f));
        List<WorldActorLabelState> labels = [];

        ActorWorldLabelBuilder.AppendEnemyLabels([enemy], labels, healthLabels: null, source, 42.25d);

        Assert.Equal(42.25d, source.LastRenderTick);
        Assert.Equal(WolfId, source.LastActorId);
        Assert.Equal(new Vector3(1f, enemy.CollisionHeight + 0.25f, 1f), Assert.Single(labels).WorldAnchor);
    }

    [Fact]
    public void AppendEnemyLabels_WithAPoseSource_AllocateNothingAfterWarmUp()
    {
        EnemyActor[] enemies =
        [
            .. Enumerable.Range(0, 8).Select(index => CreateEnemy(new Vector3(index, 0f, -6f), WolfId + index))
        ];
        enemies[1].Combatant.ApplyDamage(13);
        FixedPoseSource source = new(new Vector3(0.5f, 0f, -5f));
        ActorHealthLabelCache cache = new();
        List<WorldActorLabelState> labels = [];

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            labels.Clear();
            ActorWorldLabelBuilder.AppendEnemyLabels(enemies, labels, cache, source, 7.5d);
        });

        output.WriteLine($"Enemy labels at render poses ({enemies.Length} enemies): {bytes} B/frame");
    }

    private static EnemyActor CreateEnemy(Vector3 position, int actorId = WolfId)
    {
        EnemyCombatant combatant = new(() => position, actorId: actorId, maxHealth: 100, attackDamage: 10, attackIntervalSeconds: 1f);
        return new EnemyActor(combatant, position, aggroRange: 5f, attackRange: 1.25f, moveSpeed: 3f, displayName: "Wolf");
    }

    private static ServerSnapshotEnvelope Snapshot(ulong tick, params ActorSnapshot[] actors)
    {
        return new ServerSnapshotEnvelope(
            tick,
            new Dictionary<string, ulong>(),
            actors,
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());
    }

    private static ActorSnapshot Wolf(Vector3 position)
    {
        return new ActorSnapshot(
            WolfId,
            CreatureKind.Enemy,
            null,
            position,
            -Vector3.UnitZ,
            50,
            50,
            true,
            DisplayName: "Wolf");
    }

    private sealed class FixedPoseSource(Vector3? position) : IEnemyRenderPoseSource
    {
        public double LastRenderTick { get; private set; } = double.NaN;

        public int LastActorId { get; private set; } = -1;

        public bool TryGetEnemyRenderPose(int actorId, double renderTick, out InterpolatedActorPose pose)
        {
            LastActorId = actorId;
            LastRenderTick = renderTick;
            if (position is Vector3 value)
            {
                pose = new InterpolatedActorPose(value, Vector3.UnitX);
                return true;
            }

            pose = default;
            return false;
        }
    }
}
