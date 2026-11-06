using System.Runtime.CompilerServices;
using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Rendering.Core;
using Dream.Rendering.Ground;
using Dream.Rendering.Lighting;
using Dream.Rendering.Models;
using Dream.Rendering.Props;
using Dream.Rendering.Shadows;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Rendering.Ground;
using OpenTK.Mathematics;
using Node = SharpGLTF.Schema2.Node;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Rendering.Shadows;

// The cost of the caster pass, MEASURED rather than estimated, on the shipped content: the same culling predicates the pass
// uses, run over the real scatter and prop layout of the enriched startup terrain, with each model's draw calls counted from
// its decoded glTF (a node's primitives are one draw each, MASK primitives are skipped by the depth-only draw, an instanced
// asset is one draw per primitive however many instances). Terrain meshes are counted by running StaticWorldChunkRenderer.DrawDepth
// itself against a recording store. The numbers are printed; the assertions only guard the relations the design relies on.
public class ShadowCasterCostMeasurementTests
{
    private const int WorldSize = 56;
    private readonly ITestOutputHelper _output;

    public ShadowCasterCostMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly Dictionary<string, (int All, int NoMask)> DrawCache = new();

    // Draw calls one instance of a model costs: every primitive of every renderable node, with and without the MASK ones.
    private static (int All, int NoMask) DrawsPerInstance(string path)
    {
        if (DrawCache.TryGetValue(path, out (int, int) cached))
        {
            return cached;
        }

        AnimatedModelData data = AnimatedModelData.Load(path);
        int all = 0;
        int noMask = 0;
        for (int i = 0; i < data.Pose.RenderableNodeCount; i++)
        {
            Node node = data.Pose.GetRenderableNode(i);
            if (node.Mesh == null)
            {
                continue;
            }

            foreach (AnimatedModelPrimitiveData primitive in data.MeshPrimitives[node.Mesh.LogicalIndex])
            {
                bool mask = primitive.MaterialIndex >= 0
                    && primitive.MaterialIndex < data.Materials.Count
                    && AnimatedModel.UsesAlphaMaskProgram(data.Materials[primitive.MaterialIndex].AlphaMode);
                all++;
                if (!mask)
                {
                    noMask++;
                }
            }
        }

        return DrawCache[path] = (all, noMask);
    }

