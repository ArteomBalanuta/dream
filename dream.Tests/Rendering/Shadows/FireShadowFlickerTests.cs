using Dream.Rendering.Core;
using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.Shadows;
using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Rendering.Shadows;

// GOAL 5, THE LITERAL GATE: "real fire at any given moment flickers with varying intensity, and consequently casts
// different shadows."
//
// The chain under test is the production one, with nothing stubbed but the GPU: PlacedLightIndex.Collect (the flicker is
// applied to the brazier's position, radius and colour) -> LightSelection.SelectNearest (the frame's LightList) -> the shadow
// slot built from that list entry (origin = the flickered position, far = Radius * 1.15, the same near/far for the caster
// and the lookup) -> FireShadowList -> the atlas rendered from the flickered origin -> the lookup. If anyone freezes the fire's
// shadow (feeds the placed lights a null clock, pins the slot to the base position, fixes the far plane), a test below fails.
//
// Note on DREAM_FX_CLOCK: that pin freezes clouds, wind and the boundary mist; it deliberately does NOT pin the fire
// (Render.Lighting.cs FxClockPin: "the FX simulation and the torch/halo/flame/ember light collectors read the presentation
// clock directly"). So the two clocks here are the presentation clock a capture would hand PlacedLightIndex.Collect, which is
// exactly what PlayScene.PlacedLights.cs passes (pinned below).
public class FireShadowFlickerTests
{
    private readonly ITestOutputHelper _output;

    public FireShadowFlickerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Two instants with clearly different flames (found by scanning the clock; the test prints what it got).
    private const double ClockA = 0.10;
    private const double ClockB = 0.31;

    private static readonly Vector3 Camera = new(8.5f, 6f, 14f);

    private readonly record struct Shot(FireShadowSlot Slot, Vector3 BasePosition, float BaseRadius, float Intensity);

    // A flat 56 x 56 floor with one authored brazier in the bucket the camera looks at; the production collect-and-select path.
    private static Shot Capture(double? clockSeconds)
    {
        List<Cube> cubes = [];
        for (int x = 0; x < 56; x++)
        {
            for (int z = 0; z < 56; z++)
            {
                cubes.Add(new Cube(isEmpty: false, isSolid: true)
                {
                    Position = new Vector3(x + 0.5f, 0.5f, z + 0.5f),
                    MaterialId = GeometryMaterialId.Grass,
                });
            }
        }

        GeometryRenderExport export = new(DirtyCells: [], Cells: [], Blocks: [], CompatibilityCubes: cubes) { StaticContentVersion = 1 };
        PlacedLightIndex index = new();
        LightCollector lights = new();
        index.Collect(in export, Camera, lights, sunIntensity: 0f, clockSeconds);
        PlacedLight chosen = index.Placements.OrderBy(p => Vector3.DistanceSquared(p.Position, Camera)).First();

        Matrix4 view = Matrix4.LookAt(Camera, chosen.Position, Vector3.UnitY);
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(1.2f, 1f, 0.1f, 200f);
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(in view, in projection);
        LightList list = default;
        LightSelection.SelectNearest(lights.Lights, Camera, in frustum, ref list);

        // The nearest selected light to the brazier is the brazier (the list is sorted nearest to the camera first).
        int selected = -1;
        float best = float.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            float distance = Vector3.Distance(list.PositionRadius[i].Xyz, chosen.Position);
            if (distance < best)
            {
                best = distance;
                selected = i;
            }
        }

