using System.Runtime.CompilerServices;
using Dream.Rendering.Core;
using Dream.Rendering.Ground;
using Dream.Rendering.Props;
using Dream.Rendering.Shadows;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Rendering.Core;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Rendering.Shadows;

// Which casters the shadow pass draws. Pure maths against ViewFrustum, plus the static chunk walk against a recording mesh
// store (no GL): the culling decision is the part of the pass that can be wrong without a single GL error.
public class ShadowCasterSelectionTests
{
    private const int Resolution = 2048;
    private readonly ITestOutputHelper _output;

    public ShadowCasterSelectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static Vector3 Light(float elevationDegrees, float azimuthDegrees)
    {
        float e = MathHelper.DegreesToRadians(elevationDegrees);
        float a = MathHelper.DegreesToRadians(azimuthDegrees);
        return new Vector3(MathF.Cos(e) * MathF.Cos(a), MathF.Sin(e), MathF.Cos(e) * MathF.Sin(a));
    }

    private static Vector3 Box(Vector3 centre, out Vector3 max)
    {
        max = centre + new Vector3(0.25f);
        return centre - new Vector3(0.25f);
    }

    [Fact]
    public void A_Box_Inside_The_Frustum_And_The_Distance_Is_Accepted()
    {
        Vector3 focus = new(10f, 0f, 10f);
        ShadowFrame frame = ShadowFrame.ForSun(focus, Light(60f, 30f), MathF.Sin(MathHelper.DegreesToRadians(60f)), Resolution, 0f);
        ViewFrustum frustum = ShadowCasterSelection.SunFrustum(in frame.SunLightViewProjection);

        Vector3 min = Box(focus + new Vector3(2f, 1f, 1f), out Vector3 max);

        Assert.True(ShadowCasterSelection.Accepts(min, max, focus, in frustum));
    }