    private static ViewFrustum IsoFrustum(Vector3 focus)
    {
        Camera camera = new(Vector3.Zero);
        IsometricView.ResolveInto(focus, camera);
        return ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), IsometricView.CreateProjection(1440f / 884f));
    }

    private static Vector3 Light(float elevationDegrees, float azimuthDegrees)
    {
        float e = MathHelper.DegreesToRadians(elevationDegrees);
        float a = MathHelper.DegreesToRadians(azimuthDegrees);
        return new Vector3(MathF.Cos(e) * MathF.Cos(a), MathF.Sin(e), MathF.Cos(e) * MathF.Sin(a));
    }

    // Props: the visible pass draws every primitive of every visible placement (non-instanced); the caster skips MASK.
    private static (int Placements, int Draws) PropDraws(PropIndex props, Func<PropPlacement, bool> accepted, bool depth)
    {
        int placements = 0;
        int draws = 0;
        foreach (PropPlacement p in props.Placements)
        {
            if (!accepted(p))
            {
                continue;
            }

            placements++;
            (int all, int noMask) = DrawsPerInstance(PropCatalog.ModelPath(p.Kind));
            draws += depth ? noMask : all;
        }

        return (placements, draws);
    }

    // Scatter: one instanced draw per primitive per asset that has any accepted instance.
    private static (int Instances, int Draws) ScatterDraws(GroundScatterIndex index, Func<GroundPlacement, GroundAsset, bool> accepted, Func<GroundScatterIndex.Cell, bool> cellAccepted, bool depth)
    {
        IReadOnlyList<GroundAsset> assets = index.Assets.Assets;
        int instances = 0;
        int draws = 0;
        int[] perAsset = new int[assets.Count];
        foreach (GroundScatterIndex.Block block in index.Blocks)
        {
            if (!cellAccepted(index.Cells[block.Cell]))
            {
                continue;
            }

            for (int i = block.Start; i < block.Start + block.Count; i++)
            {
                GroundPlacement p = index.Placements[i];
                GroundAsset asset = assets[p.Asset];
                if (depth && asset.Layer == GroundLayer.Decal)
                {
                    continue;
                }

                if (accepted(p, asset))
                {
                    perAsset[p.Asset]++;
                    instances++;
                }
            }
        }

        for (int a = 0; a < assets.Count; a++)
        {
            if (perAsset[a] > 0)
            {
                (int all, int noMask) = DrawsPerInstance(assets[a].ModelPath);
                draws += depth ? noMask : all;
            }
        }

        return (instances, draws);
    }

    private sealed class RecordingStore : IStaticWorldChunkMeshStore
    {
        public Dictionary<StaticWorldChunkMeshKey, StaticWorldChunkBounds> Live { get; } = new();

        public int DepthDraws { get; private set; }

        public void Reset() => DepthDraws = 0;

        public void Upload(StaticWorldChunkMeshKey key, StaticWorldChunkMesh mesh)
        {
            if (mesh.ExposedFaceCount > 0)
            {
                Live[key] = mesh.Bounds;
            }
            else
            {
                Live.Remove(key);
            }
        }

        public void Remove(StaticWorldChunkMeshKey key) => Live.Remove(key);

        public bool Contains(StaticWorldChunkMeshKey key) => Live.ContainsKey(key);

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

        public void DrawDepth(Render render, in StaticWorldChunkMeshInfo mesh, object? handle) => DepthDraws++;

        public void Clear() => Live.Clear();
    }

    [Fact]
    public void Measured_Draw_Calls_Of_The_Sun_Pass_And_Per_Fire_Face_Against_The_Visible_Pass()
    {
        Vector3 focus = new(WorldSize / 2f, 1.5f, WorldSize / 2f);
        (PropIndex props, StaticGround ground) = GroundTestSets.World(WorldSize);
        GroundAssetSet assets = GroundAssetSet.LoadShipped();
        Assert.NotEmpty(assets.Assets);
        GroundScatterIndex scatter = new();
        scatter.Update(props, assets);

        // The visible pass at the isometric camera.
        ViewFrustum iso = IsoFrustum(focus);
        Func<PropPlacement, bool> propVisible = p => PropRenderer.IsVisible(p, focus, in iso);
        (int visibleProps, int visiblePropDraws) = PropDraws(props, propVisible, depth: false);
        (int visibleScatter, int visibleScatterDraws) = ScatterDraws(
            scatter,
            (p, a) => GroundScatterRenderer.IsVisible(in p, a, focus, in iso),
            cell => GroundScatterRenderer.CellVisible(in cell, GroundScatterRenderer.LandmarkDrawDistance, focus, in iso),
            depth: false);

        // The terrain, for the visible mesh count and then each caster target.
        RecordingStore store = new();
        using StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);
        Render render = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
        typeof(Render).GetField("_projection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(render, IsometricView.CreateProjection(1440f / 884f));
        Map map = new(physics: null, generateStartupTerrain: true, width: WorldSize, depth: WorldSize);
        GeometryRenderExport export = map.CapturePresentationGeometryRenderExport(focus, 400f);
        Camera isoCamera = new(Vector3.Zero);
        IsometricView.ResolveInto(focus, isoCamera);
        StaticWorldChunkRenderStats visibleTerrain = default;
        for (int frame = 0; frame < 64; frame++)
        {
            visibleTerrain = renderer.Draw(render, isoCamera, export);
            if (frame > 0 && visibleTerrain.RebuiltMeshes == 0)
            {
                break;
            }
        }

        _output.WriteLine($"scene: startup terrain {WorldSize}x{WorldSize}, {store.Live.Count} terrain meshes, {props.Placements.Count} props, {scatter.Placements.Count} scatter placements");
        _output.WriteLine($"VISIBLE PASS (iso camera): terrain {visibleTerrain.MeshCount} draws, props {visibleProps} placements = {visiblePropDraws} draws, scatter {visibleScatter} instances = {visibleScatterDraws} draws; total {visibleTerrain.MeshCount + visiblePropDraws + visibleScatterDraws}");

        int bestSunTotal = 0;
        foreach (float elevation in new[] { 65f, 40f, 20f })
        {
            ShadowFrame sun = ShadowFrame.ForSun(focus, Light(elevation, 35f), MathF.Sin(MathHelper.DegreesToRadians(elevation)), 2048, 0f);
            ViewFrustum frustum = ShadowCasterSelection.SunFrustum(in sun.SunLightViewProjection);
            Matrix4 identity = Matrix4.Identity;
            ShadowCasterView view = new(in identity, in sun.SunLightViewProjection, in frustum, focus);
            store.Reset();
            renderer.DrawDepth(render, in view);
            int terrain = store.DepthDraws;
            (int props_, int propDraws) = PropDraws(props, p =>
            {
                PropRenderer.CasterBounds(in p, out Vector3 min, out Vector3 max, out float reach);
                return view.Accepts(min, max, reach);
            }, depth: true);
            (int instances, int scatterDraws) = ScatterDraws(
                scatter,
                (p, a) =>
                {
                    GroundScatterRenderer.CasterBounds(in p, a, out Vector3 min, out Vector3 max, out float reach);
                    return view.Accepts(min, max, reach);
                },
                cell => GroundScatterRenderer.CellVisible(in cell, GroundScatterRenderer.LandmarkDrawDistance, focus, in frustum),
                depth: true);
            int total = terrain + propDraws + scatterDraws;
            bestSunTotal = Math.Max(bestSunTotal, total);
            _output.WriteLine($"SUN PASS (elevation {elevation} deg): terrain {terrain} draws, props {props_} placements = {propDraws} draws, scatter {instances} instances = {scatterDraws} draws; total {total} (static scene, no actors)");
        }

        // Fires: the placed braziers, flickered, through the real selection.
        PlacedLightIndex placed = new();
        LightCollector collector = new();
        placed.Collect(export, focus, collector, sunIntensity: 0f, clockSeconds: 3.7);
        LightList list = default;
        Camera cam = isoCamera;
        ViewFrustum selectFrustum = ViewFrustum.FromViewAndProjection(cam.GetViewMatrix(), IsometricView.CreateProjection(1440f / 884f));
        LightSelection.SelectNearest(collector.Lights, focus, in selectFrustum, ref list);
        ShadowFrame fires = ShadowFrame.None;
        FireShadowAtlas.FillSlots(in list, focus, ShadowSettings.CastingFireCount, 0f, ref fires);
        _output.WriteLine($"fires: {collector.Count} lights collected, {list.Count} selected, {fires.FireCount} casting");

        int fireTotal = 0;
        int faces = 0;
        int busiestFace = 0;
        for (int slot = 0; slot < fires.FireCount; slot++)
        {
            FireShadowSlot fire = fires.Fires[slot];
            Matrix4 projection = FireShadowAtlas.FaceProjection(fire.Near, fire.Far);
            int slotTotal = 0;
            for (int face = 0; face < FireShadowAtlas.FaceCount; face++)
            {
                Matrix4 faceView = FireShadowAtlas.FaceView(fire.Position, face);
                ViewFrustum frustum = ShadowCasterSelection.FireFaceFrustum(in faceView, in projection);
                ShadowCasterView view = new(in faceView, in projection, in frustum, focus);
                store.Reset();
                renderer.DrawDepth(render, in view);
                int terrain = store.DepthDraws;
                (_, int propDraws) = PropDraws(props, p =>
                {
                    PropRenderer.CasterBounds(in p, out Vector3 min, out Vector3 max, out float reach);
                    return view.Accepts(min, max, reach);
                }, depth: true);
                (_, int scatterDraws) = ScatterDraws(
                    scatter,
                    (p, a) =>
                    {
                        GroundScatterRenderer.CasterBounds(in p, a, out Vector3 min, out Vector3 max, out float reach);
                        return view.Accepts(min, max, reach);
                    },
                    cell => GroundScatterRenderer.CellVisible(in cell, GroundScatterRenderer.LandmarkDrawDistance, focus, in frustum),
                    depth: true);
                int total = terrain + propDraws + scatterDraws;
                slotTotal += total;
                faces++;
                busiestFace = Math.Max(busiestFace, total);
                _output.WriteLine($"  fire {slot} ({fire.Position.X:F1},{fire.Position.Y:F1},{fire.Position.Z:F1}) far {fire.Far:F1} face {face}: terrain {terrain}, props {propDraws}, scatter {scatterDraws} = {total}");
            }

            fireTotal += slotTotal;
        }

        _output.WriteLine($"FIRE FACES: {faces} faces, {fireTotal} draws total, {(faces == 0 ? 0 : (float)fireTotal / faces):F1} per face, busiest face {busiestFace}");

        // Where the fire-face draws go, by layer: the scatter draws split into cover / accent / landmark (knobs for the owner).
        int[] byLayer = new int[4];
        for (int slot = 0; slot < fires.FireCount; slot++)
        {
            FireShadowSlot fire = fires.Fires[slot];
            Matrix4 projection = FireShadowAtlas.FaceProjection(fire.Near, fire.Far);
            for (int face = 0; face < FireShadowAtlas.FaceCount; face++)
            {
                Matrix4 faceView = FireShadowAtlas.FaceView(fire.Position, face);
                ViewFrustum frustum = ShadowCasterSelection.FireFaceFrustum(in faceView, in projection);
                ShadowCasterView view = new(in faceView, in projection, in frustum, focus);
                foreach (GroundLayer layer in new[] { GroundLayer.Cover, GroundLayer.Accent, GroundLayer.Landmark })
                {
                    byLayer[(int)layer] += ScatterDraws(
                        scatter,
                        (p, a) =>
                        {
                            if (a.Layer != layer)
                            {
                                return false;
                            }

                            GroundScatterRenderer.CasterBounds(in p, a, out Vector3 min, out Vector3 max, out float reach);
                            return view.Accepts(min, max, reach);
                        },
                        cell => GroundScatterRenderer.CellVisible(in cell, GroundScatterRenderer.LandmarkDrawDistance, focus, in frustum),
                        depth: true).Draws;
                }
            }
        }

        _output.WriteLine($"FIRE FACES scatter draws by layer over all {faces} faces: cover {byLayer[(int)GroundLayer.Cover]}, accent {byLayer[(int)GroundLayer.Accent]}, landmark {byLayer[(int)GroundLayer.Landmark]}");

        // The CPU cost of the culling alone (no GL): the static chunk walk through DrawDepth plus the prop and scatter predicates,
        // over the sun target and all sixteen fire faces, averaged. The GL submission of the draws above is on top of this.
        ShadowFrame timedSun = ShadowFrame.ForSun(focus, Light(40f, 35f), MathF.Sin(MathHelper.DegreesToRadians(40f)), 2048, 0f);
        List<ShadowCasterView> targets = new();
        ViewFrustum sunFrustum = ShadowCasterSelection.SunFrustum(in timedSun.SunLightViewProjection);
        Matrix4 sunView = Matrix4.Identity;
        targets.Add(new ShadowCasterView(in sunView, in timedSun.SunLightViewProjection, in sunFrustum, focus));
        for (int slot = 0; slot < fires.FireCount; slot++)
        {
            FireShadowSlot fire = fires.Fires[slot];
            Matrix4 projection = FireShadowAtlas.FaceProjection(fire.Near, fire.Far);
            for (int face = 0; face < FireShadowAtlas.FaceCount; face++)
            {
                Matrix4 faceView = FireShadowAtlas.FaceView(fire.Position, face);
                ViewFrustum frustum = ShadowCasterSelection.FireFaceFrustum(in faceView, in projection);
                targets.Add(new ShadowCasterView(in faceView, in projection, in frustum, focus));
            }
        }

        int sink = 0;
        void GatherAll()
        {
            foreach (ShadowCasterView view in targets)
            {
                store.Reset();
                renderer.DrawDepth(render, in view);
                sink += store.DepthDraws;
                sink += PropDraws(props, p =>
                {
                    PropRenderer.CasterBounds(in p, out Vector3 min, out Vector3 max, out float reach);
                    return view.Accepts(min, max, reach);
                }, depth: true).Draws;
            }
        }

        for (int warm = 0; warm < 20; warm++)
        {
            GatherAll();
        }

        System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
        const int Iterations = 200;
        for (int i = 0; i < Iterations; i++)
        {
            GatherAll();
        }

        timer.Stop();
        _output.WriteLine($"CPU culling for {targets.Count} targets (terrain walk + prop predicates, {sink > 0}): {timer.Elapsed.TotalMilliseconds / Iterations:F3} ms per frame");

        // Actors: one instance's draws (the sun pass and every face in range draw each actor once).
        foreach ((string name, string path) in new[]
        {
            ("player", PlayerModelAssetDefinition.ModelPath),
            ("wolf", WolfAssetDefinition.ModelPath),
            ("npc vendor", NpcAssetDefinition.VendorModelPath),
        })
        {
            (int all, int noMask) = DrawsPerInstance(path);
            _output.WriteLine($"ACTOR {name}: {all} draws visible, {noMask} depth-only per target it is in");
        }

        Assert.True(bestSunTotal > 0);
    }

    [Fact]
    public void The_Flicker_Reaches_The_Slots_From_Real_Collected_Lights_Frame_To_Frame()
    {
        Vector3 focus = new(WorldSize / 2f, 1.5f, WorldSize / 2f);
        Map map = new(physics: null, generateStartupTerrain: true, width: WorldSize, depth: WorldSize);
        GeometryRenderExport export = map.CapturePresentationGeometryRenderExport(focus, 400f);
        PlacedLightIndex placed = new();
        Camera camera = new(Vector3.Zero);
        IsometricView.ResolveInto(focus, camera);
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), IsometricView.CreateProjection(1440f / 884f));

        // Frames 1/60 s apart, the way the scene collects: Collect applies FireFlicker at collection time.
        List<FireShadowSlot> frames = new();
        for (int frameIndex = 0; frameIndex < 12; frameIndex++)
        {
            LightCollector collector = new();
            placed.Collect(export, focus, collector, sunIntensity: 0f, clockSeconds: 10.0 + frameIndex / 60.0);
            LightList list = default;
            LightSelection.SelectNearest(collector.Lights, focus, in frustum, ref list);
            ShadowFrame frame = ShadowFrame.None;
            FireShadowAtlas.FillSlots(in list, focus, ShadowSettings.CastingFireCount, 0f, ref frame);
            Assert.True(frame.FireCount > 0, "the startup terrain has no brazier in range of its centre");
            frames.Add(frame.Fires[0]);
        }

        // The same fire in the same slot (the ordering is stable), and its origin and far plane MOVE from frame to frame.
        Assert.All(frames, f => Assert.Equal(frames[0].LightIndex, f.LightIndex));
        int movedPosition = 0;
        int movedFar = 0;
        for (int i = 1; i < frames.Count; i++)
        {
            movedPosition += frames[i].Position != frames[i - 1].Position ? 1 : 0;
            movedFar += frames[i].Far != frames[i - 1].Far ? 1 : 0;
        }

        float spread = frames.Max(f => f.Far) - frames.Min(f => f.Far);
        float travel = frames.Max(f => f.Position.X) - frames.Min(f => f.Position.X);
        _output.WriteLine($"flicker over {frames.Count} frames at 60 Hz: origin moved on {movedPosition}/{frames.Count - 1} frames, far plane on {movedFar}/{frames.Count - 1}; far spread {spread:F3}, x travel {travel:F3}");
        Assert.True(movedPosition >= frames.Count - 2, "the slot position follows the flame");
        Assert.True(movedFar >= frames.Count - 2, "the far plane breathes with the radius");
        Assert.True(spread > 0f && spread < 3f, "bounded: the radius scale is at most a few percent");
    }
}
