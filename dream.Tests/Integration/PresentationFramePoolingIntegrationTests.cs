using System.Runtime.CompilerServices;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;
using Dream.Rendering.World;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Integration;

// MEM-5 made WorldRenderBatchPlanner reuse its lists and made the presentation capture hand out
// the enemy registry's live list; MEM-6 cached actor health labels; DEC-6 moved enemy visual
// state into a renderer-owned cache. Each was tested alone. Here one real ClientWorldSession runs
// 100 frames while replicated cubes are placed, moved, destroyed and fractured and replicated
// enemies take damage, die and respawn, and every frame the pooled results are checked against a
// fresh computation, so nothing stale can leak from one frame into the next.
public class PresentationFramePoolingIntegrationTests
{
    private const int FloorSize = 16;
    private const int Frames = 100;
    private static readonly Vector3 PlayerStart = new(8f, 1.5f, 8f);
    private static readonly AnimatedModel GrassModel = CreateModel();
    private static readonly AnimatedModel DirtModel = CreateModel();
    private static readonly AnimatedModel StoneModel = CreateModel();
    private static readonly Func<GeometryMaterialId, AnimatedModel?> Resolver = ResolveLikeCatalog;

    private static readonly GeometryMaterialId[] PlacedMaterials =
    [
        GeometryMaterialId.Grass,
        GeometryMaterialId.Dirt,
        GeometryMaterialId.Stone,
        GeometryMaterialId.Sand,
        GeometryMaterialId.Snow
    ];

    [Fact]
    public void Hundred_Frames_Of_World_And_Enemy_Churn_Keep_Pooled_Batches_Enemies_Visual_States_And_Labels_Fresh()
    {
        using ClientWorldSession session = new(FloorSize, PlayerStart, new SceneEventHub());
        WorldRenderBatchPlanner planner = new(Resolver);
        EnemyVisualStateCache visualStates = new();
        ActorHealthLabelCache healthLabels = new();
        List<WorldActorLabelState> labels = [];
        HashSet<int> currentIds = [];
        Dictionary<int, EnemyVisualState> visualStateById = [];
        Random random = new(20261027);

        Dictionary<GeometryCellKey, Cube> placed = [];
        HashSet<Cube> removed = new(ReferenceEqualityComparer.Instance);
        ServerEnemies enemies = new();
        int placements = 0, moves = 0, destroys = 0, fractures = 0;
        int deaths = 0, respawns = 0, localKills = 0, fastPathFrames = 0, filteredFrames = 0, prunes = 0;

        for (int frame = 0; frame < Frames; frame++)
        {
            // World churn, applied the way the client applies server deltas.
            List<WorldMutationDelta> mutations = [];
            int placeCount = frame < 10 ? 3 : random.Next(0, 3);
            for (int i = 0; i < placeCount; i++)
            {
                if (TryFindOpenCell(session.Map, random, placed, out GeometryCellKey cell))
                {
                    mutations.Add(new WorldMutationDelta(
                        WorldMutationKind.CubePlaced,
                        ToVector(cell),
                        MaterialId: PlacedMaterials[random.Next(PlacedMaterials.Length)]));
                }
            }

            RemoteWorldMutationApplier.ApplyWorldMutations(session.Map, mutations);
            foreach (WorldMutationDelta placement in mutations)
            {
                GeometryCellKey cell = ToKey(placement.CellPosition);
                placed[cell] = Assert.IsType<Cube>(session.Map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));
                Assert.Equal(placement.MaterialId, placed[cell].MaterialId);
                placements++;
            }

            if (placed.Count > 0 && random.Next(3) == 0)
            {
                GeometryCellKey source = placed.Keys.ElementAt(random.Next(placed.Count));
                if (TryFindOpenNeighbour(session.Map, source, placed, out GeometryCellKey target))
                {
                    Cube cube = placed[source];
                    RemoteWorldMutationApplier.ApplyWorldMutations(
                        session.Map,
                        [WorldMutationDelta.Moved(ToVector(source), ToVector(target) + new Vector3(0.5f))]);
                    Assert.Same(cube, session.Map.TryGetCubeAtCell(target.X, target.Y, target.Z));
                    Assert.Null(session.Map.TryGetCubeAtCell(source.X, source.Y, source.Z));
                    placed.Remove(source);
                    placed[target] = cube;
                    moves++;
                }
            }

            if (placed.Count > 4 && random.Next(3) == 0)
            {
                GeometryCellKey cell = placed.Keys.ElementAt(random.Next(placed.Count));
                RemoteWorldMutationApplier.ApplyWorldMutations(
                    session.Map,
                    [new WorldMutationDelta(WorldMutationKind.CubeDestroyed, ToVector(cell))]);
                Assert.Null(session.Map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));
                removed.Add(placed[cell]);
                placed.Remove(cell);
                destroys++;
            }

