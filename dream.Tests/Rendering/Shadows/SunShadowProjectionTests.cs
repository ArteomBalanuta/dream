using Dream.Client.Presentation;
using Dream.Rendering.Shadows;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

public class SunShadowProjectionTests
{
    private const int Resolution = 2048;

    private static Vector3 Light(float elevationDegrees, float azimuthDegrees)
    {
        float e = MathHelper.DegreesToRadians(elevationDegrees);
        float a = MathHelper.DegreesToRadians(azimuthDegrees);
        return new Vector3(MathF.Cos(e) * MathF.Cos(a), MathF.Sin(e), MathF.Cos(e) * MathF.Sin(a));
    }

    private static float Sin(float elevationDegrees) => MathF.Sin(MathHelper.DegreesToRadians(elevationDegrees));

    private static Vector4 Clip(in Matrix4 m, Vector3 p) => new Vector4(p, 1f) * m;

    private static Vector2 TexelCoordinate(in Matrix4 lightViewProjection, Vector3 p, int resolution)
    {
        Vector4 clip = Clip(in lightViewProjection, p);
        return new Vector2((clip.X * 0.5f + 0.5f) * resolution, (clip.Y * 0.5f + 0.5f) * resolution);
    }

    [Fact]
    public void The_Returned_Texel_Is_The_Constant_Ortho_Side_Over_The_Resolution_Whatever_The_Light_Or_Focus()
    {
        float expected = ShadowSettings.OrthoSide / Resolution;
        foreach (float elevation in new[] { 5f, 20f, 45f, 75f, 90f })
        {
            foreach (float azimuth in new[] { 0f, 100f, 250f })
            {
                (_, float texel, _) = SunShadowVolume.Build(new Vector3(13.7f, 4f, -22.1f), Light(elevation, azimuth), Sin(elevation), Resolution);
                Assert.Equal(expected, texel, 7);
            }
        }
    }

