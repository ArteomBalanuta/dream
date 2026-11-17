using Dream.Rendering.Shadows;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// The penumbra gate. The flame wanders, so the shadow edge it casts sweeps several texels of the fire atlas every
// second; what decides whether that reads as a soft boundary drifting or as a hard line stepping between texels is
// the width of the lookup's own visibility ramp. This measures that ramp through FireShadowMirror (the CPU mirror of
// dreamFireVisibility, in FireShadowSamplingMirrorTests.cs) over a CPU-rendered atlas, and pins it in texels of the
// fire map at the representative caster distance - ShadowSettings.FireTexelWorldAt, never a literal.
public class FireShadowSoftnessTests
{
    private static readonly Vector3 Fire = new(10.3f, 1f, -4.2f);
    private const float Radius = 10f;

    // The representative rig: a box OccluderDistance east of the fire, ground receivers further east. The ramp is
    // scanned along +Z at a constant x, where one world unit is exactly 1 / FireTexelWorldAt(major) texels of the
    // +X face, so a width in world units converts to texels by that one definition.
    private const float OccluderDistance = 3f;
    private const float ReceiverDistance = 8f;

    // A ramp at least this many texels wide, so the edge's per-frame motion is a fraction of its own softness...
    private const float MinRampTexels = 3.5f;

    // ...and at most this many, so the fix does not become a blur.
    private const float MaxRampTexels = 8f;

    // The ramp's full support, from the last fully shadowed point to the first fully lit one.
    private const float FloorLevel = 0.001f;
    private const float CeilingLevel = 0.999f;

    private const float LowLevel = 0.1f;
    private const float HighLevel = 0.9f;

    // The kernel is a FireTapsPerSide square grid of unit-wide bilinear ramps, so the visibility across an edge is a
    // staircase whose outermost segment spans one texel and carries 1 / FireTapsPerSide of the range. The LowLevel
    // crossing therefore sits LowLevel * FireTapsPerSide texels inside the support at each end, and the LowLevel to
    // HighLevel width is that much narrower than the support at both ends. This is why the two bounds differ; it is
    // a prediction of the kernel's shape, pinned below against both measurements.
    private static float InsetTexels => LowLevel * ShadowSettings.FireTapsPerSide;

    [Fact]
    public void The_Visibility_Ramp_Across_A_Shadow_Edge_Is_Several_Texels_Wide_So_The_Moving_Edge_Reads_As_A_Penumbra()
    {
        Rig rig = new(ReceiverDistance);
        float texel = ShadowSettings.FireTexelWorldAt(ReceiverDistance);

        float? support = RampWidth(rig.Visibility, rig.Dark, rig.Lit, FloorLevel, CeilingLevel);
        Assert.NotNull(support);
        Assert.InRange(support.Value, MinRampTexels * texel, MaxRampTexels * texel);

        float? width = RampWidth(rig.Visibility, rig.Dark, rig.Lit);
        Assert.NotNull(width);
        Assert.InRange(width.Value, (MinRampTexels - (2f * InsetTexels)) * texel, MaxRampTexels * texel);

        // The two measurements must differ by exactly the staircase's two outer insets, which is what makes the
        // narrower bound above a derivation and not a relaxation.
        Assert.Equal(2f * InsetTexels, (support.Value - width.Value) / texel, 1);
    }

    // The ramp is a property of the kernel, which lives in texels, so it must stay the same number of texels wide at
    // other caster distances - where the world texel is a different size. A ramp fixed in WORLD units (a kernel that
    // forgot to scale with texelWorld) would fail here while passing at the representative distance, the same way the
    // sun map's slope bias looked right at 2048 and was broken at 1024.
    [Theory]
    [InlineData(5f)]
    [InlineData(12f)]
    public void The_Ramp_Is_The_Same_Width_In_Texels_At_Other_Caster_Distances(float receiverDistance)
    {
        Rig rig = new(receiverDistance);
        float texel = ShadowSettings.FireTexelWorldAt(receiverDistance);

        float? support = RampWidth(rig.Visibility, rig.Dark, rig.Lit, FloorLevel, CeilingLevel);
        Assert.NotNull(support);
        Assert.InRange(support.Value, MinRampTexels * texel, MaxRampTexels * texel);

        float? width = RampWidth(rig.Visibility, rig.Dark, rig.Lit);
        Assert.NotNull(width);
        Assert.InRange(width.Value, (MinRampTexels - (2f * InsetTexels)) * texel, MaxRampTexels * texel);
    }

    // B5's synthetic negative, part one: the same measurement run against the exact ray-box geometry - a hard edge,
    // no filter at all - must report a width of essentially zero. If this ever reports a soft edge, the measurement
    // above has stopped measuring softness and is measuring something else.
    [Fact]
    public void The_Ramp_Measurement_Reports_A_Hard_Edge_As_Zero_Wide()
    {
        Rig rig = new(ReceiverDistance);
        float? width = RampWidth(rig.GeometricVisibility, rig.Dark, rig.Lit);
        Assert.NotNull(width);
        Assert.True(
            width.Value < 0.1f * ShadowSettings.FireTexelWorldAt(ReceiverDistance),
            $"A step function measured {width.Value} units wide; the measurement is not measuring the ramp.");
    }

