using System.Reflection;
using System.Runtime.CompilerServices;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-TERRAIN-1: a chunk that loses every cell without any of its own cells being dirtied must lose
// its GPU mesh, or the terrain of a replaced (smaller) map stays drawn and keeps casting shadows.
// BH-TERRAIN-2: a failed GPU upload must not be remembered as done, or that chunk stays a hole.
// All CPU-side bookkeeping: a recording mesh store stands in for GL, no context is needed.
public class TerrainStaticChunkRendererTests
{
    private static readonly Render UnusedRender = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
    private static readonly Camera TestCamera = new(Vector3.Zero);

    [Fact]
    public void ChunkThatLosesEveryCell_LosesItsMesh_EvenWithoutADirtyCell()
    {
        // Two cubes two chunks apart, so neither is in the other's 27-chunk neighbourhood.
        Vector3 kept = new(0.5f, 0.5f, 0.5f);
        Vector3 vanishing = new(40.5f, 0.5f, 40.5f);
        StaticWorldChunkKey keptChunk = StaticWorldChunkKey.FromWorldPosition(kept);
        StaticWorldChunkKey vanishedChunk = StaticWorldChunkKey.FromWorldPosition(vanishing);
        Assert.NotEqual(keptChunk, vanishedChunk);

        RecordingMeshStore store = new();
        using StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);

        GeometryRenderExport both = CreateExport(version: 1, kept, vanishing);
        DrawUntilSettled(renderer, both);
        Assert.Contains(keptChunk, store.LiveKeys.Select(key => key.ChunkKey));
        Assert.Contains(vanishedChunk, store.LiveKeys.Select(key => key.ChunkKey));

        // The map was replaced by a smaller one: the second chunk's cells are simply gone, and
        // nothing ever marks a cell of a chunk that no longer exists as dirty.
        GeometryRenderExport smaller = CreateExport(version: 2, kept);
        Assert.Empty(smaller.StaticDirtyCells);
        DrawUntilSettled(renderer, smaller);

        Assert.DoesNotContain(vanishedChunk, store.LiveKeys.Select(key => key.ChunkKey));
        Assert.Contains(keptChunk, store.LiveKeys.Select(key => key.ChunkKey));
        Assert.All(ResidentSignatureKeys(renderer), key => Assert.NotEqual(vanishedChunk, key.ChunkKey));
        // Exactly one chunk is left resident on the GPU.
        Assert.Single(store.LiveKeys.Select(key => key.ChunkKey).Distinct());
    }

    [Fact]
    public void SignatureSnapshot_DropsAVanishedChunk_LikeTheRealRemovalDoes()
    {
        // The snapshot handed to the next build must agree with the removal ApplyPendingUploads
        // runs on completion; otherwise the next build plans against a mesh that is no longer there.
        StaticWorldChunkKey present = new(0, 0, 0);
        StaticWorldChunkKey gone = new(7, 0, 7);
        StaticWorldChunkMeshKey presentKey = new(present, GeometryMaterialId.Stone, StaticTerrainMaterialRole.Single);
        StaticWorldChunkMeshKey goneKey = new(gone, GeometryMaterialId.Stone, StaticTerrainMaterialRole.Single);

        StaticWorldChunkBuildBatch batch = new(
            ExportSignature: 1,
            DirtyChunks: new HashSet<StaticWorldChunkKey>(),
            Outputs: [],
            PresentChunks: new HashSet<StaticWorldChunkKey> { present });
        StaticWorldChunkUploadBatch upload = new(batch);
        Dictionary<StaticWorldChunkMeshKey, int> signatures = new()
        {
            [presentKey] = 11,
            [goneKey] = 22
        };

        upload.ApplyRemainingSignaturesTo(signatures);

        Assert.True(signatures.ContainsKey(presentKey));
        Assert.False(signatures.ContainsKey(goneKey));
    }

    [Fact]
    public void BatchWithoutACellSnapshot_RetiresNothingOutsideItsDirtySet()
    {
        // A batch that did not come from a cell snapshot knows nothing about which chunks are live,
        // so it must not retire anything the old dirty-set rule kept.
        StaticWorldChunkMeshKey key = new(new StaticWorldChunkKey(3, 0, 3), GeometryMaterialId.Stone, StaticTerrainMaterialRole.Single);
        StaticWorldChunkUploadBatch upload = new(new StaticWorldChunkBuildBatch(
            1,
            new HashSet<StaticWorldChunkKey>(),
            []));
        Dictionary<StaticWorldChunkMeshKey, int> signatures = new() { [key] = 5 };

        upload.ApplyRemainingSignaturesTo(signatures);

        Assert.True(signatures.ContainsKey(key));
    }

    private static IEnumerable<StaticWorldChunkMeshKey> ResidentSignatureKeys(StaticWorldChunkRenderer renderer)
    {
        Dictionary<StaticWorldChunkMeshKey, int> signatures = (Dictionary<StaticWorldChunkMeshKey, int>)typeof(StaticWorldChunkRenderer)
            .GetField("_signatures", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(renderer)!;
        return signatures.Keys.ToArray();
    }

    // Draws until the renderer has applied the export's batch and gone quiet.
    private static void DrawUntilSettled(StaticWorldChunkRenderer renderer, GeometryRenderExport export)
    {
        for (int frame = 0; frame < 64; frame++)
        {
            StaticWorldChunkRenderStats stats = renderer.Draw(UnusedRender, TestCamera, export);
            if (frame > 1 && stats.RebuiltMeshes == 0)
            {
                return;
            }
        }

        Assert.Fail("The static chunk batch was not applied.");
    }

    private static GeometryRenderExport CreateExport(long version, params Vector3[] positions)
    {
        List<Cube> cubes = [];
        foreach (Vector3 position in positions)
        {
            cubes.Add(new Cube(isEmpty: false, isSolid: true)
            {
                Position = position,
                MaterialId = GeometryMaterialId.Stone,
                ShaderName = "default"
            });
        }

        return new GeometryRenderExport(
            DirtyCells: [],
            Cells: [],
            Blocks: [],
            CompatibilityCubes: cubes)
        {
            StaticContentVersion = version
        };
    }

    private sealed class RecordingMeshStore : IStaticWorldChunkMeshStore
    {
        public HashSet<StaticWorldChunkMeshKey> LiveKeys { get; } = [];

        public int UploadCount { get; private set; }

        public void Upload(StaticWorldChunkMeshKey key, StaticWorldChunkMesh mesh)
        {
            UploadCount++;
            LiveKeys.Add(key);
        }

        public void Remove(StaticWorldChunkMeshKey key)
        {
            LiveKeys.Remove(key);
        }

        public bool Contains(StaticWorldChunkMeshKey key)
        {
            return LiveKeys.Contains(key);
        }

        public void BeginPass(Render render, Camera camera)
        {
        }

        public void BindMaterial(Render render, int materialSlot)
        {
        }

        public void Draw(Render render, in StaticWorldChunkMeshInfo mesh)
        {
        }

        public void EndPass(Render render)
        {
        }

        public void Clear()
        {
            LiveKeys.Clear();
        }
    }
}
