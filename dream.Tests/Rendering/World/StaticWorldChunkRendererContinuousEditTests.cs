using System.Runtime.CompilerServices;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

// The renderer uploads every completed batch, even one a newer request already supersedes, and
// plans each build against the signatures the meshes will have once the pending upload batch is
// applied. Whatever the build timing, once the edits stop the uploaded meshes must equal a plan
// built from scratch for the final world.
public class StaticWorldChunkRendererContinuousEditTests
{
    private static readonly Render UnusedRender = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
    private static readonly Camera TestCamera = new(Vector3.Zero);
    private static readonly StaticWorldChunkKey PaddingChunk = new(100, 0, 0);

    private static readonly GeometryMaterialId[] Materials =
    [
        GeometryMaterialId.Stone, GeometryMaterialId.Dirt, GeometryMaterialId.Grass
    ];

    [Theory]
    [InlineData(7, 1)]
    [InlineData(1234, 2)]
    [InlineData(98765, 3)]
    [InlineData(4242, 1)]
    public void ContinuousEdits_ThenStop_UploadedMeshesMatchAFromScratchPlan(int seed, int buildFrames)
    {
        Random random = new(seed);
        // Slots straddle the chunk borders at x = 16 and z = 16.
        const int MinX = 12, MaxX = 20, MinY = 0, MaxY = 4, MinZ = 12, MaxZ = 20;
        Dictionary<(int X, int Y, int Z), GeometryMaterialId> slots = new();
        for (int x = MinX; x < MaxX; x++)
        {
            for (int y = MinY; y < MaxY; y++)
            {
                for (int z = MinZ; z < MaxZ; z++)
                {
                    if (random.NextDouble() < 0.6)
                    {
                        slots[(x, y, z)] = Materials[random.Next(Materials.Length)];
                    }
                }
            }
        }

        StoringMeshStore store = new();
        List<(TaskCompletionSource<StaticWorldChunkBuildBatch> Build, StaticWorldChunkBuildRequest Request, int StartFrame)> builds = [];
        int frame = 0;
        using StaticWorldChunkRenderer renderer = new(
            request =>
            {
                TaskCompletionSource<StaticWorldChunkBuildBatch> build = new();
                builds.Add((build, request, frame));
                return build.Task;
            },
            new RenderFaultLog(_ => { }),
            store);

        long version = 1;
        GeometryRenderExport export = CreateExport(slots, version, []);
        (int X, int Y, int Z)? toggled = null;
        int uploadsWhileEditing = 0;
        for (; frame < 160; frame++)
        {
            if (frame > 0 && frame <= 60)
            {
                // An edit on every frame, biased onto the border planes. Every other frame also
                // reverts the previous frame's toggle, so meshes return to earlier signatures.
                List<GeometryCellKey> dirty = [];
                if (toggled is { } previous && frame % 2 == 0)
                {
                    Toggle(slots, previous, random);
                    dirty.Add(new GeometryCellKey(previous.X, previous.Y, previous.Z));
                    toggled = null;
                }
                else
                {
                    int x = random.NextDouble() < 0.5 ? 15 + random.Next(2) : MinX + random.Next(MaxX - MinX);
                    int y = MinY + random.Next(MaxY - MinY);
                    int z = random.NextDouble() < 0.5 ? 15 + random.Next(2) : MinZ + random.Next(MaxZ - MinZ);
                    Toggle(slots, (x, y, z), random);
                    dirty.Add(new GeometryCellKey(x, y, z));
                    toggled = (x, y, z);
                }

                version++;
                export = CreateExport(slots, version, dirty);
            }
            // After frame 60 the last edited export repeats verbatim. Its signature equals the last
            // scheduled one, so no follow-up request (with a fresh signature snapshot) can mask a
            // mesh the queued pipeline got wrong.

            int uploadsBefore = store.UploadCount;
            renderer.Draw(UnusedRender, TestCamera, export);
            if (frame > 0 && frame <= 60)
            {
                uploadsWhileEditing += store.UploadCount - uploadsBefore;
            }

            foreach ((TaskCompletionSource<StaticWorldChunkBuildBatch> build, StaticWorldChunkBuildRequest request, int startFrame) in builds.ToArray())
            {
                if (!build.Task.IsCompleted && frame - startFrame >= buildFrames)
                {
                    build.SetResult(StaticWorldChunkRenderer.BuildBatch(request));
                }
            }
        }

        Assert.True(builds.All(entry => entry.Build.Task.IsCompleted), "builds still pending after the edits stopped");
        Assert.True(uploadsWhileEditing > 0, $"{builds.Count} builds over 60 edited frames and none was uploaded");
        AssertStoreMatchesScratch(store, StaticWorldChunkExtractor.ExtractCells(export));
    }