    // B5's synthetic negative, part two: with nothing in the atlas to shadow anything, there is no crossing at all and
    // the measurement refuses to report a width rather than inventing one.
    [Fact]
    public void The_Ramp_Measurement_Reports_No_Edge_When_Everything_Is_Lit()
    {
        Rig rig = new(ReceiverDistance, occluder: false);
        Assert.True(rig.Visibility(rig.Dark) > HighLevel, "The scene must be fully lit for this negative.");
        Assert.Null(RampWidth(rig.Visibility, rig.Dark, rig.Lit));
    }

    // B3: the two derived relations, asserted as relations and not as restated numbers.
    [Fact]
    public void The_Slope_Bias_And_The_Tap_Count_Are_Derived_From_The_Tap_Geometry()
    {
        Assert.Equal(FireShadowList.TapOffsetTexels + 1f, FireShadowList.SlopeBiasTexels);
        Assert.Equal(ShadowSettings.FireTapsPerSide * ShadowSettings.FireTapsPerSide, ShadowSettings.FireTaps);
    }

    // B4: the furthest a tap can reach from the sample - the grid's corner offset plus the half texel of its hardware
    // 2x2, rounded up to a whole texel - stays inside the gutter, so the clamp to the tile's inner rect never has to
    // save a tap from the neighbouring tile. Measured from TapOffsetTexels, NOT from SlopeBiasTexels: the bias happens
    // to be the same expression today, but it is a DEPTH offset and decoupling the two would leave this test passing
    // while it measured nothing about the kernel.
    // A_Kernel_At_A_Face_Edge_Never_Reads_The_Neighbouring_Tile checks the clamp itself.
    [Fact]
    public void The_Kernel_Reach_Stays_Inside_The_Tile_Gutter()
    {
        float reach = FireShadowList.TapOffsetTexels + 1f;
        Assert.True(
            reach <= ShadowSettings.TileGutter,
            $"A tap reaches {reach} texels, past the {ShadowSettings.TileGutter} texel gutter.");
    }

    // The world distance between the LowLevel and HighLevel crossings along the segment dark -> lit, or null when the
    // segment does not span both levels.
    private static float? RampWidth(Func<Vector3, float> visibility, Vector3 dark, Vector3 lit, float lowLevel = LowLevel, float highLevel = HighLevel)
    {
        if (visibility(dark) >= lowLevel || visibility(lit) <= highLevel)
        {
            return null;
        }

        float low = Crossing(visibility, dark, lit, lowLevel);
        float high = Crossing(visibility, dark, lit, highLevel);
        return (high - low) * (lit - dark).Length;
    }

    // The parameter at which visibility rises past `level`, by bisection.
    private static float Crossing(Func<Vector3, float> visibility, Vector3 dark, Vector3 lit, float level)
    {
        float lo = 0f;
        float hi = 1f;
        for (int i = 0; i < 48; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (visibility(Vector3.Lerp(dark, lit, mid)) < level)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return 0.5f * (lo + hi);
    }

    // A box east of the fire, a ground receiver line further east, and a scan across the box's shadow from deep
    // inside it to well outside it.
    private sealed class Rig
    {
        private readonly FireShadowMirror.Box _box;
        private readonly FireShadowMirror.Atlas _atlas;
        private readonly FireShadowList _list;

        public Rig(float receiverDistance, bool occluder = true)
        {
            _box = new FireShadowMirror.Box(
                Fire + new Vector3(OccluderDistance, -Fire.Y, -0.5f),
                Fire + new Vector3(OccluderDistance + 1f, 1f, 0.5f));

            FireShadowSlot slot = new()
            {
                LightIndex = 0,
                Position = Fire,
                Near = FireShadowMirror.Near,
                Far = FireShadowMirror.FarFor(Radius),
                Strength = 1f,
            };

            ShadowFrame frame = ShadowFrame.None;
            Span<FireShadowSlot> fires = frame.Fires;
            fires[0] = slot;
            frame.FireCount = 1;
            _list = FireShadowList.FromFrame(in frame);
            _atlas = new FireShadowMirror.Atlas(ShadowSettings.AtlasResolution);
            FireShadowMirror.Box[] boxes = occluder ? [_box] : [];
            for (int face = 0; face < 4; face++)
            {
                _atlas.RenderTile(in slot, 0, face, boxes, ground: occluder);
            }

            // The umbra's geometric edge is at half the box's width scaled by receiver / occluder; the scan spans it
            // with room on both sides for a kernel several texels wide.
            float edge = 0.5f * receiverDistance / OccluderDistance;
            Dark = new Vector3(Fire.X + receiverDistance, 0f, Fire.Z + 0.25f * edge);
            Lit = new Vector3(Fire.X + receiverDistance, 0f, Fire.Z + 2.5f * edge);
        }

        public Vector3 Dark { get; }

        public Vector3 Lit { get; }

        public float Visibility(Vector3 p) => FireShadowMirror.Visibility(_list, _atlas, 0, p, Vector3.UnitY);

        // The same scene without any filtering: 0 if the ray from the fire meets the box, 1 if it does not.
        public float GeometricVisibility(Vector3 p)
        {
            Vector3 toReceiver = p - Fire;
            float length = toReceiver.Length;
            return FireShadowMirror.RayBox(Fire, toReceiver / length, _box.Min, _box.Max) < length ? 0f : 1f;
        }
    }
}