    [Fact]
    public void A_Box_Outside_The_Light_Frustum_Is_Rejected_Even_Next_To_The_Focus()
    {
        Vector3 focus = new(10f, 0f, 10f);
        Vector3 toLight = Light(60f, 30f);
        ShadowFrame frame = ShadowFrame.ForSun(focus, toLight, MathF.Sin(MathHelper.DegreesToRadians(60f)), Resolution, 0f);
        ViewFrustum frustum = ShadowCasterSelection.SunFrustum(in frame.SunLightViewProjection);

        // 30 units sideways in light space: outside the 47-unit-wide ortho slab (half 23.5), yet within the 48 m distance.
        Vector3 side = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toLight));
        Vector3 min = Box(focus + side * 30f, out Vector3 max);

        Assert.True(ShadowCasterSelection.HorizontalDistanceSquaredToBox(min, max, focus) < ShadowCasterSelection.CasterDistance * ShadowCasterSelection.CasterDistance);
        Assert.False(ShadowCasterSelection.Accepts(min, max, focus, in frustum));
    }

    [Fact]
    public void A_Box_Inside_The_Frustum_But_Past_The_Caster_Distance_Is_Rejected()
    {
        // A frustum that holds everything, so only the distance term can reject.
        Matrix4 huge = Matrix4.CreateOrthographicOffCenter(-500f, 500f, -500f, 500f, -500f, 500f);
        ViewFrustum frustum = ShadowCasterSelection.SunFrustum(in huge);
        Vector3 focus = Vector3.Zero;

        Vector3 nearMin = Box(new Vector3(ShadowCasterSelection.CasterDistance - 1f, 0f, 0f), out Vector3 nearMax);
        Vector3 farMin = Box(new Vector3(ShadowCasterSelection.CasterDistance + 1f, 0f, 0f), out Vector3 farMax);

        Assert.True(frustum.IntersectsAabb(farMin, farMax));
        Assert.True(ShadowCasterSelection.Accepts(nearMin, nearMax, focus, in frustum));
        Assert.False(ShadowCasterSelection.Accepts(farMin, farMax, focus, in frustum));
    }

    [Fact]
    public void The_Distance_Is_Measured_To_The_Box_Not_Its_Centre_And_Ignores_Height()
    {
        Vector3 min = new(40f, -100f, -1f);
        Vector3 max = new(60f, 100f, 1f);

        // The box reaches within 40 on the ground plane of the origin; its height is irrelevant.
        Assert.Equal(40f * 40f, ShadowCasterSelection.HorizontalDistanceSquaredToBox(min, max, Vector3.Zero), 3);
        Assert.Equal(0f, ShadowCasterSelection.HorizontalDistanceSquaredToBox(min, max, new Vector3(50f, 500f, 0f)));
    }

    [Fact]
    public void The_Distance_Counts_Both_Ground_Axes()
    {
        Vector3 min = new(-1f, 0f, 60f);
        Vector3 max = new(1f, 1f, 62f);
        Matrix4 huge = Matrix4.CreateOrthographicOffCenter(-500f, 500f, -500f, 500f, -500f, 500f);
        ViewFrustum frustum = ShadowCasterSelection.SunFrustum(in huge);

        Assert.Equal(60f * 60f, ShadowCasterSelection.HorizontalDistanceSquaredToBox(min, max, Vector3.Zero), 3);
        Assert.False(ShadowCasterSelection.Accepts(min, max, Vector3.Zero, in frustum));
        Assert.Equal(25f, ShadowCasterSelection.HorizontalDistanceSquaredToBox(new Vector3(3f, 0f, 4f), new Vector3(3f, 1f, 4f), Vector3.Zero), 4);
    }

    [Fact]
    public void Every_Caster_Entry_Culls_Against_The_Targets_View_Before_It_Draws()
    {
        // The GL half cannot run here, so the order is pinned on the source: in each entry the acceptance test comes first and a
        // rejected caster never reaches a draw.
        (string File, string Anchor, string Cull, string Draw)[] entries =
        [
            ("Rendering/Shadows/ShadowCasterPass.cs", "public int Draw(Render render", "view.Accepts(min, max)", "BeginDepthRender("),
            ("Rendering/World/WorldRenderer.cs", "public int DrawCasters(", "view.Accepts(cube.Position - half, cube.Position + half)", "DrawCubeBatches("),
            ("Rendering/World/WorldRenderer.cs", "public int DrawCasters(", "view.Accepts(block.Center - half, block.Center + half)", "DrawGeometryBlockBatches("),
            ("Rendering/Props/PropRenderer.cs", "public int DrawCasters(", "view.Accepts(min, max, reach)", "BeginDepthRender("),
            ("Rendering/Ground/GroundScatterRenderer.cs", "public int DrawCasters(", "view.Accepts(min, max, reach)", "BeginDepthRender("),
            ("Rendering/Core/Render.GroundItems.cs", "internal int DrawShadowGroundItems(", "view.Accepts(box.Center - half, box.Center + half)", "DrawBatched("),
        ];
        foreach ((string file, string anchor, string cull, string draw) in entries)
        {
            string source = System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(Path.Combine(RepoRoot(), "dream", Path.Combine(file.Split('/')))), @"//[^\r\n]*", string.Empty);
            string body = source[source.IndexOf(anchor, StringComparison.Ordinal)..];
            int cullAt = body.IndexOf(cull, StringComparison.Ordinal);
            int drawAt = body.IndexOf(draw, StringComparison.Ordinal);
            Assert.True(cullAt >= 0, $"{file}: no cull");
            Assert.True(drawAt >= 0, $"{file}: no draw");
            Assert.True(cullAt < drawAt, $"{file}: the draw comes before the cull");
            // The cull gates the draw: what follows it is an if-continue (or the if body that draws), never a bare statement.
            int from = Math.Max(0, cullAt - 6);
            Assert.Matches(@"if \(!?$", body[from..cullAt]);
        }
    }

    [Fact]
    public void The_Actor_Renderers_Clear_Their_Recordings_At_The_Start_Of_Every_Draw()
    {
        foreach ((string file, string draw) in new[]
        {
            ("Rendering/Actors/EnemyRenderer.cs", "public void DrawAtRenderTick("),
            ("Rendering/Actors/LocalPlayerRenderer.cs", "public void Draw(LocalPlayerPresentationState"),
            ("Rendering/Actors/RemotePlayerRenderer.cs", "public RemotePlayerRenderStats Draw("),
            ("Rendering/Actors/NpcModelRenderer.cs", "public void Draw(Camera camera, ClientNpcCache"),
        })
        {
            string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", Path.Combine(file.Split('/'))));
            string body = source[source.IndexOf(draw, StringComparison.Ordinal)..];
            int clear = body.IndexOf("_casters.Clear();", StringComparison.Ordinal);
            int add = body.IndexOf("_casters.Add(", StringComparison.Ordinal);
            Assert.True(clear >= 0 && add > clear, $"{file}: the list is cleared before anything is added");
        }
    }

    [Fact]
    public void The_Static_Walk_Does_Not_Keep_Mesh_Handles_Past_The_Pass()
    {
        RecordingStore store = new();
        using StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);
        Render render = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
        GeometryRenderExport export = CaptureStartupExport(out Vector3 focus);
        DrawUntilApplied(renderer, render, new Camera(focus), export);
        store.Reset();
        Matrix4 huge = Matrix4.CreateOrthographicOffCenter(-500f, 500f, -500f, 500f, -500f, 500f);
        Matrix4 identity = Matrix4.Identity;
        ViewFrustum frustum = ShadowCasterSelection.SunFrustum(in huge);
        ShadowCasterView view = new(in identity, in huge, in frustum, focus);

        Assert.True(renderer.DrawDepth(render, in view) > 0);

        // The handles are only valid for the pass; a later sync can remove the buffers they name.
        object?[] handles = (object?[])typeof(StaticWorldChunkRenderer)
            .GetField("_visibleHandles", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(renderer)!;
        Assert.True(store.DepthDrawn.Count > 0);
        Assert.All(handles, h => Assert.Null(h));
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void A_Caster_Behind_The_Camera_Is_Accepted_Though_The_Visible_Pass_Culls_It()
    {
        // The camera at the origin looking +X; the box 12 units behind it. The visible pass's chunk cull rejects it (that is the
        // camera-direction half this selection deliberately does not reuse); the sun frustum, centred on the focus, takes it.
        Camera camera = new(Vector3.Zero);
        camera.SetFacing(Vector3.UnitX);
        ViewFrustum cameraFrustum = ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), ViewFrustumTests.GameProjection());
        StaticWorldChunkBounds behind = new(new Vector3(-13f, 0f, -1f), new Vector3(-11f, 2f, 1f));
        Assert.False(StaticWorldChunkRenderer.IsChunkVisible(camera.Position, camera.Front, in cameraFrustum, behind));

        Vector3 focus = new(0f, 0f, 0f);
        ShadowFrame frame = ShadowFrame.ForSun(focus, Light(70f, 200f), MathF.Sin(MathHelper.DegreesToRadians(70f)), Resolution, 0f);
        ViewFrustum sun = ShadowCasterSelection.SunFrustum(in frame.SunLightViewProjection);

        Assert.True(ShadowCasterSelection.Accepts(behind.Min, behind.Max, focus, in sun));
    }

    [Fact]
    public void A_Tall_Caster_Beyond_The_Receiver_Box_Still_Casts_Into_It_Through_The_Pulled_Back_Near_Plane()
    {
        // The guard against culling with the receiver box: a caster toward the light, past the half-depth of the receiver slab,
        // lies outside the receiver region yet casts INTO it. Build pulls the near plane back by CasterExtensionFor and the
        // frustum must carry that.
        Vector3 focus = new(8f, 0f, 8f);
        float elevation = 53f; // sin 0.8: an extension of 32.5 units, whose reach stays within the 48 m distance term
        Vector3 toLight = Light(elevation, 120f);
        float sin = MathF.Sin(MathHelper.DegreesToRadians(elevation));
        ShadowFrame frame = ShadowFrame.ForSun(focus, toLight, sin, Resolution, 0f);
        ViewFrustum frustum = ShadowCasterSelection.SunFrustum(in frame.SunLightViewProjection);

        Vector3 centre = focus + SunShadowVolume.ViewForwardGround * ShadowSettings.ReceiverCentreOffset;
        float half = ShadowSettings.OrthoSide * 0.5f;
        float extension = ShadowSettings.CasterExtensionFor(sin);
        Assert.True(extension > 10f);

        // Inside the extension: outside the receiver slab (centre + toLight * d, d > half), inside the pulled-back frustum.
        Vector3 inside = Box(centre + toLight * (half + extension - 2f), out Vector3 insideMax);
        Vector3 beyond = Box(centre + toLight * (half + extension + 4f), out Vector3 beyondMax);

        Assert.True(Vector3.Dot(inside + new Vector3(0.25f) - centre, toLight) > half);
        Assert.True(ShadowCasterSelection.Accepts(inside, insideMax, focus, in frustum));
        Assert.False(ShadowCasterSelection.Accepts(beyond, beyondMax, focus, in frustum));

        // And a frustum built WITHOUT the extension (what a receiver-box cull amounts to) would have cut it.
        float centreDistance = -Vector3.TransformPosition(centre, Matrix4.LookAt(toLight * ShadowSettings.OrthoSide, Vector3.Zero, Vector3.UnitY)).Z;
        Matrix4 view = Matrix4.LookAt(toLight * ShadowSettings.OrthoSide, Vector3.Zero, Vector3.UnitY);
        Matrix4 noExtension = view * Matrix4.CreateOrthographicOffCenter(
            -half - 100f, half + 100f, -half - 100f, half + 100f, centreDistance - half, centreDistance + half);
        ViewFrustum receiverOnly = ShadowCasterSelection.SunFrustum(in noExtension);
        Assert.False(ShadowCasterSelection.Accepts(inside, insideMax, focus, in receiverOnly));
    }

    [Fact]
    public void The_Sun_Frustum_Is_The_Identity_View_Times_The_Combined_Matrix()
    {
        // The choice recorded in ShadowCasterSelection.SunFrustum: SunShadowVolume.Build returns only view * projection, and
        // FromViewProjection(combined) is FromViewAndProjection(identity, combined) without the identity multiply. No split needed.
        ShadowFrame frame = ShadowFrame.ForSun(new Vector3(3f, 1f, 2f), Light(35f, 80f), MathF.Sin(MathHelper.DegreesToRadians(35f)), Resolution, 0f);
        Matrix4 identity = Matrix4.Identity;

        ViewFrustum a = ShadowCasterSelection.SunFrustum(in frame.SunLightViewProjection);
        ViewFrustum b = ViewFrustum.FromViewAndProjection(in identity, in frame.SunLightViewProjection);

        Assert.Equal(b.Left, a.Left);
        Assert.Equal(b.Right, a.Right);
        Assert.Equal(b.Bottom, a.Bottom);
        Assert.Equal(b.Top, a.Top);
        Assert.Equal(b.Near, a.Near);
        Assert.Equal(b.Far, a.Far);
        Assert.True(a.IsValid);
    }

    [Fact]
    public void The_Sun_Frustum_Is_Orthographic_Parallel_Planes_Whose_Slab_Includes_The_Extension()
    {
        float sin = 0.5f;
        Vector3 toLight = Light(30f, 10f);
        ShadowFrame frame = ShadowFrame.ForSun(Vector3.Zero, toLight, sin, Resolution, 0f);
        ViewFrustum f = ShadowCasterSelection.SunFrustum(in frame.SunLightViewProjection);

        // Left/right and bottom/top are parallel pairs; the near and far planes are perpendicular to the light, and the slab
        // between them is the receiver depth plus the caster extension (DepthRange).
        Assert.Equal(-f.Left.Xyz, f.Right.Xyz);
        Assert.Equal(-f.Bottom.Xyz, f.Top.Xyz);
        Vector3 nearNormal = f.Near.Xyz;
        Assert.True(MathF.Abs(MathF.Abs(Vector3.Dot(nearNormal, toLight)) - 1f) < 1e-4f);
        float slab = f.Near.W + f.Far.W;
        Assert.Equal(frame.SunDepthRange, slab, 2);
        Assert.Equal(ShadowSettings.OrthoSide + ShadowSettings.CasterExtensionFor(sin), slab, 2);
    }

    [Fact]
    public void An_Actor_Box_Is_A_Cube_Around_The_Feet_Scaled_With_The_Model_Matrix()
    {
        Matrix4 unit = Matrix4.CreateScale(1f) * Matrix4.CreateTranslation(5f, 1f, -2f);
        (Vector3 min, Vector3 max) = ShadowCasterSelection.ActorBounds(in unit);
        Assert.Equal(new Vector3(5f, 1f, -2f) - new Vector3(ShadowCasterSelection.ActorRadius), min);
        Assert.Equal(new Vector3(5f, 1f, -2f) + new Vector3(ShadowCasterSelection.ActorRadius), max);

        Matrix4 big = Matrix4.CreateScale(2.5f) * Matrix4.CreateTranslation(0f, 0f, 0f);
        (Vector3 bigMin, Vector3 bigMax) = ShadowCasterSelection.ActorBounds(in big);
        Assert.Equal(ShadowCasterSelection.ActorRadius * 2.5f, bigMax.X, 4);
        Assert.Equal(-ShadowCasterSelection.ActorRadius * 2.5f, bigMin.Z, 4);

        // A small model (scale below 1) never shrinks the box under the base radius.
        Matrix4 small = Matrix4.CreateScale(0.2f);
        Assert.Equal(ShadowCasterSelection.ActorRadius, ShadowCasterSelection.ActorBounds(in small).Max.X, 4);
    }

    [Fact]
    public void A_Prop_Or_Plant_Is_Accepted_No_Further_Than_The_Visible_Pass_Draws_It()
    {
        PropPlacement prop = new(PropKind.Brazier, new Vector3(1f, 2f, 3f), 0f, 1f);
        PropRenderer.CasterBounds(in prop, out Vector3 min, out Vector3 max, out float reach);
        PropBounds bounds = PropCatalog.Bounds(PropKind.Brazier);
        Assert.Equal(new Vector3(1f - bounds.HorizontalRadius, 1.7f, 3f - bounds.HorizontalRadius), min);
        Assert.Equal(new Vector3(1f + bounds.HorizontalRadius, 2f + bounds.Height + 0.3f, 3f + bounds.HorizontalRadius), max);
        Assert.Equal(MathF.Min(ShadowCasterSelection.CasterDistance, PropRenderer.DrawDistance + bounds.HorizontalRadius), reach);

        // Cover is drawn to 24 units, so it does not cast from 30; a landmark is drawn to 42 and casts there.
        GroundAsset cover = Asset(GroundLayer.Cover, radius: 0.5f, height: 0.4f);
        GroundAsset landmark = Asset(GroundLayer.Landmark, radius: 3f, height: 8f);
        GroundPlacement placement = new(0, new Vector3(30f, 0f, 0f), 0f, 1f);
        GroundScatterRenderer.CasterBounds(in placement, cover, out _, out _, out float coverReach);
        GroundScatterRenderer.CasterBounds(in placement, landmark, out _, out _, out float landmarkReach);
        Assert.Equal(GroundScatterRenderer.CoverDrawDistance + 0.5f, coverReach, 4);
        Assert.True(30f > coverReach);
        Assert.Equal(MathF.Min(ShadowCasterSelection.CasterDistance, GroundScatterRenderer.LandmarkDrawDistance + 3f), landmarkReach, 4);
        Assert.True(30f < landmarkReach);
    }

    private static GroundAsset Asset(GroundLayer layer, float radius, float height) =>
        new("test", "x.glb", GroundAssetClass.Tree, layer, radius, 1f, height, [], 1f, 1f, 1f);

    [Fact]
    public void The_Static_Chunk_Walk_Draws_Every_Accepted_Mesh_Without_A_Material_Bind_Or_A_Camera_Cull()
    {
        RecordingStore store = new();
        using StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);
        Render render = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
        GeometryRenderExport export = CaptureStartupExport(out Vector3 focus);
        Camera camera = new(focus);
        DrawUntilApplied(renderer, render, camera, export);
        Assert.True(store.Live.Count > 4);
        store.Reset();

        // The sun volume over the focus: some of the world is inside it and some is not.
        ShadowFrame frame = ShadowFrame.ForSun(focus, Light(50f, 30f), MathF.Sin(MathHelper.DegreesToRadians(50f)), Resolution, 0f);
        ViewFrustum frustum = ShadowCasterSelection.SunFrustum(in frame.SunLightViewProjection);
        Matrix4 identity = Matrix4.Identity;
        ShadowCasterView view = new(in identity, in frame.SunLightViewProjection, in frustum, focus);

        int drawn = renderer.DrawDepth(render, in view);

        Assert.Equal(drawn, store.DepthDrawn.Count);
        Assert.True(drawn > 0);
        Assert.Equal(0, store.MaterialBinds);
        Assert.Equal(0, store.Drawn);
        Assert.Equal(1, store.DepthPasses);
        Assert.Equal(store.DepthPasses, store.Ended);
        int expected = store.Live.Values.Count(b => ShadowCasterSelection.Accepts(b.Min, b.Max, focus, in frustum));
        Assert.Equal(expected, drawn);
        Assert.Equal(identity, store.DepthView);
        Assert.Equal(frame.SunLightViewProjection, store.DepthProjection);
        _output.WriteLine($"sun volume over the startup floor: {drawn} of {store.Live.Count} meshes accepted, 0 material binds");

        // Nothing accepted draws nothing, not even an empty pass.
        Matrix4 away = Matrix4.CreateOrthographicOffCenter(-1f, 1f, -1f, 1f, -1f, 1f) * Matrix4.CreateTranslation(0f, 0f, 0f);
        Matrix4 awayView = Matrix4.CreateTranslation(-5000f, 0f, 0f);
        ViewFrustum none = ViewFrustum.FromViewAndProjection(in awayView, in away);
        ShadowCasterView empty = new(in awayView, in away, in none, focus);
        store.Reset();
        Assert.Equal(0, renderer.DrawDepth(render, in empty));
        Assert.Equal(0, store.DepthPasses);
    }

    [Fact]
    public void The_Static_Chunk_Walk_Ends_The_Pass_And_Drops_Its_Handles_When_A_Draw_Throws()
    {
        RecordingStore store = new() { ThrowOnDepthDraw = true };
        using StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);
        Render render = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
        GeometryRenderExport export = CaptureStartupExport(out Vector3 focus);
        DrawUntilApplied(renderer, render, new Camera(focus), export);
        store.Reset();

        Matrix4 huge = Matrix4.CreateOrthographicOffCenter(-500f, 500f, -500f, 500f, -500f, 500f);
        Matrix4 identity = Matrix4.Identity;
        ViewFrustum frustum = ShadowCasterSelection.SunFrustum(in huge);
        ShadowCasterView view = new(in identity, in huge, in frustum, focus);

        Assert.Throws<InvalidOperationException>(() => renderer.DrawDepth(render, in view));
        Assert.Equal(1, store.Ended);

        // The next walk starts clean.
        store.ThrowOnDepthDraw = false;
        store.Reset();
        Assert.True(renderer.DrawDepth(render, in view) > 0);
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

    private static GeometryRenderExport CaptureStartupExport(out Vector3 focus)
    {
        Map map = new(
            physics: null,
            generateStartupTerrain: true,
            width: WorldDefaults.StartupFloorSize,
            depth: WorldDefaults.StartupFloorSize);
        focus = new Vector3(WorldDefaults.StartupFloorSize * 0.5f, 1f, WorldDefaults.StartupFloorSize * 0.5f);
        return map.CapturePresentationGeometryRenderExport(focus, WorldVisibility.MaxRenderDistance);
    }

    private sealed class RecordingStore : IStaticWorldChunkMeshStore
    {
        public Dictionary<StaticWorldChunkMeshKey, StaticWorldChunkBounds> Live { get; } = new();

        public List<StaticWorldChunkMeshInfo> DepthDrawn { get; } = new();

        public int MaterialBinds { get; private set; }

        public int Drawn { get; private set; }

        public int DepthPasses { get; private set; }

        public int Ended { get; private set; }

        public bool ThrowOnDepthDraw { get; set; }

        public Matrix4 DepthView { get; private set; }

        public Matrix4 DepthProjection { get; private set; }

        public void Reset()
        {
            DepthDrawn.Clear();
            MaterialBinds = 0;
            Drawn = 0;
            DepthPasses = 0;
            Ended = 0;
        }

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

        // A real draw handle (the GL store hands out its StaticMeshBuffer), so a handle kept past the pass is visible.
        public bool TryGetDrawHandle(StaticWorldChunkMeshKey key, out object? handle)
        {
            handle = Live.ContainsKey(key) ? this : null;
            return handle != null;
        }

        public void BeginPass(Render render, Camera camera)
        {
        }

        public void BindMaterial(Render render, int materialSlot) => MaterialBinds++;

        public void Draw(Render render, in StaticWorldChunkMeshInfo mesh) => Drawn++;

        public void EndPass(Render render) => Ended++;

        public void BeginDepthPass(Render render, in Matrix4 view, in Matrix4 projection)
        {
            DepthPasses++;
            DepthView = view;
            DepthProjection = projection;
        }

        public void DrawDepth(Render render, in StaticWorldChunkMeshInfo mesh, object? handle)
        {
            if (ThrowOnDepthDraw)
            {
                throw new InvalidOperationException("draw failed");
            }

            DepthDrawn.Add(mesh);
        }

        public void Clear() => Live.Clear();
    }
}