    // A request scheduled while an upload batch is pending (not yet started, or cut short by the
    // byte budget) used to copy the signatures applied so far. Reverting an edit then let the plan
    // skip a non-dirty neighbour mesh whose cached signature still matched, and the pending batch
    // left that neighbour showing the reverted edit.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevertedEdit_ScheduledWhileABatchIsPending_RebuildsTheNeighbourMesh(bool budgetSplitsTheBatch)
    {
        StaticWorldChunkBuildOutput? padding = budgetSplitsTheBatch ? CreatePaddingOutput() : null;
        StoringMeshStore store = new();
        using StaticWorldChunkRenderer renderer = new(
            request =>
            {
                StaticWorldChunkBuildBatch batch = StaticWorldChunkRenderer.BuildBatch(request);
                if (padding != null)
                {
                    // A mesh over the byte budget at the head of every batch: the first upload
                    // frame applies it alone and leaves the chunk meshes pending.
                    batch = batch with { Outputs = [padding, .. batch.Outputs] };
                }

                return Task.FromResult(batch);
            },
            new RenderFaultLog(_ => { }),
            store);

        // c and a sit in chunk (0,0,0); b sits across the x = 16 border in chunk (1,0,0), so
        // removing a exposes b's -X face and changes b's mesh without dirtying its chunk.
        Dictionary<(int X, int Y, int Z), GeometryMaterialId> slots = new()
        {
            [(14, 0, 0)] = GeometryMaterialId.Stone,
            [(15, 0, 0)] = GeometryMaterialId.Stone,
            [(16, 0, 0)] = GeometryMaterialId.Stone
        };
        GeometryRenderExport original = CreateExport(slots, 1, []);
        DrawUntilIdle(renderer, original);
        AssertStoreMatchesScratch(store, StaticWorldChunkExtractor.ExtractCells(original));

        slots.Remove((15, 0, 0));
        GeometryRenderExport removed = CreateExport(slots, 2, [new GeometryCellKey(15, 0, 0)]);
        renderer.Draw(UnusedRender, TestCamera, removed);
        if (budgetSplitsTheBatch)
        {
            // The repeated export schedules nothing; the frame takes the removal batch and
            // uploads only the padding mesh.
            renderer.Draw(UnusedRender, TestCamera, removed);
        }

        // The revert is scheduled before the removal batch's chunk meshes are uploaded.
        slots[(15, 0, 0)] = GeometryMaterialId.Stone;
        GeometryRenderExport restored = CreateExport(slots, 3, [new GeometryCellKey(15, 0, 0)]);
        // Repeating the same export schedules nothing more; a clean export would schedule one
        // follow-up request with a fresh snapshot, which would hide the stale plan.
        DrawUntilIdle(renderer, restored);

        AssertStoreMatchesScratch(store, StaticWorldChunkExtractor.ExtractCells(restored));
    }

    private static void Toggle(
        Dictionary<(int X, int Y, int Z), GeometryMaterialId> slots,
        (int X, int Y, int Z) slot,
        Random random)
    {
        if (!slots.Remove(slot))
        {
            slots[slot] = Materials[random.Next(Materials.Length)];
        }
    }

    private static void DrawUntilIdle(StaticWorldChunkRenderer renderer, GeometryRenderExport export)
    {
        int idleFrames = 0;
        for (int frame = 0; frame < 64; frame++)
        {
            StaticWorldChunkRenderStats stats = renderer.Draw(UnusedRender, TestCamera, export);
            idleFrames = stats.RebuiltMeshes == 0 ? idleFrames + 1 : 0;
            if (idleFrames >= 3)
            {
                return;
            }
        }

        Assert.Fail("The static chunk renderer did not settle.");
    }

