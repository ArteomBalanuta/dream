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

// BH-TERRAIN-2: a mesh signature means "the GPU holds a mesh built from this signature", so it must
// not be recorded until the upload has actually happened. Recorded first, a single failed upload
// left a permanent hole: Draw found no buffer and every later build skipped the chunk against the
// cached signature. CPU-side state only; a mesh store that throws stands in for a failing GL upload.
public class TerrainStaticChunkUploadFailureTests
{
    private static readonly Render UnusedRender = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
    private static readonly Camera TestCamera = new(Vector3.Zero);

    [Fact]
    public void FailedUpload_IsRetried_NotRememberedAsUploaded()
    {
        Vector3 position = new(0.5f, 0.5f, 0.5f);
        ThrowingMeshStore store = new(failUploads: 1);
        using StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);

        GeometryRenderExport export = CreateExport(version: 1, position);
        // The upload throws on the frame the batch is applied; that frame still fails, as it does
        // today. What must not happen is the renderer remembering the mesh as uploaded.
        Assert.Throws<InvalidOperationException>(() => DrawUntilSettled(renderer, export));
        Assert.Empty(store.LiveKeys);
        Assert.Empty(ResidentSignatureKeys(renderer));

        // A later content change must rebuild the chunk the failed upload left empty, even though
        // none of its own cells was edited.
        DrawUntilSettled(renderer, CreateExport(version: 2, position));

        Assert.NotEmpty(store.LiveKeys);
        Assert.True(store.UploadCount >= 2, $"uploads: {store.UploadCount}");
        Assert.Contains(StaticWorldChunkKey.FromWorldPosition(position), store.LiveKeys.Select(key => key.ChunkKey));
    }

    private static IEnumerable<StaticWorldChunkMeshKey> ResidentSignatureKeys(StaticWorldChunkRenderer renderer)
    {
        Dictionary<StaticWorldChunkMeshKey, int> signatures = (Dictionary<StaticWorldChunkMeshKey, int>)typeof(StaticWorldChunkRenderer)
            .GetField("_signatures", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(renderer)!;
        return signatures.Keys.ToArray();
    }

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

    // Fails the first few uploads, the way an out-of-memory GPU upload would.
    private sealed class ThrowingMeshStore(int failUploads) : IStaticWorldChunkMeshStore
    {
        private int _remainingFailures = failUploads;

        public HashSet<StaticWorldChunkMeshKey> LiveKeys { get; } = [];

        public int UploadCount { get; private set; }

        public void Upload(StaticWorldChunkMeshKey key, StaticWorldChunkMesh mesh)
        {
            UploadCount++;
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                throw new InvalidOperationException("Simulated GPU upload failure.");
            }

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