            if (placed.Count > 4 && frame % 17 == 5)
            {
                GeometryCellKey cell = placed.Keys.ElementAt(random.Next(placed.Count));
                Cube cube = placed[cell];
                RemoteWorldMutationApplier.ApplyWorldMutations(
                    session.Map,
                    [new WorldMutationDelta(WorldMutationKind.CubeFractured, ToVector(cell), MaterialId: cube.MaterialId)]);
                Assert.Null(session.Map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));
                removed.Add(cube);
                placed.Remove(cell);
                fractures++;
            }

            // Enemy churn, applied the way the client applies server actor snapshots.
            (int died, int respawned) = enemies.Step(random, frame);
            deaths += died;
            respawns += respawned;
            RemoteEnemyStateApplier.ApplyActorSnapshots(session.Enemies, enemies.CreateSnapshots());

            // Client-side damage feedback can drop an enemy to 0 before the next snapshot, which
            // leaves an inactive actor in the registry and takes the presentation's filtered path.
            int? locallyKilled = null;
            if (session.Enemies.ActiveActors.Count > 1 && random.Next(4) == 0)
            {
                EnemyActor victim = session.Enemies.ActiveActors[random.Next(session.Enemies.ActiveActors.Count)];
                victim.Combatant.ApplyDamage(100_000);
                Assert.False(victim.IsActive);
                locallyKilled = victim.Combatant.Actor.Id;
                localKills++;
            }

            session.AdvancePredictionTick(
                (ulong)frame + 1,
                SimulationClock.FixedDeltaSeconds,
                PlayerCommandFrame.CreateIdle(PlayerSessionId.LocalDefault, (ulong)frame + 1),
                PlayerSimulationTuning.AuthoritativeDefault);
            WorldSessionPresentationState state = session.CapturePresentationState(WorldVisibility.MaxRenderDistance);
            GeometryRenderExport export = state.VisibleWorld;

            // MEM-5: every pooled plan equals a fresh static-path plan of the same frame. Each
            // plan is checked before the next call, because the next call reuses its lists.
            HashSet<Cube> batchedCubes = new(ReferenceEqualityComparer.Instance);
            HashSet<GeometryCellKey> batchedBlockCells = [];
            AssertCubePlanMatchesStatic(planner.PlanCubeBatches(export.CompatibilityCubes), export.CompatibilityCubes, batchedCubes, frame, "static cubes");
            AssertCubePlanMatchesStatic(planner.PlanCubeBatches(export.DynamicCompatibilityCubes), export.DynamicCompatibilityCubes, batchedCubes, frame, "dynamic cubes");
            AssertBlockPlanMatchesStatic(planner.PlanGeometryBlockBatches(export.Blocks), export.Blocks, batchedBlockCells, frame, "static blocks");
            AssertBlockPlanMatchesStatic(planner.PlanGeometryBlockBatches(export.DynamicBlocks), export.DynamicBlocks, batchedBlockCells, frame, "dynamic blocks");

            foreach (Cube gone in removed)
            {
                Assert.False(batchedCubes.Contains(gone), $"Frame {frame}: a destroyed or fractured cube is still batched.");
            }

            foreach ((GeometryCellKey cell, Cube cube) in placed)
            {
                Assert.True(
                    batchedCubes.Contains(cube) || batchedBlockCells.Contains(cell),
                    $"Frame {frame}: the placed cube at {cell} is missing from every batch.");
            }

            // MEM-5: ActiveEnemies is the live registry list when everything is active, and an
            // ordered copy of the active ones otherwise.
            EnemyActor[] expectedActive = session.Enemies.ActiveActors.Where(actor => actor.IsActive).ToArray();
            Assert.Equal(expectedActive, state.ActiveEnemies);
            if (expectedActive.Length == session.Enemies.ActiveActors.Count)
            {
                Assert.Same(session.Enemies.ActiveActors, state.ActiveEnemies);
                fastPathFrames++;
            }
            else
            {
                Assert.NotSame(session.Enemies.ActiveActors, state.ActiveEnemies);
                Assert.DoesNotContain(state.ActiveEnemies, actor => actor.Combatant.Actor.Id == locallyKilled);
                filteredFrames++;
            }

            Assert.Equal(enemies.AliveIds.Count, session.Enemies.ActiveActors.Count);
            foreach (EnemyActor enemy in state.ActiveEnemies)
            {
                Assert.Contains(enemy.Combatant.Actor.Id, enemies.AliveIds);
            }

            // DEC-6: what EnemyRenderer.Draw does with its cache, minus the GL draw.
            currentIds.Clear();
            foreach (EnemyActor actor in state.ActiveEnemies)
            {
                int actorId = actor.Combatant.Actor.Id;
                currentIds.Add(actorId);
                EnemyVisualState visualState = visualStates.GetOrAdd(actorId);
                if (visualStateById.TryGetValue(actorId, out EnemyVisualState? previous))
                {
                    Assert.Same(previous, visualState);
                }
                else
                {
                    visualStateById[actorId] = visualState;
                }
            }

            int countBeforePrune = visualStates.Count;
            visualStates.PruneExcept(currentIds);
            prunes += countBeforePrune - visualStates.Count;
            Assert.Equal(currentIds.Count, visualStates.Count);
            foreach (int staleId in visualStateById.Keys.Where(id => !currentIds.Contains(id)).ToArray())
            {
                visualStateById.Remove(staleId);
            }

            // MEM-6: cached labels equal freshly formatted ones and follow health changes.
            labels.Clear();
            ActorWorldLabelBuilder.AppendEnemyLabels(state.ActiveEnemies, labels, healthLabels);
            IReadOnlyList<WorldActorLabelState> fresh = ActorWorldLabelBuilder.CreateEnemyLabels(state.ActiveEnemies);
            Assert.Equal(fresh, labels);
            Assert.Equal(state.ActiveEnemies.Count, labels.Count);
            for (int i = 0; i < labels.Count; i++)
            {
                EnemyActor enemy = state.ActiveEnemies[i];
                ServerEnemies.Entry entry = enemies.Get(enemy.Combatant.Actor.Id);
                Assert.Equal($"{entry.Health}/{entry.MaxHealth}", labels[i].HealthLabel);
                Assert.Equal(entry.Name, labels[i].DisplayName);
                Assert.Equal(enemy.Position + new Vector3(0f, enemy.CollisionHeight + 0.25f, 0f), labels[i].WorldAnchor);
            }
        }

        // The run actually exercised every path it claims to cover.
        Assert.True(placements > 20, $"placements {placements}");
        Assert.True(moves > 5, $"moves {moves}");
        Assert.True(destroys > 5, $"destroys {destroys}");
        Assert.True(fractures > 2, $"fractures {fractures}");
        Assert.True(deaths > 5, $"deaths {deaths}");
        Assert.True(respawns > 3, $"respawns {respawns}");
        Assert.True(localKills > 5, $"local kills {localKills}");
        Assert.True(fastPathFrames > 20, $"fast-path frames {fastPathFrames}");
        Assert.True(filteredFrames > 5, $"filtered frames {filteredFrames}");
        Assert.True(prunes > 5, $"pruned visual states {prunes}");
    }

    private static void AssertCubePlanMatchesStatic(
        IReadOnlyList<WorldRenderBatch> actual,
        IReadOnlyList<Cube> source,
        HashSet<Cube> batched,
        int frame,
        string label)
    {
        List<WorldRenderBatch> expected = WorldRenderBatchPlanner.CreateBatches(source.ToList(), Resolver);
        Assert.True(expected.Count == actual.Count, $"Frame {frame} {label}: {actual.Count} batches, expected {expected.Count}.");
        int items = 0;
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Same(expected[i].Animation, actual[i].Animation);
            Assert.Equal(expected[i].ShaderName, actual[i].ShaderName);
            Assert.Equal(expected[i].Cubes, actual[i].Cubes);
            foreach (Cube cube in actual[i].Cubes)
            {
                Assert.Same(Resolver(cube.MaterialId), actual[i].Animation);
                Assert.Equal(actual[i].ShaderName, cube.ShaderName);
                batched.Add(cube);
            }

            items += actual[i].Cubes.Count;
        }

        Assert.True(items == source.Count, $"Frame {frame} {label}: {items} batched cubes, export has {source.Count}.");
    }

    private static void AssertBlockPlanMatchesStatic(
        IReadOnlyList<WorldGeometryRenderBatch> actual,
        IReadOnlyList<GeometryRenderBlockDescriptor> source,
        HashSet<GeometryCellKey> batchedCells,
        int frame,
        string label)
    {
        List<WorldGeometryRenderBatch> expected = WorldRenderBatchPlanner.CreateGeometryBlockBatches(source.ToList(), Resolver);
        Assert.True(expected.Count == actual.Count, $"Frame {frame} {label}: {actual.Count} batches, expected {expected.Count}.");
        int items = 0;
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Same(expected[i].Animation, actual[i].Animation);
            Assert.Equal(expected[i].ShaderName, actual[i].ShaderName);
            Assert.Equal(expected[i].Blocks, actual[i].Blocks);
            foreach (GeometryRenderBlockDescriptor block in actual[i].Blocks)
            {
                Assert.Same(Resolver(block.MaterialId), actual[i].Animation);
                batchedCells.Add(block.CellKey);
            }

            items += actual[i].Blocks.Count;
        }

        Assert.True(items == source.Count, $"Frame {frame} {label}: {items} batched blocks, export has {source.Count}.");
    }

    private static bool TryFindOpenCell(Map map, Random random, Dictionary<GeometryCellKey, Cube> placed, out GeometryCellKey cell)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            GeometryCellKey candidate = new(random.Next(2, FloorSize - 2), random.Next(2, 6), random.Next(2, FloorSize - 2));
            bool nearPlayer = Math.Abs(candidate.X - (int)PlayerStart.X) <= 1 && Math.Abs(candidate.Z - (int)PlayerStart.Z) <= 1;
            if (!nearPlayer && IsOpen(map, candidate) && !placed.ContainsKey(candidate))
            {
                cell = candidate;
                return true;
            }
        }

        cell = default;
        return false;
    }

    private static bool TryFindOpenNeighbour(Map map, GeometryCellKey source, Dictionary<GeometryCellKey, Cube> placed, out GeometryCellKey target)
    {
        GeometryCellKey[] neighbours =
        [
            new(source.X + 1, source.Y, source.Z),
            new(source.X - 1, source.Y, source.Z),
            new(source.X, source.Y, source.Z + 1),
            new(source.X, source.Y, source.Z - 1),
            new(source.X, source.Y + 1, source.Z)
        ];
        foreach (GeometryCellKey candidate in neighbours)
        {
            bool nearPlayer = Math.Abs(candidate.X - (int)PlayerStart.X) <= 1 && Math.Abs(candidate.Z - (int)PlayerStart.Z) <= 1;
            if (candidate.X >= 1 && candidate.Z >= 1 && candidate.X < FloorSize - 1 && candidate.Z < FloorSize - 1 &&
                candidate.Y >= 1 && candidate.Y < 8 && !nearPlayer && IsOpen(map, candidate) && !placed.ContainsKey(candidate))
            {
                target = candidate;
                return true;
            }
        }

        target = default;
        return false;
    }

    private static bool IsOpen(Map map, GeometryCellKey cell)
    {
        return map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z) == null &&
            map.TryGetGeometryCellAtCell(cell.X, cell.Y, cell.Z) == null &&
            map.TryGetRubbleAtCell(cell) == null;
    }

    private static Vector3 ToVector(GeometryCellKey cell) => new(cell.X, cell.Y, cell.Z);

    private static GeometryCellKey ToKey(Vector3 cell) => new((int)MathF.Floor(cell.X + 1e-6f), (int)MathF.Floor(cell.Y + 1e-6f), (int)MathF.Floor(cell.Z + 1e-6f));

    private static AnimatedModel? ResolveLikeCatalog(GeometryMaterialId materialId)
    {
        return materialId switch
        {
            GeometryMaterialId.Dirt => DirtModel,
            GeometryMaterialId.Stone => StoneModel,
            GeometryMaterialId.Grass => GrassModel,
            _ => null
        };
    }

    private static AnimatedModel CreateModel()
    {
        return (AnimatedModel)RuntimeHelpers.GetUninitializedObject(typeof(AnimatedModel));
    }

    // The server's view of the wolves: health changes, deaths, respawns under the same id after a
    // few frames, and an occasional brand new id.
    private sealed class ServerEnemies
    {
        private readonly Dictionary<int, Entry> _entries = [];
        private int _nextId = 9001;

        public ServerEnemies()
        {
            for (int i = 0; i < 6; i++)
            {
                Spawn(_nextId++);
            }
        }

        public HashSet<int> AliveIds => _entries.Values.Where(entry => entry.Health > 0).Select(entry => entry.Id).ToHashSet();

        public Entry Get(int id) => _entries[id];

        public (int Died, int Respawned) Step(Random random, int frame)
        {
            int died = 0;
            int respawned = 0;
            foreach (Entry entry in _entries.Values.ToArray())
            {
                if (entry.Health <= 0)
                {
                    if (frame >= entry.RespawnFrame)
                    {
                        Spawn(entry.Id);
                        respawned++;
                    }

                    continue;
                }

                entry.Position += new Vector3((float)(random.NextDouble() - 0.5) * 0.2f, 0f, (float)(random.NextDouble() - 0.5) * 0.2f);
                if (random.Next(3) == 0)
                {
                    entry.Health = Math.Max(0, entry.Health - random.Next(5, 40));
                }

                if (entry.Health == 0)
                {
                    entry.RespawnFrame = frame + random.Next(2, 6);
                    died++;
                }
            }

            if (frame % 25 == 24)
            {
                Spawn(_nextId++);
            }

            return (died, respawned);
        }

        public IReadOnlyList<ActorSnapshot> CreateSnapshots()
        {
            // Dead wolves are still reported by the server, as not alive.
            return _entries.Values
                .OrderBy(entry => entry.Id)
                .Select(entry => new ActorSnapshot(
                    entry.Id,
                    CreatureKind.Enemy,
                    null,
                    entry.Position,
                    -Vector3.UnitZ,
                    entry.Health,
                    entry.MaxHealth,
                    entry.Health > 0,
                    DisplayName: entry.Name,
                    EnemyBehaviorState: EnemyBehaviorState.Idle))
                .ToArray();
        }

        private void Spawn(int id)
        {
            int maxHealth = 80 + (id % 3) * 10;
            _entries[id] = new Entry
            {
                Id = id,
                Health = maxHealth,
                MaxHealth = maxHealth,
                Name = $"Wolf {id}",
                Position = new Vector3(3f + id % 9, 1f, 3f + (id / 3) % 9)
            };
        }

        public sealed class Entry
        {
            public int Id { get; init; }

            public int Health { get; set; }

            public int MaxHealth { get; init; }

            public string Name { get; init; } = string.Empty;

            public Vector3 Position { get; set; }

            public int RespawnFrame { get; set; }
        }
    }
}