    private static void AssertStoreMatchesScratch(StoringMeshStore store, IReadOnlyList<StaticVoxelRenderCell> cells)
    {
        Dictionary<StaticWorldChunkMeshKey, StaticWorldChunkMesh> scratch = StaticWorldChunkRenderer
            .PlanBuildInputs(cells)
            .Select(input => (input.Key, Mesh: input.BuildMesh()))
            .Where(entry => entry.Mesh.ExposedFaceCount > 0)
            .ToDictionary(entry => entry.Key, entry => entry.Mesh);
        Dictionary<StaticWorldChunkMeshKey, StaticWorldChunkMesh> live = store.Meshes
            .Where(pair => pair.Key.ChunkKey != PaddingChunk)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        Assert.Equal(scratch.Keys.OrderBy(key => key.ToString()), live.Keys.OrderBy(key => key.ToString()));
        foreach ((StaticWorldChunkMeshKey key, StaticWorldChunkMesh expected) in scratch)
        {
            StaticWorldChunkMesh actual = live[key];
            Assert.True(expected.Vertices.SequenceEqual(actual.Vertices), $"{key} vertices differ from the scratch plan");
            Assert.True(expected.Indices.SequenceEqual(actual.Indices), $"{key} indices differ from the scratch plan");
        }
    }

    private static StaticWorldChunkBuildOutput CreatePaddingOutput()
    {
        List<StaticVoxelRenderCell> cells = [];
        for (int i = 0; i < 3000; i++)
        {
            // Two-unit spacing keeps every face exposed: 18000 faces, well over the byte budget.
            cells.Add(new StaticVoxelRenderCell(
                new Vector3((PaddingChunk.X * 16) + 0.5f + (2 * (i % 8)), 0.5f + (2 * (i / 64)), 0.5f + (2 * ((i / 8) % 8))),
                Vector3.One,
                GeometryMaterialId.Stone));
        }

        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(PaddingChunk, GeometryMaterialId.Stone, cells);
        Assert.True(mesh.UploadByteCount > StaticWorldChunkRenderer.UploadByteBudgetPerFrame);
        return new StaticWorldChunkBuildOutput(
            new StaticWorldChunkMeshKey(PaddingChunk, GeometryMaterialId.Stone, StaticTerrainMaterialRole.Single),
            Signature: 1,
            mesh);
    }

    private static GeometryRenderExport CreateExport(
        Dictionary<(int X, int Y, int Z), GeometryMaterialId> slots,
        long version,
        IReadOnlyList<GeometryCellKey> dirtyCells)
    {
        List<Cube> cubes = [];
        foreach (((int x, int y, int z), GeometryMaterialId material) in slots
            .OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.Z))
        {
            cubes.Add(new Cube(isEmpty: false, isSolid: true)
            {
                Position = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f),
                MaterialId = material,
                ShaderName = "default"
            });
        }

        return new GeometryRenderExport(
            DirtyCells: dirtyCells,
            Cells: [],
            Blocks: [],
            CompatibilityCubes: cubes)
        {
            StaticContentVersion = version
        };
    }

    private sealed class StoringMeshStore : IStaticWorldChunkMeshStore
    {
        public Dictionary<StaticWorldChunkMeshKey, StaticWorldChunkMesh> Meshes { get; } = new();

        public int UploadCount { get; private set; }

        public void Upload(StaticWorldChunkMeshKey key, StaticWorldChunkMesh mesh)
        {
            UploadCount++;
            Meshes[key] = mesh;
        }

        public void Remove(StaticWorldChunkMeshKey key)
        {
            Meshes.Remove(key);
        }

        public bool Draw(Render render, Camera camera, in StaticWorldChunkMeshInfo mesh)
        {
            return Meshes.ContainsKey(mesh.Key);
        }

        public void Clear()
        {
            Meshes.Clear();
        }
    }
}
