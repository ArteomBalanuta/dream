using System.Reflection;
using System.Reflection.Emit;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

// GPU-3: the static terrain draws as one batched pass. Each mesh keeps the material slot resolved
// at upload, the visible meshes are sorted by slot, each slot is bound once, and a steady-state
// Draw allocates nothing. A recording mesh store stands in for GL.
public class StaticTerrainBatchedPassTests
{
    private const int GridSize = 48;

    [Fact]
    public void Draw_WithManyMeshesAndMaterials_AllocatesNothing()
    {
        RecordingPassStore store = new() { Record = false };
        using StaticWorldChunkRenderer renderer = CreateRenderer(store);
        Render render = StaticWorldChunkFrustumCullingTests.RenderWithGameProjection();
        Camera camera = OverheadCamera();
        GeometryRenderExport export = CreateMixedMaterialExport();
        DrawUntilApplied(renderer, render, camera, export);

        StaticWorldChunkRenderStats stats = renderer.Draw(render, camera, export);
        Assert.True(stats.MeshCount >= 40, $"{stats.MeshCount} meshes");
        Assert.True(stats.MaterialBinds >= 8, $"{stats.MaterialBinds} material binds");

        long bytes = AllocationAssert.MeasureBytesPerIteration(() => renderer.Draw(render, camera, export));

        Assert.Equal(0, bytes);
    }

    [Fact]
    public void MaterialBinds_EqualTheDistinctMaterialSlotsDrawn()
    {
        RecordingPassStore store = new();
        using StaticWorldChunkRenderer renderer = CreateRenderer(store);
        Render render = StaticWorldChunkFrustumCullingTests.RenderWithGameProjection();
        Camera camera = OverheadCamera();
        GeometryRenderExport export = CreateMixedMaterialExport();
        DrawUntilApplied(renderer, render, camera, export);

        store.Events.Clear();
        StaticWorldChunkRenderStats stats = renderer.Draw(render, camera, export);

        List<int> binds = store.Events.Where(e => e.Kind == PassEventKind.Bind).Select(e => e.Slot).ToList();
        List<StaticWorldChunkMeshInfo> draws = store.Events
            .Where(e => e.Kind == PassEventKind.Draw)
            .Select(e => e.Mesh)
            .ToList();
        int distinctSlots = draws.Select(SlotOf).Distinct().Count();

        Assert.True(distinctSlots > 8, $"{distinctSlots} distinct slots");
        Assert.Equal(distinctSlots, binds.Count);
        Assert.Equal(binds.Count, binds.Distinct().Count());
        Assert.Equal(binds.Count, stats.MaterialBinds);
        Assert.Equal(draws.Count, stats.MeshCount);
        // Grass splits into three roles, each with its own slot and bind.
        Assert.Contains(StaticTerrainMaterialCatalog.ResolveSlot(GeometryMaterialId.Grass, StaticTerrainMaterialRole.Top), binds);
        Assert.Contains(StaticTerrainMaterialCatalog.ResolveSlot(GeometryMaterialId.Grass, StaticTerrainMaterialRole.Side), binds);
        Assert.Contains(StaticTerrainMaterialCatalog.ResolveSlot(GeometryMaterialId.Grass, StaticTerrainMaterialRole.Bottom), binds);
        // One pass per frame, and the material binds per mesh are gone: far fewer binds than meshes.
        Assert.Equal(1, store.Events.Count(e => e.Kind == PassEventKind.Begin));
        Assert.Equal(1, store.Events.Count(e => e.Kind == PassEventKind.End));
        Assert.True(binds.Count < draws.Count, $"{binds.Count} binds for {draws.Count} meshes");
    }

