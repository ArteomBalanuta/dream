using System.Text.RegularExpressions;
using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.Shadows;
using Dream.Rendering.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// The fire atlas's pure maths: the tile layout (and its agreement with ShadowMapTargets), the face cameras and the lookup
// contract they implement, the falloff window shared with pointLights.glsl, and which lights get the slots.
public class FireShadowAtlasLayoutTests
{
    private static LightList ListOf(params (Vector3 Position, float Radius, Vector3 Color)[] lights)
    {
        LightList list = default;
        list.Count = lights.Length;
        for (int i = 0; i < lights.Length; i++)
        {
            list.PositionRadius[i] = new Vector4(lights[i].Position, lights[i].Radius);
            list.Color[i] = new Vector4(lights[i].Color, 0f);
        }

        return list;
    }

    private static readonly Vector3 Orange = new(1.6f, 0.99f, 0.51f);

    // ---- tile layout ----

    [Fact]
    public void TileRect_Agrees_With_ShadowMapTargets_TileOrigin_For_Every_Slot_And_Face()
    {
        // The pin the task asks for: a silent disagreement here puts every fire shadow in the wrong tile. TileRect is derived
        // independently of TileOrigin, so equality over the whole grid is a real check, not a tautology.
        for (int slot = 0; slot < ShadowSettings.MaxCastingFireCount; slot++)
        {
            for (int face = 0; face < FireShadowAtlas.FaceCount; face++)
            {
                (int x, int y) = ShadowMapTargets.TileOrigin(slot, face, ShadowSettings.TileSide);
                Vector4 rect = FireShadowAtlas.TileRect(slot, face);

                Assert.Equal((float)x / ShadowSettings.AtlasResolution, rect.X, 6);
                Assert.Equal((float)y / ShadowSettings.AtlasResolution, rect.Y, 6);
                Assert.Equal((float)ShadowSettings.TileSide / ShadowSettings.AtlasResolution, rect.Z, 6);
                Assert.Equal(rect.Z, rect.W);
            }
        }
    }

    [Fact]
    public void The_Sixteen_Tiles_Are_Distinct_And_Fill_The_Atlas_Exactly()
    {
        HashSet<(int, int)> cells = new();
        for (int slot = 0; slot < ShadowSettings.MaxCastingFireCount; slot++)
        {
            for (int face = 0; face < FireShadowAtlas.FaceCount; face++)
            {
                Vector4 rect = FireShadowAtlas.TileRect(slot, face);
                cells.Add(((int)MathF.Round(rect.X * ShadowSettings.GridSide), (int)MathF.Round(rect.Y * ShadowSettings.GridSide)));
            }
        }

        Assert.Equal(ShadowSettings.GridSide * ShadowSettings.GridSide, cells.Count);
        Assert.Equal(ShadowSettings.MaxCastingFireCount * FireShadowAtlas.FaceCount, cells.Count);
    }