        // The nearest selected light can only be the brazier itself: the next one is a bucket away, and the flame's
        // wander can move this one by at most the diagonal of its own offset box.
        float wanderReach = MathF.Sqrt((2f * FireFlicker.OffsetLateral * FireFlicker.OffsetLateral)
            + (FireFlicker.OffsetVertical * FireFlicker.OffsetVertical));
        Assert.True(best <= wanderReach + 1e-4f, $"The brazier must be in the frame's selected lights (nearest was {best}).");
        Vector4 positionRadius = list.PositionRadius[selected];
        FireShadowSlot slot = new()
        {
            LightIndex = selected,
            Position = positionRadius.Xyz,
            Near = FireShadowMirror.Near,
            Far = FireShadowMirror.FarFor(positionRadius.W),
            Strength = 1f,
        };
        return new Shot(slot, chosen.Position, chosen.Radius, list.Color[selected].X / chosen.Color.X);
    }

    private static FireShadowList BlockOf(in FireShadowSlot slot)
    {
        ShadowFrame frame = ShadowFrame.None;
        Span<FireShadowSlot> fires = frame.Fires;
        fires[0] = slot;
        frame.FireCount = 1;
        return FireShadowList.FromFrame(in frame);
    }

    [Fact]
    public void Two_Different_Instants_Give_Different_Face_Origins_And_Far_Planes()
    {
        Shot a = Capture(ClockA);
        Shot b = Capture(ClockB);
        FireShadowList blockA = BlockOf(a.Slot);
        FireShadowList blockB = BlockOf(b.Slot);

        Vector3 originA = blockA.Light[0].Xyz;
        Vector3 originB = blockB.Light[0].Xyz;
        float farA = blockA.Range[0].Y;
        float farB = blockB.Range[0].Y;
        _output.WriteLine($"base {a.BasePosition}, radius {a.BaseRadius}");
        _output.WriteLine($"t={ClockA}: origin {originA} (offset {originA - a.BasePosition}), far {farA}, intensity x{a.Intensity:0.000}");
        _output.WriteLine($"t={ClockB}: origin {originB} (offset {originB - b.BasePosition}), far {farB}, intensity x{b.Intensity:0.000}");

        Assert.NotEqual(originA, originB);
        Assert.NotEqual(farA, farB);
        // Real, but bounded by the flicker's own contract: the positional wobble is at most OffsetLateral / OffsetVertical,
        // the radius breathes inside 1 +- RadiusFollow * amplitude.
        Vector3 moved = originA - originB;
        Assert.True(MathF.Abs(moved.X) <= 2f * FireFlicker.OffsetLateral + 1e-5f);
        Assert.True(MathF.Abs(moved.Y) <= 2f * FireFlicker.OffsetVertical + 1e-5f);
        Assert.True(MathF.Abs(moved.Z) <= 2f * FireFlicker.OffsetLateral + 1e-5f);
        float lo = a.BaseRadius * (1f - FireFlicker.RadiusFollow * FireFlicker.BrazierAmplitude) * 1.15f;
        float hi = a.BaseRadius * (1f + FireFlicker.RadiusFollow * FireFlicker.BrazierAmplitude) * 1.15f;
        Assert.InRange(farA, lo - 1e-4f, hi + 1e-4f);
        Assert.InRange(farB, lo - 1e-4f, hi + 1e-4f);
        Assert.NotEqual(a.Intensity, b.Intensity);
    }

    [Fact]
    public void The_Far_Plane_Is_The_Flickered_Radius_Times_The_Shared_Margin_Not_A_Constant()
    {
        foreach (double clock in new[] { 0.0, 0.10, 0.31, 0.77, 1.9 })
        {
            Shot shot = Capture(clock);
            FireFlickerSample sample = FireFlicker.Sample(
                FireFlickerKind.Brazier, shot.BasePosition, clock, FireFlicker.SeedFor(shot.BasePosition));
            Assert.Equal(shot.BaseRadius * sample.RadiusScale * 1.15f, shot.Slot.Far, 3);
            Assert.Equal(shot.BasePosition + sample.Offset, shot.Slot.Position);
        }
    }

    // The fixed occluder/receiver pair. The occluder is a box 3..4 units east of the (base) fire, the receivers lie on the
    // ground 8 units east, and we find where along z the shadow ends, both from the atlas lookup and from exact geometry.
    private static FireShadowMirror.Box OccluderFor(Vector3 basePosition) =>
        new(basePosition + new Vector3(3f, -basePosition.Y, -0.5f), basePosition + new Vector3(4f, 2f - basePosition.Y, 0.5f));

    // Exact: the z at which the segment from `origin` to the ground point (receiverX, 0, z) leaves the box's shadow.
    private static float ExactEdge(Vector3 origin, FireShadowMirror.Box box, float receiverX)
    {
        float dark = origin.Z + 0.2f;
        float lit = origin.Z + 4f;
        for (int i = 0; i < 60; i++)
        {
            float mid = 0.5f * (dark + lit);
            Vector3 receiver = new(receiverX, 0f, mid);
            Vector3 toReceiver = receiver - origin;
            float length = toReceiver.Length;
            bool occluded = FireShadowMirror.RayBox(origin, toReceiver / length, box.Min, box.Max) < length;
            if (occluded)
            {
                dark = mid;
            }
            else
            {
                lit = mid;
            }
        }

        return 0.5f * (dark + lit);
    }

    // From the lookup: the z where the visibility crosses one half.
    private static float MeasuredEdge(in FireShadowSlot slot, FireShadowMirror.Box box, float receiverX)
    {
        FireShadowList list = BlockOf(in slot);
        FireShadowMirror.Atlas atlas = new(ShadowSettings.AtlasResolution);
        for (int face = 0; face < 4; face++)
        {
            atlas.RenderTile(in slot, 0, face, [box]);
        }

        float dark = slot.Position.Z + 0.2f;
        float lit = slot.Position.Z + 4f;
        Assert.True(FireShadowMirror.Visibility(list, atlas, slot.LightIndex, new Vector3(receiverX, 0f, dark), Vector3.UnitY) < 0.01f, "The receiver at the dark end must be shadowed.");
        Assert.True(FireShadowMirror.Visibility(list, atlas, slot.LightIndex, new Vector3(receiverX, 0f, lit), Vector3.UnitY) > 0.99f, "The receiver at the lit end must be lit.");
        for (int i = 0; i < 40; i++)
        {
            float mid = 0.5f * (dark + lit);
            if (FireShadowMirror.Visibility(list, atlas, slot.LightIndex, new Vector3(receiverX, 0f, mid), Vector3.UnitY) < 0.5f)
            {
                dark = mid;
            }
            else
            {
                lit = mid;
            }
        }

        return 0.5f * (dark + lit);
    }

    [Fact]
    public void For_A_Fixed_Occluder_And_Receiver_The_Shadow_Boundary_Moves_By_A_Nonzero_Bounded_Amount_As_The_Fire_Flickers()
    {
        Shot a = Capture(ClockA);
        Shot b = Capture(ClockB);
        FireShadowMirror.Box occluder = OccluderFor(a.BasePosition);
        float receiverX = a.BasePosition.X + 8f;

        float measuredA = MeasuredEdge(a.Slot, occluder, receiverX);
        float measuredB = MeasuredEdge(b.Slot, occluder, receiverX);
        float exactA = ExactEdge(a.Slot.Position, occluder, receiverX);
        float exactB = ExactEdge(b.Slot.Position, occluder, receiverX);
        float baseEdge = ExactEdge(a.BasePosition, occluder, receiverX);
        float texelAtReceiver = 2f * 8f / (ShadowSettings.TileSide - 2f * ShadowSettings.TileGutter);

        _output.WriteLine($"shadow edge z (receiver 8 units out, occluder 3 units out): unflickered {baseEdge:0.0000}");
        _output.WriteLine($"  t={ClockA}: lookup {measuredA:0.0000}, exact {exactA:0.0000}");
        _output.WriteLine($"  t={ClockB}: lookup {measuredB:0.0000}, exact {exactB:0.0000}");
        _output.WriteLine($"  moved: lookup {measuredB - measuredA:0.0000} units, exact {exactB - exactA:0.0000} units, one texel there is {texelAtReceiver:0.0000}");

        float movedExact = exactB - exactA;
        float movedMeasured = measuredB - measuredA;
        // The parallax bound: an origin shift of s moves the boundary of an occluder at O seen on a receiver at R by s (R/O - 1),
        // here R/O = 8/3. The wobble spans at most 2 x OffsetLateral in z (and a little in x and y).
        float parallax = 8f / 3f - 1f;
        float bound = 2f * FireFlicker.OffsetLateral * parallax + 2f * FireFlicker.OffsetVertical + 2f * FireFlicker.OffsetLateral * 0.5f;

        Assert.NotEqual(0f, movedExact);
        Assert.True(MathF.Abs(movedExact) > 0.005f, $"The exact boundary barely moved ({movedExact}); pick instants with a larger wobble.");
        Assert.True(MathF.Abs(movedExact) <= bound, $"Moved {movedExact}, bound {bound}.");
        // The lookup tracks the exact motion to within a couple of texels: the shadow really moved, and to the right place.
        Assert.True(MathF.Abs(movedMeasured) > 0.005f, $"The sampled shadow did not move ({movedMeasured}).");
        Assert.InRange(movedMeasured, movedExact - 2f * texelAtReceiver, movedExact + 2f * texelAtReceiver);
        Assert.InRange(measuredA, exactA - 2.5f * texelAtReceiver, exactA + 2.5f * texelAtReceiver);
        Assert.InRange(measuredB, exactB - 2.5f * texelAtReceiver, exactB + 2.5f * texelAtReceiver);
    }

    // The whole point of the wander, measured end to end: sweep the presentation clock and watch where the shadow's
    // 0.5-visibility boundary lands, through the production collect -> select -> slot -> atlas -> lookup chain.
    //
    // Two things are asserted about the same sweep, because they pull in opposite directions.
    //   A5 the boundary must TRAVEL: at least four texels of the fire map at the receiver's distance. (Before this
    //      wave the two instants above moved it 0.045 units, about 1.4 texels, and it read as a static shadow.)
    //   A6 the boundary must not STEP: at 60 Hz no single frame may move it by more than MaxFrameStepTexels, or a
    //      hard edge would visibly jump from texel to texel. The other half of that claim - that the penumbra is
    //      wide enough for that much motion per frame to read as a drift - is the fire lookup's kernel, elsewhere.
    // The upper bound on the travel is the flicker's own contract: the lateral swing is 2 x OffsetLateral, amplified
    // by the parallax of an occluder at 3 and a receiver at 8, plus what the vertical and the along-ray components
    // of the wander can add.
    //
    // A6 is asserted on the EXACT boundary, which is where the flame's own speed lives. The sampled boundary carries
    // the atlas's quantisation on top of it - a 0.5-crossing found by bisecting a lookup over 24 bit depth texels
    // wanders by a texel or so between neighbouring frames whatever the flame does - so the sampled figure is
    // asserted separately, against the exact one plus that quantisation. Both are printed.
    //
    // Why this many texels per frame and not fewer. The flame's worst-case speed on one axis is
    // OffsetLateral * 2 pi * sum(weight * frequency) = 1.11 units/s. On this rig the boundary responds to the
    // flame's z at the parallax gain 8/3 - 1 and to its x at a further (R - O) / O^2 * (box z - fire z), so the
    // sensitivity is about 1.95, not the 1.67 of the z term alone: 1.11 * 1.95 / 60 = 0.036 units = 1.14 texels.
    // The measured worst frame is 1.08 texels. The gate is set above that and below what the design needs it to
    // stay under - a third of the fire lookup's penumbra, which is four texels wide - so a flame that got a fifth
    // faster, from a larger offset or a higher wander frequency, would fail here.
    private const float MaxFrameStepTexels = 1.25f;

    [Fact]
    public void Over_A_Clock_Sweep_The_Shadow_Boundary_Travels_Several_Texels_And_Never_Jumps_A_Whole_One()
    {
        const double Step = 1.0 / 60.0;
        (float[] sampled, float[] exact) = EdgeSweep((3 * 60) + 1, Step, frozen: false);
        float texel = ShadowSettings.FireTexelWorldAt(8f);
        float spread = sampled.Max() - sampled.Min();
        float sampledStep = BiggestFrameStep(sampled);
        float exactStep = BiggestFrameStep(exact);

        float parallax = (8f / 3f) - 1f;
        float bound = (2f * FireFlicker.OffsetLateral * parallax) + (2f * FireFlicker.OffsetVertical) + FireFlicker.OffsetLateral;
        _output.WriteLine($"one fire-map texel at 8 units is {texel:0.0000}");
        _output.WriteLine($"  travel over 3 s: sampled {spread:0.0000} units = {spread / texel:0.00} texels (bound {bound:0.0000});"
            + $" exact {exact.Max() - exact.Min():0.0000}");
        _output.WriteLine($"  worst 60 Hz frame step: exact {exactStep:0.0000} = {exactStep / texel:0.00} texels,"
            + $" sampled {sampledStep:0.0000} = {sampledStep / texel:0.00} texels");

        Assert.True(spread >= 4f * texel, $"the boundary travelled {spread / texel:0.00} texels");
        Assert.True(spread <= bound, $"travelled {spread}, which is past the flicker's own bound {bound}");
        Assert.True(exactStep <= MaxFrameStepTexels * texel, $"a frame moved the boundary {exactStep / texel:0.00} texels");
        Assert.True(sampledStep <= exactStep + (1.0f * texel), $"the lookup added {(sampledStep - exactStep) / texel:0.00} texels of its own");
    }

    private static float BiggestFrameStep(float[] edges)
    {
        float worst = 0f;
        for (int i = 1; i < edges.Length; i++)
        {
            worst = MathF.Max(worst, MathF.Abs(edges[i] - edges[i - 1]));
        }

        return worst;
    }

    // The synthetic negative for the sweep above: freeze the fire and the very same measurement returns a spread of
    // exactly zero. So the travel it reports is the flame moving, not binary-search noise or atlas quantisation.
    [Fact]
    public void The_Same_Sweep_Over_A_Frozen_Fire_Measures_Exactly_No_Travel()
    {
        (float[] sampled, float[] exact) = EdgeSweep(60, 1.0 / 60.0, frozen: true);
        _output.WriteLine($"frozen edge {sampled[0]:0.000000}, sampled spread {sampled.Max() - sampled.Min():0.000000},"
            + $" exact spread {exact.Max() - exact.Min():0.000000}");
        Assert.Equal(0f, sampled.Max() - sampled.Min());
        Assert.Equal(0f, exact.Max() - exact.Min());
    }

    // The shadow boundary at each of `samples` clock instants, on the fixed rig: occluder 3 units east of the fire,
    // receivers on the ground 8 units east. Both the atlas lookup's 0.5-visibility crossing and the exact geometric
    // boundary, from the same slots. `frozen` hands the placed lights no clock at all.
    private static (float[] Sampled, float[] Exact) EdgeSweep(int samples, double step, bool frozen)
    {
        Shot reference = Capture(frozen ? null : 0.0);
        FireShadowMirror.Box occluder = OccluderFor(reference.BasePosition);
        float receiverX = reference.BasePosition.X + 8f;
        float[] sampled = new float[samples];
        float[] exact = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            Shot shot = Capture(frozen ? null : i * step);
            sampled[i] = MeasuredEdge(shot.Slot, occluder, receiverX);
            exact[i] = ExactEdge(shot.Slot.Position, occluder, receiverX);
        }

        return (sampled, exact);
    }

    // What the gate catches. Freeze the fire (hand the placed lights no clock) and both instants are identical, so every
    // assertion above would fail. This test documents the sensitivity: it is the mutation, kept as a permanent check.
    [Fact]
    public void A_Frozen_Fire_Gives_Identical_Slots_At_Any_Two_Instants_Which_Is_Exactly_What_The_Gate_Rejects()
    {
        Shot a = Capture(null);
        Shot b = Capture(null);
        Assert.Equal(a.Slot.Position, b.Slot.Position);
        Assert.Equal(a.Slot.Far, b.Slot.Far);
        Assert.Equal(a.BasePosition, a.Slot.Position);
        Assert.Equal(a.BaseRadius * 1.15f, a.Slot.Far, 4);

        // ...and the flickering one differs from the frozen one.
        Shot flickering = Capture(ClockA);
        Assert.NotEqual(a.Slot.Position, flickering.Slot.Position);
        Assert.NotEqual(a.Slot.Far, flickering.Slot.Far);
    }

    [Fact]
    public void The_Scene_Feeds_The_Placed_Fires_The_Presentation_Clock_So_Production_Is_Not_Frozen()
    {
        string placed = FireShadowSamplingMirrorTests.Read("dream", "Client", "Scenes", "PlayScene.PlacedLights.cs");
        Assert.Contains("_clientPresentationTimeSeconds);", placed);
        Assert.Contains("lights,", placed);

        // And PlacedLightIndex honours it: with a clock the brazier is built from FireFlicker.Sample, without one it is the base light.
        string index = FireShadowSamplingMirrorTests.Read("dream", "Rendering", "Lighting", "PlacedLightIndex.cs");
        Assert.Contains("FireFlicker.Sample(FireFlickerKind.Brazier, placed.Position, clock, seed)", index);
        Assert.Contains("int seed = FireFlicker.SeedFor(placed.Position);", index);
        Assert.Contains("placed.Position + fire.Offset, placed.Radius * fire.RadiusScale, placed.Color * (strength * fire.Intensity)", index);
    }

    [Fact]
    public void The_Flickered_Position_And_Far_Reach_The_Caster_And_The_Lookup_From_One_Slot()
    {
        // One value feeds both sides: the block's origin and planes ARE the slot's, the pass renders from slot.Position with
        // slot.Near/Far, the shader reads uFireShadowLight/uFireShadowRange. A round trip through the block loses nothing.
        Shot shot = Capture(ClockB);
        FireShadowList block = BlockOf(shot.Slot);

        Assert.Equal(shot.Slot.Position, block.Light[0].Xyz);
        Assert.Equal(shot.Slot.Near, block.Range[0].X);
        Assert.Equal(shot.Slot.Far, block.Range[0].Y);
        Assert.Equal((float)shot.Slot.LightIndex, block.Light[0].W);
    }
}
