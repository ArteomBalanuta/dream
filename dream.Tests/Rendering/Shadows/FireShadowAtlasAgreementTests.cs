using System.Text.RegularExpressions;
using Dream.Rendering.Core;
using Dream.Rendering.Lighting;
using Dream.Rendering.Shadows;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// INTEGRATION GATE. The fire atlas is described twice: FireShadowAtlas (the face cameras and tile layout the caster pass
// renders with, workstream D2) and FireShadowList (the uniform block the lookup reads, workstream D3). The two were built
// independently and arrived with INCOMPATIBLE face conventions: D2 made the -X and +Z cameras mirrored so a sign-free GLSL
// lookup would work, D3 used a plain Matrix4.LookAt per face and carried the signs in fireShadows.glsl. Plain LookAt won
// (a mirrored view matrix silently inverts winding, and front-face culling in the caster pass is a plausible future
// peter-panning fix, so no face may be left-handed).
//
// A disagreement between the two is SILENT: it compiles, every test on either side passes, and every fire shadow is read
// from the wrong tile or the wrong side of the right one. These tests are the proof that they agree.
public class FireShadowAtlasAgreementTests
{
    private static readonly Vector3 Eye = Vector3.Zero;
    private static readonly Matrix4 View = Matrix4.LookAt(Eye, -Vector3.UnitZ, Vector3.UnitY);
    private static readonly Matrix4 Projection =
        Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(60f), 16f / 9f, 0.1f, 500f);

    // ---- the face cameras ----

    [Fact]
    public void The_Caster_Pass_Face_Camera_Is_Exactly_A_Plain_LookAt_Down_The_Lookups_Face_Forward()
    {
        Vector3[] lights = [Vector3.Zero, new(7.5f, 2.25f, -3f), new(-19f, 0.75f, 42.5f)];
        foreach (Vector3 light in lights)
        {
            for (int face = 0; face < ShadowSettings.FacesPerLight; face++)
            {
                Matrix4 expected = Matrix4.LookAt(light, light + FireShadowList.FaceForward(face), Vector3.UnitY);
                Assert.Equal(expected, FireShadowAtlas.FaceView(light, face));
            }
        }
    }

    [Fact]
    public void The_Two_Sides_Number_And_Orient_The_Faces_The_Same_Way()
    {
        Assert.Equal(FireShadowList.FacePlusX, FireShadowAtlas.PositiveX);
        Assert.Equal(FireShadowList.FaceMinusX, FireShadowAtlas.NegativeX);
        Assert.Equal(FireShadowList.FacePlusZ, FireShadowAtlas.PositiveZ);
        Assert.Equal(FireShadowList.FaceMinusZ, FireShadowAtlas.NegativeZ);
        Assert.Equal(ShadowSettings.FacesPerLight, FireShadowAtlas.FaceCount);

        for (int face = 0; face < ShadowSettings.FacesPerLight; face++)
        {
            Assert.Equal(FireShadowList.FaceForward(face), FireShadowAtlas.Forward(face));
        }
    }

    [Fact]
    public void The_Face_Right_Axes_Are_The_Per_Face_Signs_FireShadows_Glsl_Spells_Out()
    {
        // fireShadows.glsl: lat.x = (d.x > 0 ? d.z : -d.z) on the x faces and (d.z > 0 ? -d.x : d.x) on the z faces.
        Vector3[] expected =
        [
            Vector3.UnitZ,   // +X
            -Vector3.UnitZ,  // -X
            -Vector3.UnitX,  // +Z
            Vector3.UnitX,   // -Z
        ];

        Vector3 d = new(3.5f, 0.75f, -2.25f);
        for (int face = 0; face < ShadowSettings.FacesPerLight; face++)
        {
            Assert.Equal(expected[face], FireShadowAtlas.Right(face));
            // ... and the CPU mirror of the lookup reads its lateral coordinate off that axis.
            bool xFace = face is FireShadowAtlas.PositiveX or FireShadowAtlas.NegativeX;
            float major = xFace ? MathF.Abs(d.X) : MathF.Abs(d.Z);
            (Vector2 ndc, float reported) = FireShadowAtlas.FaceNdc(face, d);
            Assert.Equal(major, reported, 5);
            Assert.Equal(Vector3.Dot(d, expected[face]) / major, ndc.X, 5);
            Assert.Equal(d.Y / major, ndc.Y, 5);
        }
    }

    // ---- the tile layout ----

    [Fact]
    public void Every_Slot_And_Face_Lands_On_The_Same_Tile_On_Both_Sides()
    {
        int pairs = 0;
        HashSet<Vector4> distinct = [];
        for (int slot = 0; slot < ShadowSettings.MaxCastingFireCount; slot++)
        {
            for (int face = 0; face < ShadowSettings.FacesPerLight; face++)
            {
                Vector4 caster = FireShadowAtlas.TileRect(slot, face);
                Vector4 lookup = FireShadowList.TileRect(slot, face, ShadowSettings.TileSide, ShadowSettings.AtlasResolution);
                Assert.Equal(caster, lookup);
                Assert.True(distinct.Add(caster), "no two slot/face pairs may share a tile");
                pairs++;
            }
        }

        Assert.Equal(16, pairs);
        Assert.Equal(ShadowSettings.GridSide * ShadowSettings.GridSide, distinct.Count);
    }

    [Fact]
    public void The_Uploaded_Block_Carries_The_Tiles_The_Caster_Pass_Rendered_Into()
    {
        ShadowFrame frame = default;
        Span<FireShadowSlot> fires = frame.Fires;
        for (int slot = 0; slot < ShadowSettings.MaxCastingFireCount; slot++)
        {
            fires[slot] = new FireShadowSlot
            {
                LightIndex = slot,
                Position = new Vector3(slot, 1f, -slot),
                Near = FireShadowAtlas.NearPlane,
                Far = FireShadowAtlas.FarPlane(10f),
                Strength = 1f,
            };
        }

        frame.FireCount = ShadowSettings.MaxCastingFireCount;
        FireShadowList list = FireShadowList.FromFrame(in frame);
        for (int slot = 0; slot < ShadowSettings.MaxCastingFireCount; slot++)
        {
            for (int face = 0; face < ShadowSettings.FacesPerLight; face++)
            {
                Assert.Equal(
                    FireShadowAtlas.TileRect(slot, face),
                    list.Tile[slot * ShadowSettings.FacesPerLight + face]);
            }
        }
    }

    // ---- the light selection order ----

    [Fact]
    public void The_Slots_Are_Chosen_From_The_Lights_That_Same_Frame_Lit()
    {
        // The bug this guards: _selectedLights is filled by UploadFrameLighting, which runs inside DrawWorld, AFTER
        // PrepareShadows. If PrepareShadows did not select the lights itself the fire slots would be empty on the first
        // frame and one frame stale after it, so a shadow would point at where the flame WAS.
        PointLight[] lights =
        [
            new(new Vector3(0f, 1f, -6f), 10f, new Vector3(1.6f, 0.99f, 0.51f)),
            new(new Vector3(3f, 1f, -9f), 10f, new Vector3(1.6f, 0.99f, 0.51f)),
            new(new Vector3(-2f, 1f, -14f), 8f, new Vector3(1.2f, 0.7f, 0.4f)),
            new(new Vector3(1f, 1f, -22f), 10f, new Vector3(0.8f, 0.5f, 0.3f)),
            new(new Vector3(-5f, 1f, -30f), 10f, new Vector3(0.8f, 0.5f, 0.3f)),
        ];

        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(View, Projection);
        LightList lit = default;
        LightSelection.SelectNearest(lights, Eye, in frustum, ref lit);
        Assert.True(lit.Count > 1, "the fixture must light more than one light");

        ShadowFrame frame = default;
        FireShadowAtlas.FillSlots(in lit, Eye, ShadowSettings.MaxCastingFireCount, 1f, ref frame);
        Assert.True(frame.FireCount > 0);

        for (int slot = 0; slot < frame.FireCount; slot++)
        {
            FireShadowSlot fire = frame.Fires[slot];
            Assert.InRange(fire.LightIndex, 0, lit.Count - 1);
            Vector4 positionRadius = lit.PositionRadius[fire.LightIndex];
            // The slot's depth-map origin IS the lit light's position and its far plane IS that light's radius, so the
            // tile the pass rendered and the light the shader shades with cannot be two different lights.
            Assert.Equal(positionRadius.Xyz, fire.Position);
            Assert.Equal(FireShadowAtlas.FarPlane(positionRadius.W), fire.Far, 5);
        }

        // Re-selecting from the same inputs is identical, which is what makes PrepareShadows' selection and
        // UploadFrameLighting's later one the same list rather than two.
        LightList again = default;
        LightSelection.SelectNearest(lights, Eye, in frustum, ref again);
        Assert.Equal(lit.Count, again.Count);
        for (int i = 0; i < lit.Count; i++)
        {
            Assert.Equal(lit.PositionRadius[i], again.PositionRadius[i]);
            Assert.Equal(lit.Color[i], again.Color[i]);
        }
    }

    [Fact]
    public void PrepareShadows_Selects_The_Point_Lights_Before_It_Fills_The_Fire_Slots()
    {
        string source = Strip(Read("dream", "Rendering", "Core", "Render.Shadows.cs"));
        int select = source.IndexOf("SelectPointLights(", StringComparison.Ordinal);
        int fill = source.IndexOf("FireShadowAtlas.FillSlots(", StringComparison.Ordinal);
        Assert.True(select >= 0, "PrepareShadows must select this frame's point lights itself");
        Assert.True(fill >= 0);
        Assert.True(select < fill, "the slots must be picked from THIS frame's lights, not last frame's");

        // And the seam D3 wrote while D2's file did not exist is gone, with the call site bound to D2's real signature.
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Render.ShadowsSeam.cs")));
        string scene = Strip(Read("dream", "Client", "Scenes", "PlayScene.Render.cs"));
        Assert.Matches(@"PrepareShadows\(\s*presentation\.Camera\s*,", scene);
        Assert.True(
            scene.IndexOf("SetFrameLighting(", StringComparison.Ordinal) < scene.IndexOf("PrepareShadows(", StringComparison.Ordinal),
            "PrepareShadows writes the sun matrix into the frame lighting, so SetFrameLighting must run first");
    }

    // ---- the wiring of the block nobody owned ----

    [Fact]
    public void The_FireShadowList_Buffer_Is_Constructed_Uploaded_Bound_And_Disposed()
    {
        string lighting = Strip(Read("dream", "Rendering", "Core", "Render.Lighting.cs"));
        Assert.Contains("new Dream.Rendering.Shadows.FireShadowListBuffer()", lighting);
        Assert.Contains("_fireShadowListBuffer?.Dispose()", lighting);
        Assert.Contains("_fireShadowListBuffer?.Upload(in _shadowFrame)", lighting);
        Assert.Contains("_fireShadowListBuffer?.BindWorld()", lighting);
        Assert.Contains("_fireShadowListBuffer?.BindViewModel()", lighting);

        // The upload sits in UploadFrameLighting, after PrepareShadows filled the frame and before any world draw.
        int upload = lighting.IndexOf("_fireShadowListBuffer?.Upload(in _shadowFrame)", StringComparison.Ordinal);
        int bindWorld = lighting.IndexOf("public void BindWorldLighting()", StringComparison.Ordinal);
        Assert.True(upload >= 0 && upload < bindWorld);

        // Link-time block binding, or the block aliases binding point 0 and reads FrameLighting.
        Assert.Contains("FireShadowListBuffer.BindProgramBlock(program)", Strip(Read("dream", "Rendering", "Core", "Shader.cs")));

        // The view-model range is a count-0 block by construction: eye-space fragPos against a world-space atlas.
        FireShadowList viewModel = default;
        Assert.Equal(0, viewModel.Count);
    }

    [Fact]
    public void The_Maps_Are_Sampled_And_The_Sun_Lookup_Is_Live()
    {
        Assert.True(ShadowCasterPass.MapsAreSampled);
        Assert.Equal(1f, ShadowCasterPass.Strength);
        Assert.Equal(ShadowSettings.Enabled, ShadowCasterPass.ShadowMapsReplaceBlobs);
        Assert.Matches(@"#define\s+DREAM_SUN_SHADOW_LIVE\s+1", Read("dream", "shaders", "lighting.glsl"));
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string Strip(string source) => Regex.Replace(source, @"//[^\r\n]*", string.Empty);

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