    [Fact]
    public void Slot_Major_Row_Major_Is_Pinned_By_Value()
    {
        // slot 0 fills row 0, slot 1 row 1: the faces of one light share a row. Pinned by value so a swap of the loop order
        // in either implementation is caught even if both were changed together.
        Assert.Equal(new Vector4(0f, 0f, 0.25f, 0.25f), FireShadowAtlas.TileRect(0, 0));
        Assert.Equal(new Vector4(0.75f, 0f, 0.25f, 0.25f), FireShadowAtlas.TileRect(0, 3));
        Assert.Equal(new Vector4(0f, 0.25f, 0.25f, 0.25f), FireShadowAtlas.TileRect(1, 0));
        Assert.Equal(new Vector4(0.5f, 0.75f, 0.25f, 0.25f), FireShadowAtlas.TileRect(3, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => FireShadowAtlas.TileRect(4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FireShadowAtlas.TileRect(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => FireShadowAtlas.TileRect(-1, 0));
    }

    // ---- the face cameras ----

    [Theory]
    [InlineData(FireShadowAtlas.PositiveX)]
    [InlineData(FireShadowAtlas.NegativeX)]
    [InlineData(FireShadowAtlas.PositiveZ)]
    [InlineData(FireShadowAtlas.NegativeZ)]
    public void The_Face_Cameras_Produce_Exactly_The_Lookup_Contracts_Ndc_And_Depth(int face)
    {
        Vector3 light = new(7.5f, 2.25f, -3f);
        float near = FireShadowAtlas.NearPlane;
        float far = FireShadowAtlas.FarPlane(10f);
        Matrix4 view = FireShadowAtlas.FaceView(light, face);
        Matrix4 projection = FireShadowAtlas.FaceProjection(near, far);
        float tanHalf = MathF.Tan(ShadowSettings.FaceFovRadians * 0.5f);

        Vector3[] offsets = [new(1f, 0.2f, 0.3f), new(2f, -0.5f, 1.2f), new(4.5f, 1f, -2.4f), new(0.6f, 0.1f, -0.2f)];
        foreach (Vector3 raw in offsets)
        {
            // Rotate the raw offset into this face's cone: the major axis along the face's forward.
            Vector3 forward = FireShadowAtlas.Forward(face);
            Vector3 d = face <= FireShadowAtlas.NegativeX
                ? new Vector3(forward.X * raw.X, raw.Y, raw.Z)
                : new Vector3(raw.Z, raw.Y, forward.Z * raw.X);
            Assert.Equal(face, FireShadowAtlas.FaceOf(d));

            Vector4 clip = new Vector4(light + d, 1f) * (view * projection);
            Vector2 ndc = new(clip.X / clip.W, clip.Y / clip.W);
            float depth = 0.5f * (clip.Z / clip.W) + 0.5f;

            (Vector2 contractNdc, float major) = FireShadowAtlas.FaceNdc(face, d);
            Assert.Equal(contractNdc.X / tanHalf, ndc.X, 3);
            Assert.Equal(contractNdc.Y / tanHalf, ndc.Y, 3);
            Assert.Equal(FireShadowAtlas.Depth(major, near, far), depth, 5);
        }
    }

    [Fact]
    public void Every_Face_Camera_Is_Right_Handed_So_The_Caster_Pass_Can_Cull_Later()
    {
        // All four faces are plain LookAt, so all four determinants are +1. A mirrored face (determinant -1) would make a
        // sign-free GLSL lookup possible, but it silently inverts winding: front-face culling in the caster pass, the
        // standard peter-panning fix, would then cull the wrong faces in exactly the -X and +Z tiles. The four signs live
        // in fireShadows.glsl instead, where they are visible.
        for (int face = 0; face < FireShadowAtlas.FaceCount; face++)
        {
            Matrix4 view = FireShadowAtlas.FaceView(Vector3.Zero, face);
            Assert.Equal(1, MathF.Sign(view.Determinant));
            Assert.Equal(1f, MathF.Abs(view.Determinant), 4);
        }
    }

    [Fact]
    public void The_View_Moves_With_The_Light_So_A_Flickering_Flame_Moves_Every_Shadow()
    {
        Vector3 a = new(4f, 3f, 5f);
        Vector3 b = a + new Vector3(0.04f, 0.03f, -0.05f);
        for (int face = 0; face < FireShadowAtlas.FaceCount; face++)
        {
            Assert.NotEqual(FireShadowAtlas.FaceView(a, face), FireShadowAtlas.FaceView(b, face));
            // The light itself is the view origin: it maps to view-space (0, 0, 0).
            Assert.True(Vector3.TransformPosition(b, FireShadowAtlas.FaceView(b, face)).Length < 1e-5f);
        }
    }

    [Fact]
    public void The_Ninety_Degree_Cone_Lands_Inside_The_Tile_Minus_The_Gutter()
    {
        // A point on the cone's edge (|across| == major) must project to +-(tile - 2 gutter) / tile of the half-extent.
        float near = FireShadowAtlas.NearPlane;
        float far = 11.5f;
        Matrix4 view = FireShadowAtlas.FaceView(Vector3.Zero, FireShadowAtlas.PositiveX);
        Matrix4 projection = FireShadowAtlas.FaceProjection(near, far);
        Vector4 clip = new Vector4(3f, 0f, 3f, 1f) * (view * projection);
        float inner = (ShadowSettings.TileSide - 2f * ShadowSettings.TileGutter) / ShadowSettings.TileSide;

        Assert.Equal(inner, MathF.Abs(clip.X / clip.W), 4);
        Assert.True(MathF.Abs(clip.X / clip.W) < 1f);
    }

    [Fact]
    public void Depth_Spans_Zero_At_The_Near_Plane_And_One_At_The_Far_Plane()
    {
        float near = FireShadowAtlas.NearPlane;
        float far = FireShadowAtlas.FarPlane(10f);
        Assert.Equal(11.5f, far, 5);
        Assert.Equal(0f, FireShadowAtlas.Depth(near, near, far), 5);
        Assert.Equal(1f, FireShadowAtlas.Depth(far, near, far), 5);
        Assert.True(FireShadowAtlas.Depth(3f, near, far) is > 0f and < 1f);
    }

    // ---- the near plane against the flame's wander ----

    // WHETHER A CASTER IS CLIPPED BY THE NEAR PLANE MUST NOT DEPEND ON THE WANDER. The caster pass renders each face from
    // the FLICKERED position, so a surface d from the flame's BASE position is tested against d minus the wander's
    // component along that face's axis; every face axis is +-X or +-Z, so that component swings over
    // +-FireFlicker.OffsetLateral. Surfaces in [NearPlane - OffsetLateral, NearPlane + OffsetLateral] therefore enter and
    // leave the frustum as the flame wanders, flipping between occluding and lit every frame.
    //
    // That band is 2 x OffsetLateral wide wherever the near plane sits, so no near plane removes it; what the near plane
    // decides is where it sits. Pushed down onto zero, the only surfaces left inside it are ones the flame itself passes
    // through, whose shadow is degenerate whatever the pass does. So the near plane must be a small FRACTION of the
    // swing - the fraction it is allowed to add to the band - and not, as it was while the wander was two centimetres,
    // several times it.
    [Fact]
    public void The_Near_Plane_Is_A_Small_Fraction_Of_The_Flames_Wander_So_The_Wander_Cannot_Flip_A_Caster_Across_It()
    {
        float swing = FireFlicker.OffsetLateral;
        Assert.True(
            FireShadowAtlas.NearPlane <= swing * FireShadowAtlas.WanderBandFraction,
            $"the near plane {FireShadowAtlas.NearPlane} carries the wander's flip band out to "
            + $"{FireShadowAtlas.NearPlane + swing} units from the flame, against the {swing} the wander alone forces");
        Assert.Equal(swing * FireShadowAtlas.WanderBandFraction, FireShadowAtlas.NearPlane);
        Assert.True(FireShadowAtlas.WanderBandFraction < 1f);
    }

    // The other side of that choice, so nobody answers the test above by driving the near plane to zero: perspective
    // depth precision is governed by the far/near ratio, and the atlas stores DEPTH_COMPONENT24. At the deepest far plane
    // the atlas ever uses (the largest fire radius) and at the worst distance inside it (the far plane itself), one depth
    // quantum in world units must stay far under ShadowSettings.ConstantBiasWorld, the smallest bias any receiver gets;
    // otherwise the quantisation, not the bias, would decide self-shadowing.
    [Fact]
    public void The_Near_Plane_Is_Still_Far_Enough_Out_That_One_Depth_Quantum_Is_Nothing_Beside_The_Constant_Bias()
    {
        const float Depth24Steps = 16777215f;
        float near = FireShadowAtlas.NearPlane;
        float far = FireShadowAtlas.FarPlane(MathF.Max(LampLightIndex.LampRadius, HeldLightCatalog.TorchRadius));

        // z_stored = 0.5 * ((f + n) / (f - n) - (2 f n / (f - n)) / z) + 0.5, so d(z_stored)/dz = (f n / (f - n)) / z^2
        // and one step of the 24 bit buffer is worth z^2 (f - n) / (Steps f n) world units at z.
        float quantum = far * far * (far - near) / (Depth24Steps * far * near);
        Assert.True(
            quantum * 8f <= ShadowSettings.ConstantBiasWorld,
            $"one depth quantum at the far plane is {quantum} world units against a constant bias of "
            + $"{ShadowSettings.ConstantBiasWorld}; the near plane {near} is too close in");
    }

    [Fact]
    public void The_Polar_Cones_Are_Not_A_Face()
    {
        Assert.Equal(-1, FireShadowAtlas.FaceOf(new Vector3(1f, 5f, 1f)));
        Assert.Equal(-1, FireShadowAtlas.FaceOf(new Vector3(0f, -2f, 0f)));
        Assert.Equal(-1, FireShadowAtlas.FaceOf(Vector3.Zero));
        // |d.y| equal to the major axis is the cone edge and counts as polar, like the GLSL's `a.y >= max(a.x, a.z)`.
        Assert.Equal(-1, FireShadowAtlas.FaceOf(new Vector3(2f, 2f, 0f)));
        Assert.Equal(FireShadowAtlas.PositiveX, FireShadowAtlas.FaceOf(new Vector3(2f, 1.9f, 0f)));
    }

    // ---- the window and the selection ----

    [Fact]
    public void The_Falloff_Window_Is_The_One_In_pointLights_glsl()
    {
        string glsl = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "shaders", "pointLights.glsl"));
        // The four lines of the window, character for character apart from the types.
        Assert.Matches(new Regex(@"float s = d2 / \(r \* r\);"), glsl);
        Assert.Matches(new Regex(@"float f = clamp\(1\.0 - s, 0\.0, 1\.0\);"), glsl);
        Assert.Matches(new Regex(@"f = f \* f / \(1\.0 \+ 1\.5 \* s\);"), glsl);

        // And the C# window evaluates that expression.
        foreach ((float distance, float radius) in new[] { (0f, 10f), (3f, 10f), (7.5f, 10f), (9.99f, 10f), (4f, 4.5f) })
        {
            float s = distance * distance / (radius * radius);
            float f = Math.Clamp(1f - s, 0f, 1f);
            Assert.Equal(f * f / (1f + 1.5f * s), FireShadowAtlas.WindowedFalloff(distance, radius), 6);
        }
    }

    [Fact]
    public void The_Window_Is_Exactly_Zero_At_And_Beyond_The_Radius_And_For_A_Dead_Light()
    {
        Assert.Equal(1f, FireShadowAtlas.WindowedFalloff(0f, 10f));
        Assert.Equal(0f, FireShadowAtlas.WindowedFalloff(10f, 10f));
        Assert.Equal(0f, FireShadowAtlas.WindowedFalloff(25f, 10f));
        Assert.Equal(0f, FireShadowAtlas.WindowedFalloff(1f, 0f));
        Assert.Equal(0f, FireShadowAtlas.WindowedFalloff(1f, -3f));
        Assert.Equal(0f, FireShadowAtlas.WindowedFalloff(1f, float.NaN));
    }

    [Fact]
    public void The_Score_Is_Luminance_Times_The_Window_At_The_Focus()
    {
        Vector3 focus = new(0f, 0f, 0f);
        float score = FireShadowAtlas.Score(new Vector3(2f, 1f, 0.5f), new Vector3(3f, 0f, 4f), 10f, focus);
        float luminance = 0.2126f * 2f + 0.7152f * 1f + 0.0722f * 0.5f;
        Assert.Equal(luminance * FireShadowAtlas.WindowedFalloff(5f, 10f), score, 5);
    }

    [Fact]
    public void The_Highest_Scores_Take_The_Slots_Best_First()
    {
        Vector3 focus = Vector3.Zero;
        // Same colour and radius, so the score is the window of the distance: nearer wins.
        LightList list = ListOf(
            (new Vector3(8f, 0f, 0f), 10f, Orange),
            (new Vector3(1f, 0f, 0f), 10f, Orange),
            (new Vector3(5f, 0f, 0f), 10f, Orange),
            (new Vector3(3f, 0f, 0f), 10f, Orange));
        Span<int> slots = stackalloc int[4];

        int count = FireShadowAtlas.SelectSlots(in list, focus, 2, slots);

        Assert.Equal(2, count);
        Assert.Equal(1, slots[0]);
        Assert.Equal(3, slots[1]);
    }

    [Fact]
    public void A_Brighter_Light_Beats_A_Nearer_Dimmer_One()
    {
        LightList list = ListOf(
            (new Vector3(2f, 0f, 0f), 10f, new Vector3(0.2f, 0.2f, 0.2f)),
            (new Vector3(4f, 0f, 0f), 10f, new Vector3(3f, 3f, 3f)));
        Span<int> slots = stackalloc int[4];

        Assert.Equal(1, FireShadowAtlas.SelectSlots(in list, Vector3.Zero, 1, slots));
        Assert.Equal(1, slots[0]);
    }

    [Fact]
    public void A_Wider_Light_Beats_A_Narrower_One_At_The_Same_Distance()
    {
        LightList list = ListOf(
            (new Vector3(6f, 0f, 0f), 7f, Orange),
            (new Vector3(6f, 0f, 0f), 14f, Orange));
        Span<int> slots = stackalloc int[4];

        FireShadowAtlas.SelectSlots(in list, Vector3.Zero, 1, slots);

        Assert.Equal(1, slots[0]);
    }

    [Fact]
    public void Ties_Keep_The_Lower_Index_Whatever_The_Order_Of_Arrival()
    {
        // Four identical lights: ranks are decided by the list index alone, so a light never swaps slots with an equal one.
        LightList list = ListOf(
            (new Vector3(3f, 0f, 0f), 10f, Orange),
            (new Vector3(0f, 0f, 3f), 10f, Orange),
            (new Vector3(-3f, 0f, 0f), 10f, Orange),
            (new Vector3(0f, 0f, -3f), 10f, Orange));
        Span<int> slots = stackalloc int[4];

        int count = FireShadowAtlas.SelectSlots(in list, Vector3.Zero, 3, slots);

        Assert.Equal(3, count);
        Assert.Equal(new[] { 0, 1, 2 }, slots[..3].ToArray());
    }

    [Fact]
    public void A_Light_That_Scores_Zero_Still_Takes_A_Free_Slot_After_Every_Scoring_Light()
    {
        LightList list = ListOf(
            (new Vector3(40f, 0f, 0f), 10f, Orange), // out of reach of the focus: score 0
            (new Vector3(2f, 0f, 0f), 10f, Orange));
        Span<int> slots = stackalloc int[4];

        int count = FireShadowAtlas.SelectSlots(in list, Vector3.Zero, 4, slots);

        Assert.Equal(2, count);
        Assert.Equal(1, slots[0]);
        Assert.Equal(0, slots[1]);
    }

    [Fact]
    public void The_Count_Is_Limited_By_The_Budget_The_Span_And_The_List_And_Never_Exceeds_Four()
    {
        LightList list = ListOf(
            (new Vector3(1f, 0f, 0f), 10f, Orange), (new Vector3(2f, 0f, 0f), 10f, Orange), (new Vector3(3f, 0f, 0f), 10f, Orange),
            (new Vector3(4f, 0f, 0f), 10f, Orange), (new Vector3(5f, 0f, 0f), 10f, Orange), (new Vector3(6f, 0f, 0f), 10f, Orange));
        Span<int> four = stackalloc int[4];
        Span<int> sixteen = stackalloc int[16];
        Span<int> two = stackalloc int[2];

        Assert.Equal(4, FireShadowAtlas.SelectSlots(in list, Vector3.Zero, 99, sixteen));
        Assert.Equal(0, FireShadowAtlas.SelectSlots(in list, Vector3.Zero, 0, four));
        Assert.Equal(0, FireShadowAtlas.SelectSlots(in list, Vector3.Zero, -3, four));
        Assert.Equal(2, FireShadowAtlas.SelectSlots(in list, Vector3.Zero, 4, two));
        LightList empty = default;
        Assert.Equal(0, FireShadowAtlas.SelectSlots(in empty, Vector3.Zero, 4, four));

        LightList small = ListOf((new Vector3(1f, 0f, 0f), 10f, Orange));
        Assert.Equal(1, FireShadowAtlas.SelectSlots(in small, Vector3.Zero, 4, four));
    }

    [Fact]
    public void The_Selection_Is_From_The_Frames_List_Only_So_It_Cannot_Name_A_Light_The_Shaders_Do_Not_Light_With()
    {
        // Entries past list.Count are stale buffer contents; the selection must not read them.
        LightList list = ListOf((new Vector3(5f, 0f, 0f), 10f, Orange));
        list.PositionRadius[7] = new Vector4(0.1f, 0f, 0f, 30f);
        list.Color[7] = new Vector4(9f, 9f, 9f, 0f);
        Span<int> slots = stackalloc int[4];

        int count = FireShadowAtlas.SelectSlots(in list, Vector3.Zero, 4, slots);

        Assert.Equal(1, count);
        Assert.Equal(0, slots[0]);

        FireShadowAtlas.SelectSlots(in list, Vector3.Zero, 4, slots);
        Assert.DoesNotContain(7, slots[..count].ToArray());
    }

    // ---- filling the frame ----

    [Fact]
    public void FillSlots_Copies_The_Flickered_Position_And_Derives_Far_From_The_Flickered_Radius()
    {
        LightList list = ListOf(
            (new Vector3(9f, 1f, 9f), 8f, Orange),
            (new Vector3(2.04f, 1.03f, 2.96f), 9.2f, Orange));
        ShadowFrame frame = ShadowFrame.None;

        FireShadowAtlas.FillSlots(in list, Vector3.Zero, 4, 0.75f, ref frame);

        Assert.Equal(2, frame.FireCount);
        FireShadowSlot first = frame.Fires[0];
        Assert.Equal(1, first.LightIndex);
        Assert.Equal(new Vector3(2.04f, 1.03f, 2.96f), first.Position);
        Assert.Equal(FireShadowAtlas.NearPlane, first.Near);
        Assert.Equal(9.2f * 1.15f, first.Far, 5);
        Assert.Equal(0.75f, first.Strength);
        Assert.Equal(0, frame.Fires[1].LightIndex);
        Assert.Equal(default, frame.Fires[2]);
        Assert.Equal(default, frame.Fires[3]);
    }

    [Fact]
    public void FillSlots_Rewrites_Every_Slot_Each_Frame_So_The_Flicker_Reaches_The_Maps()
    {
        // The list entries already carry the flicker (applied at collection time). Two frames of the same fire at different
        // flicker samples must give a different origin and a different far plane, which is what makes the shadow move.
        LightList frameA = ListOf((new Vector3(4f, 1f, 4f), 10f, Orange));
        LightList frameB = ListOf((new Vector3(4.03f, 1.02f, 3.97f), 10.6f, Orange));
        ShadowFrame frame = ShadowFrame.None;

        FireShadowAtlas.FillSlots(in frameA, Vector3.Zero, 4, 1f, ref frame);
        FireShadowSlot a = frame.Fires[0];
        FireShadowAtlas.FillSlots(in frameB, Vector3.Zero, 4, 1f, ref frame);
        FireShadowSlot b = frame.Fires[0];

        Assert.NotEqual(a.Position, b.Position);
        Assert.NotEqual(a.Far, b.Far);
        Assert.Equal(11.5f, a.Far, 4);
        Assert.Equal(10.6f * 1.15f, b.Far, 4);
        for (int face = 0; face < FireShadowAtlas.FaceCount; face++)
        {
            Assert.NotEqual(FireShadowAtlas.FaceView(a.Position, face), FireShadowAtlas.FaceView(b.Position, face));
        }

        // A frame that selects fewer lights clears the slots it no longer uses (no stale slot is rendered).
        LightList none = default;
        FireShadowAtlas.FillSlots(in none, Vector3.Zero, 4, 1f, ref frame);
        Assert.Equal(0, frame.FireCount);
        Assert.Equal(default, frame.Fires[0]);
    }

    [Fact]
    public void FillSlots_Honours_The_Fire_Budget()
    {
        LightList list = ListOf(
            (new Vector3(1f, 0f, 0f), 10f, Orange), (new Vector3(2f, 0f, 0f), 10f, Orange), (new Vector3(3f, 0f, 0f), 10f, Orange));
        ShadowFrame frame = ShadowFrame.None;

        FireShadowAtlas.FillSlots(in list, Vector3.Zero, 0, 1f, ref frame);
        Assert.Equal(0, frame.FireCount);

        FireShadowAtlas.FillSlots(in list, Vector3.Zero, 2, 1f, ref frame);
        Assert.Equal(2, frame.FireCount);
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
}