    [Fact]
    public void DrawOrder_GroupsMeshesByMaterial_AndEachDrawUsesTheBoundMaterial()
    {
        RecordingPassStore store = new();
        using StaticWorldChunkRenderer renderer = CreateRenderer(store);
        Render render = StaticWorldChunkFrustumCullingTests.RenderWithGameProjection();
        Camera camera = OverheadCamera();
        GeometryRenderExport export = CreateMixedMaterialExport();
        DrawUntilApplied(renderer, render, camera, export);

        store.Events.Clear();
        renderer.Draw(render, camera, export);

        Assert.Equal(PassEventKind.Begin, store.Events[0].Kind);
        Assert.Equal(PassEventKind.End, store.Events[^1].Kind);
        int boundSlot = -1;
        int previousSlot = -1;
        foreach (PassEvent passEvent in store.Events.Skip(1).SkipLast(1))
        {
            switch (passEvent.Kind)
            {
                case PassEventKind.Bind:
                    // Slots ascend, so each material (and role) is one contiguous group.
                    Assert.True(passEvent.Slot > previousSlot, $"slot {passEvent.Slot} after {previousSlot}");
                    previousSlot = passEvent.Slot;
                    boundSlot = passEvent.Slot;
                    break;
                case PassEventKind.Draw:
                    Assert.Equal(boundSlot, SlotOf(passEvent.Mesh));
                    break;
                default:
                    Assert.Fail($"Unexpected {passEvent.Kind} inside the pass.");
                    break;
            }
        }

        // The drawn meshes' materials appear as contiguous runs.
        List<GeometryMaterialId> materials = store.Events
            .Where(e => e.Kind == PassEventKind.Draw)
            .Select(e => e.Mesh.Key.MaterialId)
            .ToList();
        HashSet<GeometryMaterialId> finished = [];
        for (int i = 1; i < materials.Count; i++)
        {
            if (materials[i] != materials[i - 1])
            {
                Assert.True(finished.Add(materials[i - 1]));
                Assert.DoesNotContain(materials[i], finished);
            }
        }
    }

    [Fact]
    public void NothingVisible_MakesNoPassCalls()
    {
        RecordingPassStore store = new();
        using StaticWorldChunkRenderer renderer = CreateRenderer(store);
        Render render = StaticWorldChunkFrustumCullingTests.RenderWithGameProjection();
        Camera camera = OverheadCamera();
        GeometryRenderExport export = CreateMixedMaterialExport();
        DrawUntilApplied(renderer, render, camera, export);

        // Looking straight up from above the floor: every mesh is behind the camera.
        Camera skyward = new(new Vector3(GridSize / 2f, 20f, GridSize / 2f));
        skyward.SetFacing(new Vector3(0f, 1f, 0.001f));
        store.Events.Clear();
        StaticWorldChunkRenderStats stats = renderer.Draw(render, skyward, export);

        Assert.Equal(0, stats.MeshCount);
        Assert.Equal(0, stats.MaterialBinds);
        Assert.Empty(store.Events);
    }

    [Fact]
    public void ResolveSlot_IsDenseAndOrderedByMaterialThenRole()
    {
        HashSet<int> slots = [];
        int previous = -1;
        foreach (GeometryMaterialId material in Enum.GetValues<GeometryMaterialId>().Order())
        {
            foreach (StaticTerrainMaterialRole role in Enum.GetValues<StaticTerrainMaterialRole>().Order())
            {
                int slot = StaticTerrainMaterialCatalog.ResolveSlot(material, role);
                Assert.InRange(slot, 0, StaticTerrainMaterialCatalog.MaterialSlotCount - 1);
                Assert.True(slot > previous);
                Assert.True(slots.Add(slot));
                previous = slot;
                Assert.Equal(StaticTerrainMaterialCatalog.Resolve(material, role), StaticTerrainMaterialCatalog.GetBySlot(slot));
            }
        }

        Assert.Equal(
            StaticTerrainMaterialCatalog.UnknownMaterialSlot,
            StaticTerrainMaterialCatalog.ResolveSlot((GeometryMaterialId)999, StaticTerrainMaterialRole.Single));
    }