    [Fact]
    public void One_World_Texel_Along_The_Light_Right_Axis_Is_Exactly_Two_Over_Resolution_In_Ndc()
    {
        Vector3 toLight = Light(40f, 70f);
        (Matrix4 lvp, float texel, _) = SunShadowVolume.Build(new Vector3(3f, 1f, 2f), toLight, Sin(40f), Resolution);
        // Light-space right axis = normalize(cross(up, toLight)) for LookAt with up = +Y.
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toLight));
        Vector3 origin = new(3f, 1f, 2f);

        float delta = Clip(in lvp, origin + right * texel).X - Clip(in lvp, origin).X;

        Assert.Equal(2f / Resolution, MathF.Abs(delta), 6);
    }

    [Fact]
    public void The_Depth_Range_Is_The_Ortho_Side_Plus_The_Caster_Extension_And_Maps_Linearly_To_Ndc()
    {
        foreach (float elevation in new[] { 8f, 30f, 60f, 90f })
        {
            Vector3 toLight = Light(elevation, 35f);
            (Matrix4 lvp, _, float range) = SunShadowVolume.Build(Vector3.Zero, toLight, Sin(elevation), Resolution);

            Assert.Equal(ShadowSettings.OrthoSide + ShadowSettings.CasterExtensionFor(Sin(elevation)), range, 3);
            // One world unit toward the light is exactly 2 / range of NDC depth (nearer = more negative).
            float dz = Clip(in lvp, Vector3.Zero + toLight).Z - Clip(in lvp, Vector3.Zero).Z;
            Assert.Equal(-2f / range, dz, 5);
        }
    }

    [Fact]
    public void A_Caster_Far_Behind_The_Receiver_Toward_The_Light_Is_Still_Inside_The_Depth_Range()
    {
        foreach (float elevation in new[] { 12f, 30f, 55f, 85f })
        {
            Vector3 toLight = Light(elevation, 200f);
            Vector3 focus = new(5f, 0f, 5f);
            (Matrix4 lvp, _, _) = SunShadowVolume.Build(focus, toLight, Sin(elevation), Resolution);
            Vector3 centre = focus + SunShadowVolume.ViewForwardGround * ShadowSettings.ReceiverCentreOffset;

            // A caster up to the extension nearer the light than the receiver centre must not be clipped by the near plane.
            float reach = ShadowSettings.CasterExtensionFor(Sin(elevation)) * 0.95f;
            Vector4 inside = Clip(in lvp, centre + toLight * reach);
            Assert.InRange(inside.Z, -1f, 1f);

            // Well beyond the extension IS behind the near plane, so the pull-back is exactly as large as it claims.
            Vector4 beyond = Clip(in lvp, centre + toLight * (ShadowSettings.OrthoSide * 0.5f + ShadowSettings.CasterExtensionFor(Sin(elevation)) + 5f));
            Assert.True(beyond.Z < -1f, $"A caster {beyond.Z} beyond the near plane should be clipped.");
        }
    }

    [Fact]
    public void The_Near_Plane_Is_Pulled_Back_Without_Widening_The_Ortho()
    {
        // The same XY footprint at every elevation: only the depth range grows when the sun drops.
        Vector3 focus = new(1f, 2f, 3f);
        (Matrix4 high, _, float highRange) = SunShadowVolume.Build(focus, Light(80f, 0f), Sin(80f), Resolution);
        (Matrix4 low, _, float lowRange) = SunShadowVolume.Build(focus, Light(10f, 0f), Sin(10f), Resolution);

        Assert.True(lowRange > highRange);
        // Per world unit along the light's right axis the NDC scale is 2 / OrthoSide at BOTH elevations.
        foreach ((Matrix4 lvp, float elevation) in new[] { (high, 80f), (low, 10f) })
        {
            Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, Light(elevation, 0f)));
            float perUnit = MathF.Abs(Clip(in lvp, focus + right).X - Clip(in lvp, focus).X);
            Assert.Equal(2f / ShadowSettings.OrthoSide, perUnit, 6);
        }
    }

    // The core of "shimmer-free": the light-space origin is quantised to whole texels, so however the focus moves a
    // fixed world point's TEXEL COORDINATE changes only by whole texels, never by a fraction.
    [Theory]
    [InlineData(60f, 20f)]
    [InlineData(25f, 140f)]
    [InlineData(80f, 300f)]
    public void Moving_The_Focus_By_Fractions_Of_A_Texel_Moves_A_Fixed_Point_Only_By_Whole_Texels(float elevation, float azimuth)
    {
        Vector3 toLight = Light(elevation, azimuth);
        Vector3 fixedPoint = new(7.31f, 0.4f, -3.77f);
        Vector3 focus0 = new(2.2f, 1f, 5.9f);
        float texelWorld = ShadowSettings.OrthoSide / Resolution;

        (Matrix4 lvp0, _, _) = SunShadowVolume.Build(focus0, toLight, Sin(elevation), Resolution);
        Vector2 t0 = TexelCoordinate(in lvp0, fixedPoint, Resolution);

        float worstFraction = 0f;
        int movedWholeTexels = 0;
        for (int step = 1; step <= 120; step++)
        {
            // Sub-texel strides along both axes at once, so neither snap axis is idle.
            Vector3 focus = focus0 + new Vector3(0.3173f, 0f, -0.2291f) * (texelWorld * step);
            (Matrix4 lvp, _, _) = SunShadowVolume.Build(focus, toLight, Sin(elevation), Resolution);
            Vector2 t = TexelCoordinate(in lvp, fixedPoint, Resolution);

            float dx = t.X - t0.X;
            float dy = t.Y - t0.Y;
            float fx = MathF.Abs(dx - MathF.Round(dx));
            float fy = MathF.Abs(dy - MathF.Round(dy));
            worstFraction = MathF.Max(worstFraction, MathF.Max(fx, fy));
            if (MathF.Abs(dx) > 0.5f || MathF.Abs(dy) > 0.5f)
            {
                movedWholeTexels++;
            }
        }

        // Rounding step: float32 over ~2000 texels, a few thousandths of a texel at worst.
        Assert.True(worstFraction < 0.01f, $"A fixed point slid by a fraction of a texel ({worstFraction}).");
        // The scan really did cross texel boundaries (otherwise the property would hold trivially).
        Assert.True(movedWholeTexels > 20, $"Only {movedWholeTexels} of 120 steps moved a whole texel.");
    }

    [Fact]
    public void Without_Snapping_The_Same_Scan_Would_Slide_By_Fractions()
    {
        // Control for the test above: build the same projection with the origin NOT quantised and show the fraction appears.
        Vector3 toLight = Light(60f, 20f);
        float texelWorld = ShadowSettings.OrthoSide / Resolution;
        Vector3 fixedPoint = new(7.31f, 0.4f, -3.77f);
        Vector3 focus0 = new(2.2f, 1f, 5.9f);
        Vector2 t0 = UnsnappedTexel(focus0, toLight, fixedPoint);
        float worst = 0f;
        for (int step = 1; step <= 120; step++)
        {
            Vector3 focus = focus0 + new Vector3(0.3173f, 0f, -0.2291f) * (texelWorld * step);
            Vector2 t = UnsnappedTexel(focus, toLight, fixedPoint);
            float dx = t.X - t0.X;
            worst = MathF.Max(worst, MathF.Abs(dx - MathF.Round(dx)));
        }

        Assert.True(worst > 0.2f, $"The control did not slide ({worst}); the snapping test would prove nothing.");
    }

    private static Vector2 UnsnappedTexel(Vector3 focus, Vector3 toLight, Vector3 point)
    {
        float side = ShadowSettings.OrthoSide;
        Vector3 centre = focus + SunShadowVolume.ViewForwardGround * ShadowSettings.ReceiverCentreOffset;
        Matrix4 view = Matrix4.LookAt(toLight * side, Vector3.Zero, Vector3.UnitY);
        Vector3 c = Vector3.TransformPosition(centre, view);
        Vector3 p = Vector3.TransformPosition(point, view);
        return new Vector2((p.X - (c.X - side * 0.5f)) / side * Resolution, (p.Y - (c.Y - side * 0.5f)) / side * Resolution);
    }

    [Fact]
    public void Snapping_Rounds_To_The_Nearest_Whole_Step()
    {
        Assert.Equal(0.5f, SunShadowVolume.Snap(0.52f, 0.25f), 6);
        Assert.Equal(0.5f, SunShadowVolume.Snap(0.4f, 0.25f), 6);
        Assert.Equal(-0.25f, SunShadowVolume.Snap(-0.2f, 0.25f), 6);
        Assert.Equal(0f, SunShadowVolume.Snap(0.1f, 0.25f), 6);
    }

    // The honest requirement behind the receiver box: everything the camera can actually see (the measured ground
    // footprint of -5.9..+10.1 along the view azimuth and +-11.4 across, plus relief 2 below the focus and props up to 16 above it) must land
    // inside the map at EVERY sun azimuth and elevation, in x, y and depth.
    [Fact]
    public void The_Visible_Footprint_Fits_Inside_The_Map_At_Every_Sun_Azimuth_And_Elevation()
    {
        Vector3 focus = new(40f, 3f, -17f);
        Vector3 forward = SunShadowVolume.ViewForwardGround;
        Vector3 lateral = new(-forward.Z, 0f, forward.X);
        List<Vector3> corners = [];
        foreach (float along in new[] { -5.9f, 10.1f })
        {
            foreach (float across in new[] { -11.4f, 11.4f })
            {
                foreach (float height in new[] { -2f, 16f })
                {
                    corners.Add(focus + forward * along + lateral * across + Vector3.UnitY * height);
                }
            }
        }

        for (float elevation = 10f; elevation <= 90f; elevation += 10f)
        {
            for (float azimuth = 0f; azimuth < 360f; azimuth += 30f)
            {
                (Matrix4 lvp, _, _) = SunShadowVolume.Build(focus, Light(elevation, azimuth), Sin(elevation), Resolution);
                foreach (Vector3 corner in corners)
                {
                    Vector4 clip = Clip(in lvp, corner);
                    Assert.True(MathF.Abs(clip.X) <= 1f && MathF.Abs(clip.Y) <= 1f && MathF.Abs(clip.Z) <= 1f,
                        $"Corner {corner} at elevation {elevation}, azimuth {azimuth} fell outside the map: {clip}.");
                }
            }
        }
    }

    // The design wrote the centre offset along (cos45, 0, sin45), which is TOWARD the camera. The camera sits at
    // +X,+Z of the focus and looks along -X,-Z, and the screen shows more ground in front of the focus than behind.
    [Fact]
    public void The_Receiver_Centre_Is_Offset_Away_From_The_Camera_Not_Toward_It()
    {
        Vector3 toCamera = IsometricView.FocusToCamera;
        Vector3 forward = SunShadowVolume.ViewForwardGround;

        Assert.True(Vector3.Dot(forward, new Vector3(toCamera.X, 0f, toCamera.Z)) < 0f);
        Assert.Equal(1f, forward.Length, 5);
        Assert.Equal(0f, forward.Y);
        Assert.True(forward.X < 0f && forward.Z < 0f);
    }

    [Fact]
    public void The_Volume_Is_Finite_And_Stable_For_A_Straight_Down_Light()
    {
        (Matrix4 lvp, float texel, float range) = SunShadowVolume.Build(new Vector3(1f, 2f, 3f), Vector3.UnitY, 1f, Resolution);

        Assert.True(float.IsFinite(texel) && float.IsFinite(range));
        foreach (Vector4 row in new[] { lvp.Row0, lvp.Row1, lvp.Row2, lvp.Row3 })
        {
            Assert.True(float.IsFinite(row.X) && float.IsFinite(row.Y) && float.IsFinite(row.Z) && float.IsFinite(row.W));
        }
    }

    [Fact]
    public void A_Non_Positive_Resolution_Is_Rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SunShadowVolume.Build(Vector3.Zero, Vector3.UnitY, 1f, 0));
    }

    // ---- Bias: derived values, not literals ----

    [Fact]
    public void The_Constant_Bias_In_Depth_Units_Times_The_Depth_Range_Is_The_World_Bias()
    {
        foreach (float range in new[] { 48f, 80f, 130f })
        {
            Assert.Equal(ShadowSettings.ConstantBiasWorld, SunShadowVolume.ConstantBiasDepth(range) * range, 6);
        }
    }

    [Fact]
    public void The_Normal_Offset_Is_One_And_A_Half_World_Texels()
    {
        float texel = ShadowSettings.TexelWorldFor(Resolution);
        Assert.Equal(ShadowSettings.NormalOffsetTexels * texel, SunShadowVolume.NormalOffsetWorld(texel), 7);
        Assert.Equal(1.5f, ShadowSettings.NormalOffsetTexels);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void The_Slope_Term_Vanishes_Facing_The_Light_And_Saturates_At_Its_Derived_Maximum(int resolution)
    {
        float range = 100f;
        float texel = ShadowSettings.TexelWorldFor(resolution);
        // Facing the light head on: constant bias only.
        Assert.Equal(ShadowSettings.ConstantBiasWorld, SunShadowVolume.BiasDepth(1f, range, texel) * range, 6);
        // Grazing or back facing: capped at constant + the derived maximum slope bias for THIS texel.
        float expectedMax = ShadowSettings.ConstantBiasWorld + ShadowSettings.SlopeBiasMaxWorldFor(texel);
        Assert.Equal(expectedMax, SunShadowVolume.BiasDepth(0f, range, texel) * range, 5);
        Assert.Equal(expectedMax, SunShadowVolume.BiasDepth(-0.5f, range, texel) * range, 5);
        float previous = SunShadowVolume.BiasDepth(1f, range, texel);
        for (float ndl = 0.95f; ndl >= 0.1f; ndl -= 0.05f)
        {
            float bias = SunShadowVolume.BiasDepth(ndl, range, texel);
            Assert.True(bias >= previous);
            previous = bias;
        }
    }

    [Fact]
    public void A_Coarser_Map_Gets_A_Proportionally_Larger_Slope_Bias()
    {
        float coarse = SunShadowVolume.BiasDepth(0f, 100f, ShadowSettings.TexelWorldFor(512)) - SunShadowVolume.ConstantBiasDepth(100f);
        float fine = SunShadowVolume.BiasDepth(0f, 100f, ShadowSettings.TexelWorldFor(2048)) - SunShadowVolume.ConstantBiasDepth(100f);
        Assert.Equal(4f, coarse / fine, 3);
    }

    [Fact]
    public void The_Slope_Tangent_Is_The_Real_Tangent_Until_The_Grazing_Floor_Then_Clamped()
    {
        float ndl = 0.8f;
        Assert.Equal(MathF.Sqrt(1f - ndl * ndl) / ndl, SunShadowVolume.SlopeTan(ndl), 5);
        Assert.Equal(0f, SunShadowVolume.SlopeTan(1f), 6);
        Assert.Equal(ShadowSettings.SlopeTanMax, SunShadowVolume.SlopeTan(0f), 6);
        // Between the floor and where tan reaches the clamp it follows the floored form.
        float atFloor = SunShadowVolume.SlopeTan(ShadowSettings.GrazingCosineFloor);
        Assert.Equal(ShadowSettings.SlopeTanMax, atFloor, 6);
    }

    [Fact]
    public void Shadow_Frame_Params_Carry_The_Derived_Bias_For_The_Frames_Own_Depth_Range()
    {
        ShadowFrame frame = ShadowFrame.ForSun(new Vector3(1f, 0f, 1f), Light(35f, 80f), Sin(35f), Resolution, 1f);

        Assert.Equal(1f / Resolution, frame.SunParams.X, 9);
        Assert.Equal(ShadowSettings.ConstantBiasWorld, frame.SunParams.Y * frame.SunDepthRange, 6);
        Assert.Equal(ShadowSettings.NormalOffsetTexels * frame.SunTexelWorld, frame.SunParams.Z, 7);
        Assert.Equal(1f, frame.SunParams.W);
    }
}