    [Fact]
    public void Resolve_AndResolveRoles_ReturnCachedValues_WithoutAllocating()
    {
        StaticTerrainMaterial first = StaticTerrainMaterialCatalog.Resolve(GeometryMaterialId.Rock);
        StaticTerrainMaterial second = StaticTerrainMaterialCatalog.Resolve(GeometryMaterialId.Rock);
        Assert.Same(first.TexturePath, second.TexturePath);
        Assert.Same(
            StaticTerrainMaterialCatalog.ResolveRoles(GeometryMaterialId.Grass),
            StaticTerrainMaterialCatalog.ResolveRoles(GeometryMaterialId.Grass));

        long bytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            StaticTerrainMaterialCatalog.Resolve(GeometryMaterialId.Grass, StaticTerrainMaterialRole.Side);
            StaticTerrainMaterialCatalog.Resolve(GeometryMaterialId.GrassPath);
            StaticTerrainMaterialCatalog.ResolveRoles(GeometryMaterialId.Grass);
            StaticTerrainMaterialCatalog.ResolveRoles(GeometryMaterialId.Stone);
        });

        Assert.Equal(0, bytes);
    }

    // Structural: nothing reachable by direct calls from the per-frame static draw path calls the
    // catalog's Resolve or ResolveRoles. The material is resolved to a slot at upload instead.
    [Fact]
    public void PerFrameDrawPath_NeverCallsResolve()
    {
        Assembly dream = typeof(StaticWorldChunkRenderer).Assembly;
        Type glStore = dream.GetType("Dream.Rendering.World.GlStaticWorldChunkMeshStore", throwOnError: true)!;
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        List<MethodBase> roots =
        [
            .. typeof(StaticWorldChunkRenderer).GetMethods(All).Where(method => method.Name == nameof(StaticWorldChunkRenderer.Draw)),
            .. glStore.GetMethods(All).Where(method => method.Name is "Contains" or "TryGetDrawHandle" or "BeginPass" or "BindMaterial" or "Draw" or "EndPass"),
        ];
        Assert.Equal(2, roots.Count(method => method.DeclaringType == typeof(StaticWorldChunkRenderer)));
        Assert.Equal(7, roots.Count(method => method.DeclaringType == glStore));

        HashSet<MethodBase> reached = CallGraph.Reachable(roots, dream);

        Assert.Contains(reached, method => method.Name == nameof(Render.BeginStaticTerrainPass));
        Assert.Contains(reached, method => method.Name == nameof(Render.BindStaticTerrainMaterial));
        Assert.Contains(reached, method => method.Name == nameof(StaticTerrainMaterialCatalog.GetBySlot));
        Assert.DoesNotContain(reached, method =>
            method.DeclaringType == typeof(StaticTerrainMaterialCatalog) &&
            method.Name is nameof(StaticTerrainMaterialCatalog.Resolve) or nameof(StaticTerrainMaterialCatalog.ResolveRoles));
        Assert.DoesNotContain(reached, method => method.DeclaringType == typeof(Path));
    }

    private static int SlotOf(StaticWorldChunkMeshInfo mesh) =>
        StaticTerrainMaterialCatalog.ResolveSlot(mesh.Key.MaterialId, mesh.Key.MaterialRole);

    private static Camera OverheadCamera()
    {
        Camera camera = new(new Vector3(GridSize / 2f, 14f, -6f));
        camera.SetFacing(new Vector3(0f, -0.6f, 1f));
        return camera;
    }

    private static StaticWorldChunkRenderer CreateRenderer(IStaticWorldChunkMeshStore store)
    {
        return new StaticWorldChunkRenderer(
            request => Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);
    }

    private static void DrawUntilApplied(StaticWorldChunkRenderer renderer, Render render, Camera camera, GeometryRenderExport export)
    {
        for (int frame = 0; frame < 64; frame++)
        {
            StaticWorldChunkRenderStats stats = renderer.Draw(render, camera, export);
            if (frame > 0 && stats.RebuiltMeshes == 0)
            {
                return;
            }
        }

        Assert.Fail("The static chunk batch was not applied.");
    }

    // A floor of every material, in patches that cross the chunk borders, so each chunk holds
    // several materials and every material spans several chunks. A second layer of stone on part
    // of the floor gives grass side faces next to it.
    private static GeometryRenderExport CreateMixedMaterialExport()
    {
        GeometryMaterialId[] materials = Enum.GetValues<GeometryMaterialId>();
        List<Cube> cubes = [];
        for (int x = 0; x < GridSize; x++)
        {
            for (int z = 0; z < GridSize; z++)
            {
                GeometryMaterialId material = materials[((x / 3) + (z / 5)) % materials.Length];
                cubes.Add(CreateCube(x, 0, z, material));
                if ((x + z) % 7 == 0)
                {
                    cubes.Add(CreateCube(x, 1, z, GeometryMaterialId.Stone));
                }
            }
        }

        return new GeometryRenderExport(
            DirtyCells: [],
            Cells: [],
            Blocks: [],
            CompatibilityCubes: cubes)
        {
            StaticContentVersion = 1
        };
    }

    private static Cube CreateCube(int x, int y, int z, GeometryMaterialId material)
    {
        return new Cube(isEmpty: false, isSolid: true)
        {
            Position = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f),
            MaterialId = material,
            ShaderName = "default"
        };
    }

    private enum PassEventKind
    {
        Begin,
        Bind,
        Draw,
        End
    }

    private readonly record struct PassEvent(PassEventKind Kind, int Slot, StaticWorldChunkMeshInfo Mesh);

    private sealed class RecordingPassStore : IStaticWorldChunkMeshStore
    {
        private readonly HashSet<StaticWorldChunkMeshKey> _live = [];

        public List<PassEvent> Events { get; } = [];

        public bool Record { get; init; } = true;

        public void Upload(StaticWorldChunkMeshKey key, StaticWorldChunkMesh mesh)
        {
            _live.Add(key);
        }

        public void Remove(StaticWorldChunkMeshKey key)
        {
            _live.Remove(key);
        }

        public bool Contains(StaticWorldChunkMeshKey key)
        {
            return _live.Contains(key);
        }

        public void BeginPass(Render render, Camera camera)
        {
            Add(new PassEvent(PassEventKind.Begin, -1, default));
        }

        public void BindMaterial(Render render, int materialSlot)
        {
            Add(new PassEvent(PassEventKind.Bind, materialSlot, default));
        }

        public void Draw(Render render, in StaticWorldChunkMeshInfo mesh)
        {
            Add(new PassEvent(PassEventKind.Draw, -1, mesh));
        }

        public void EndPass(Render render)
        {
            Add(new PassEvent(PassEventKind.End, -1, default));
        }

        public void Clear()
        {
            _live.Clear();
        }

        private void Add(PassEvent passEvent)
        {
            if (Record)
            {
                Events.Add(passEvent);
            }
        }
    }

    // Walks the IL of the given methods and every method of the same assembly they call directly
    // (call, callvirt, newobj). Interface and virtual calls resolve to the declared method only.
    private static class CallGraph
    {
        private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => code.Value);

        public static HashSet<MethodBase> Reachable(IEnumerable<MethodBase> roots, Assembly assembly)
        {
            HashSet<MethodBase> reached = [];
            Stack<MethodBase> pending = new(roots);
            while (pending.Count > 0)
            {
                MethodBase method = pending.Pop();
                if (!reached.Add(method))
                {
                    continue;
                }

                foreach (MethodBase callee in DirectCallees(method))
                {
                    // Calls into other assemblies are recorded but not walked.
                    if (callee.Module.Assembly != assembly)
                    {
                        reached.Add(callee);
                        continue;
                    }

                    pending.Push(callee);
                }
            }

            return reached;
        }

        private static IEnumerable<MethodBase> DirectCallees(MethodBase method)
        {
            byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null)
            {
                yield break;
            }

            Type[]? typeArguments = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
            Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            int position = 0;
            while (position < il.Length)
            {
                short value = il[position] == 0xFE
                    ? (short)(0xFE00 | il[position + 1])
                    : il[position];
                position += il[position] == 0xFE ? 2 : 1;
                OpCode code = OpCodesByValue[value];
                if (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj)
                {
                    int token = BitConverter.ToInt32(il, position);
                    MethodBase? callee = null;
                    try
                    {
                        callee = method.Module.ResolveMethod(token, typeArguments, methodArguments);
                    }
                    catch (ArgumentException)
                    {
                    }

                    if (callee != null)
                    {
                        yield return callee;
                    }
                }

                position += OperandSize(code, il, position);
            }
        }

        private static int OperandSize(OpCode code, byte[] il, int position)
        {
            return code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, position) * 4),
                _ => 4
            };
        }
    }
}
