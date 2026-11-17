using System.Globalization;
using System.Text.RegularExpressions;
using Dream.Rendering.Shadows;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// A CPU mirror of dreamFireVisibility (shaders/fireShadows.glsl) over a CPU-rendered fire atlas, the house pattern for
// shaders (SunShadowLookupMirrorTests, CloudLayer). The atlas is rendered through the real matrix pipeline a caster pass
// uses (Matrix4.LookAt + CreatePerspectiveFieldOfView with ShadowSettings.FaceFovRadians, depth 24 bit), while the lookup is
// the shader's arithmetic, so the two are independent and a convention disagreement shows up as a wrong shadow.
public class FireShadowSamplingMirrorTests
{
    private static readonly Vector3 Fire = new(10.3f, 1f, -4.2f);
    private const float Radius = 10f;

    // ---- the scene: the ground plane y = 0 and a box 3..4 units east of the fire ----

    private static readonly FireShadowMirror.Box BoxEast = new(Fire + new Vector3(3f, -1f, -0.5f), Fire + new Vector3(4f, 1f, 0.5f));

    private static FireShadowSlot Slot(Vector3 position, float radius, int lightIndex = 0, float strength = 1f) => new()
    {
        LightIndex = lightIndex,
        Position = position,
        Near = FireShadowMirror.Near,
        Far = FireShadowMirror.FarFor(radius),
        Strength = strength,
    };

    private static ShadowFrame FrameOf(params FireShadowSlot[] slots)
    {
        ShadowFrame frame = ShadowFrame.None;
        Span<FireShadowSlot> fires = frame.Fires;
        for (int i = 0; i < slots.Length; i++)
        {
            fires[i] = slots[i];
        }

        frame.FireCount = slots.Length;
        return frame;
    }

    private sealed class Rig
    {
        public Rig(FireShadowSlot slot, FireShadowMirror.Box[] boxes, FireShadowMirror.Plane[]? planes = null, bool ground = true)
        {
            Slot = slot;
            ShadowFrame frame = FrameOf(slot);
            Frame = frame;
            List = FireShadowList.FromFrame(in frame);
            Atlas = new FireShadowMirror.Atlas(ShadowSettings.AtlasResolution);
            for (int face = 0; face < 4; face++)
            {
                Atlas.RenderTile(in slot, 0, face, boxes, ground, planes ?? []);
            }
        }

        public FireShadowSlot Slot { get; }
        public ShadowFrame Frame { get; }
        public FireShadowList List { get; }
        public FireShadowMirror.Atlas Atlas { get; }

        public float Visibility(Vector3 fragPos, Vector3 n, int light = 0) =>
            FireShadowMirror.Visibility(List, Atlas, light, fragPos, n);
    }

    private static readonly Lazy<Rig> Shared = new(() => new Rig(Slot(Fire, Radius), [BoxEast]));

    private static bool OccludedByBox(Vector3 receiver, float margin)
    {
        Vector3 toReceiver = receiver - Fire;
        float length = toReceiver.Length;
        float t = FireShadowMirror.RayBox(Fire, toReceiver / length, BoxEast.Min + new Vector3(margin), BoxEast.Max - new Vector3(margin));
        return t < length;
    }

    // ---- the lookup against the scene ----

    [Fact]
    public void Open_Ground_Is_Fully_Lit_In_Every_Direction_And_At_Every_Distance_The_Ground_Never_Shadows_Itself()
    {
        Rig rig = Shared.Value;
        int checkedPoints = 0;
        for (float angle = 0f; angle < 360f; angle += 7.3f)
        {
            for (float distance = 1.6f; distance <= 9.9f; distance += 0.37f)
            {
                Vector3 p = new(Fire.X + distance * MathF.Cos(MathHelper.DegreesToRadians(angle)), 0f, Fire.Z + distance * MathF.Sin(MathHelper.DegreesToRadians(angle)));
                if (OccludedByBox(p, -0.4f) || (p.X > Fire.X + 2.5f && p.X < Fire.X + 4.5f && MathF.Abs(p.Z - Fire.Z) < 1f))
                {
                    continue;
                }

                float v = rig.Visibility(p, Vector3.UnitY);
                Assert.True(v > 0.999f, $"Acne on open ground at angle {angle}, distance {distance}: visibility {v}.");
                checkedPoints++;
            }
        }

        Assert.True(checkedPoints > 600, $"Only {checkedPoints} ground points were checked.");
    }

    [Fact]
    public void Ground_Behind_The_Box_Is_Dark_And_Ground_Beside_It_Is_Lit()
    {
        Rig rig = Shared.Value;
        int dark = 0;
        int lit = 0;
        for (float dx = -9f; dx <= 9.5f; dx += 0.11f)
        {
            for (float dz = -4f; dz <= 4f; dz += 0.11f)
            {
                Vector3 p = new(Fire.X + dx, 0f, Fire.Z + dz);
                if (dx > 2.8f && dx < 4.2f && MathF.Abs(dz) < 0.7f)
                {
                    continue; // the box's own footprint and its contact skirt, covered by the contact test
                }

                if (MathF.Sqrt(dx * dx + dz * dz) < 1.6f || MathF.Sqrt(dx * dx + dz * dz) > 9.8f)
                {
                    continue;
                }

                if (OccludedByBox(p, 0.06f))
                {
                    Assert.True(rig.Visibility(p, Vector3.UnitY) < 0.001f, $"Not dark at dx {dx}, dz {dz}.");
                    dark++;
                }
                else if (!OccludedByBox(p, -0.15f))
                {
                    Assert.True(rig.Visibility(p, Vector3.UnitY) > 0.999f, $"Not lit at dx {dx}, dz {dz}.");
                    lit++;
                }
            }
        }

        Assert.True(dark > 80, $"Only {dark} points in the shadow.");
        Assert.True(lit > 1500, $"Only {lit} lit points.");
    }

    [Fact]
    public void The_Contact_Point_Behind_The_Box_Stays_Dark_The_Bias_Does_Not_Float_The_Shadow_Off_Its_Caster()
    {
        Rig rig = Shared.Value;
        // 0.12 units (about 10 texels at this distance) behind the box's back face, on the ground.
        foreach (float behind in new[] { 0.12f, 0.3f })
        {
            Vector3 p = new(Fire.X + 4f + behind, 0f, Fire.Z);
            Assert.True(OccludedByBox(p, 0f));
            Assert.True(rig.Visibility(p, Vector3.UnitY) < 0.001f);
        }
    }

    [Fact]
    public void The_Face_Of_The_Box_That_Faces_The_Fire_Is_Lit_It_Does_Not_Shadow_Itself()
    {
        Rig rig = Shared.Value;
        int checkedPoints = 0;
        for (float y = 0.08f; y < 1.92f; y += 0.09f)
        {
            for (float z = -0.45f; z <= 0.45f; z += 0.07f)
            {
                Vector3 p = new(Fire.X + 3f, y, Fire.Z + z);
                // The face plane itself: the receiver sits exactly on the surface the caster rendered.
                Assert.True(rig.Visibility(p, -Vector3.UnitX) > 0.999f, $"Self-shadow on the lit face at y {y}, z {z}.");
                checkedPoints++;
            }
        }

        Assert.True(checkedPoints > 200);
    }

    [Fact]
    public void The_Polar_Cone_Above_And_Below_The_Fire_Is_Unshadowed_The_Safe_Direction()
    {
        Rig rig = Shared.Value;
        Assert.Equal(1f, rig.Visibility(Fire + new Vector3(0.3f, -1f, 0.2f), Vector3.UnitY), 6);
        Assert.Equal(1f, rig.Visibility(Fire + new Vector3(-0.5f, 2f, 0.1f), -Vector3.UnitY), 6);
        // The boundary is |d.y| >= max(|d.x|, |d.z|): 45 degrees.
        Assert.Equal(1f, rig.Visibility(Fire + new Vector3(2f, -2f, 0f), Vector3.UnitY), 6);
        // The fire's own position (a zero offset) must not divide by zero.
        Assert.Equal(1f, rig.Visibility(Fire, Vector3.UnitY), 6);
    }

    [Fact]
    public void A_Light_That_Is_Not_A_Casting_Fire_Is_Fully_Lit()
    {
        Rig rig = Shared.Value;
        Vector3 inShadow = new(Fire.X + 6f, 0f, Fire.Z);
        Assert.True(rig.Visibility(inShadow, Vector3.UnitY, light: 0) < 0.001f);
        Assert.Equal(1f, rig.Visibility(inShadow, Vector3.UnitY, light: 5));
    }

    [Fact]
    public void The_Slot_Is_Found_By_Its_Light_List_Index_Not_By_Position_In_The_Block()
    {
        Rig seed = Shared.Value;
        ShadowFrame frame = FrameOf(Slot(new Vector3(-50f, 1f, -50f), Radius, lightIndex: 9), Slot(Fire, Radius, lightIndex: 3));
        FireShadowList list = FireShadowList.FromFrame(in frame);
        FireShadowMirror.Atlas atlas = new(ShadowSettings.AtlasResolution);
        FireShadowSlot second = frame.Fires[1];
        for (int face = 0; face < 4; face++)
        {
            atlas.RenderTile(in second, 1, face, [BoxEast]);
        }

        Vector3 inShadow = new(Fire.X + 6f, 0f, Fire.Z);
        Assert.True(FireShadowMirror.Visibility(list, atlas, 3, inShadow, Vector3.UnitY) < 0.001f);
        // Light 9 is the first slot, whose atlas tiles are empty and whose position is elsewhere: nothing shadows it.
        Assert.Equal(1f, FireShadowMirror.Visibility(list, atlas, 9, inShadow, Vector3.UnitY));
        Assert.NotNull(seed);
    }

    [Fact]
    public void Strength_Zero_Count_Zero_And_The_Disabled_Block_Are_A_Literal_One_Even_In_Shadow()
    {
        Rig rig = Shared.Value;
        Vector3 inShadow = new(Fire.X + 6f, 0f, Fire.Z);

        FireShadowList off = rig.List;
        off.Range[0] = new Vector4(off.Range[0].X, off.Range[0].Y, off.Range[0].Z, 0f);
        Assert.Equal(1f, FireShadowMirror.Visibility(off, rig.Atlas, 0, inShadow, Vector3.UnitY));

        FireShadowList empty = FireShadowList.FromFrame(ShadowFrame.None);
        Assert.Equal(0, empty.Count);
        Assert.Equal(1f, FireShadowMirror.Visibility(empty, rig.Atlas, 0, inShadow, Vector3.UnitY));
    }

    [Fact]
    public void Strength_Scales_The_Shadow_Toward_Lit()
    {
        Rig rig = Shared.Value;
        FireShadowList half = rig.List;
        half.Range[0] = new Vector4(half.Range[0].X, half.Range[0].Y, half.Range[0].Z, 0.5f);

        Assert.Equal(0.5f, FireShadowMirror.Visibility(half, rig.Atlas, 0, new Vector3(Fire.X + 6f, 0f, Fire.Z), Vector3.UnitY), 3);
    }

    // The PCF kernel reaches a face's edge, and normal offset can push the lookup's vertical coordinate past it (the polar test
    // is made on the UNOFFSET position). Without the clamp into the tile's inner rect the kernel reads the tile above.
    [Fact]
    public void A_Kernel_At_A_Face_Edge_Never_Reads_The_Neighbouring_Tile()
    {
        Rig rig = new(Slot(Fire, Radius), []);
        // Poison the tile directly above slot 0 face 0 (slot 1 face 0): everything there is "occluded at depth 0".
        rig.Atlas.FillTile(1, 0, 0f);

        // Almost on the polar boundary on the +X face, at a grazing normal: normal offset moves the lookup upward past the tile edge.
        Vector3 p = Fire + new Vector3(3f, 2.99f, 0f);
        Vector3 n = Vector3.Normalize(new Vector3(-2.99f, 3f, 0f));
        FireShadowMirror.Probe probe = FireShadowMirror.ProbeLookup(rig.List, 0, p, n);

        // The unclamped lookup would sit past the tile's top (v > 1); the clamped taps stay a gutter inside it.
        Assert.True(probe.UnclampedUv.Y > rig.List.Tile[0].Y + rig.List.Tile[0].W, "The scenario must push the lookup past the tile edge.");
        Assert.True(rig.Visibility(p, n) > 0.999f, "The kernel leaked into the neighbouring tile.");
    }

    // A wall facing the fire needs the SLOPE bias: its normal is toward the light, so the normal offset is nil, yet the stored depth
    // changes with every texel along it (most at a 45 degree azimuth, where the wall is a diagonal to both faces' axes).
    [Theory]
    [InlineData(0f)]
    [InlineData(22.5f)]
    [InlineData(45f)]
    [InlineData(67.5f)]
    [InlineData(135f)]
    [InlineData(200f)]
    [InlineData(300f)]
    public void A_Wall_Facing_The_Fire_Is_Fully_Lit_At_Every_Azimuth_Distance_And_Tilt(float azimuthDegrees)
    {
        Vector3 axis = new(MathF.Cos(MathHelper.DegreesToRadians(azimuthDegrees)), 0f, MathF.Sin(MathHelper.DegreesToRadians(azimuthDegrees)));
        Vector3 side = new(-axis.Z, 0f, axis.X);
        int checkedPoints = 0;
        foreach (float tilt in new[] { 0f, 25f })
        {
            // The wall's normal points back at the fire, pitched by `tilt` degrees (a ramp leaning toward or away from the light).
            Vector3 normal = Vector3.Normalize(-axis * MathF.Cos(MathHelper.DegreesToRadians(tilt)) + Vector3.UnitY * MathF.Sin(MathHelper.DegreesToRadians(tilt)));
            foreach (float distance in new[] { 3f, 5.5f, 8.5f })
            {
                Vector3 centre = Fire + axis * distance;
                Rig rig = new(Slot(Fire, Radius), [], planes: [new FireShadowMirror.Plane(normal, Vector3.Dot(normal, centre))], ground: false);
                Vector3 up = Vector3.Normalize(Vector3.Cross(normal, side));
                for (float u = -distance * 0.55f; u <= distance * 0.55f; u += distance * 0.11f)
                {
                    for (float w = -distance * 0.12f; w <= distance * 0.12f; w += distance * 0.04f)
                    {
                        Vector3 p = centre + side * u + up * w;
                        Vector3 d = p - Fire;
                        if (MathF.Abs(d.Y) >= MathF.Max(MathF.Abs(d.X), MathF.Abs(d.Z)))
                        {
                            continue;
                        }

                        float v = rig.Visibility(p, normal);
                        Assert.True(v > 0.999f, $"Acne on a wall facing the fire: azimuth {azimuthDegrees}, tilt {tilt}, distance {distance}, u {u}, w {w}: visibility {v}.");
                        checkedPoints++;
                    }
                }
            }
        }

        Assert.True(checkedPoints > 100, $"Only {checkedPoints} wall points were checked.");
    }

    // The arithmetic UV must land on the texel the caster pass wrote, at the face EDGES too: a lookup that ignored the gutter
    // shrink would drift by several texels at a 40 degree azimuth. The shadow edge of a box there is compared with exact geometry.
    [Theory]
    [InlineData(40f)]
    [InlineData(-40f)]
    [InlineData(130f)]
    [InlineData(220f)]
    public void The_Shadow_Edge_Of_A_Box_Near_A_Face_Edge_Lands_Where_The_Geometry_Puts_It(float azimuthDegrees)
    {
        Vector3 axis = new(MathF.Cos(MathHelper.DegreesToRadians(azimuthDegrees)), 0f, MathF.Sin(MathHelper.DegreesToRadians(azimuthDegrees)));
        Vector3 side = new(-axis.Z, 0f, axis.X);
        Vector3 boxCentre = Fire + axis * 3.5f;
        FireShadowMirror.Box box = new(boxCentre + new Vector3(-0.3f, -1f, -0.3f), boxCentre + new Vector3(0.3f, 1f, 0.3f));
        Rig rig = new(Slot(Fire, Radius), [box]);

        // Along a line across the shadow, 8 units out, from deep inside it to well outside it.
        Vector3 start = Fire + axis * 8f;
        Vector3 end = Fire + axis * 8f + side * 1.6f;
        start.Y = end.Y = 0f;
        Assert.True(rig.Visibility(start, Vector3.UnitY) < 0.01f, "The start must be shadowed.");
        Assert.True(rig.Visibility(end, Vector3.UnitY) > 0.99f, "The end must be lit.");
        float lo = 0f;
        float hi = 1f;
        float exactLo = 0f;
        float exactHi = 1f;
        for (int i = 0; i < 40; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (rig.Visibility(Vector3.Lerp(start, end, mid), Vector3.UnitY) < 0.5f)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }

            float exactMid = 0.5f * (exactLo + exactHi);
            Vector3 receiver = Vector3.Lerp(start, end, exactMid);
            Vector3 toReceiver = receiver - Fire;
            if (FireShadowMirror.RayBox(Fire, Vector3.Normalize(toReceiver), box.Min, box.Max) < toReceiver.Length)
            {
                exactLo = exactMid;
            }
            else
            {
                exactHi = exactMid;
            }
        }

        float lengthPerParameter = (end - start).Length;
        float errorWorld = MathF.Abs(0.5f * (lo + hi) - 0.5f * (exactLo + exactHi)) * lengthPerParameter;
        float texelAtReceiver = 2f * 8f / (ShadowSettings.TileSide - 2f * ShadowSettings.TileGutter);
        Assert.True(errorWorld < 1.2f * texelAtReceiver, $"The shadow edge is {errorWorld} units off ({errorWorld / texelAtReceiver:0.0} texels) at azimuth {azimuthDegrees}.");
    }

    // The same at the VERTICAL extreme of a face: a slab hanging above the fire shades a tall wall, and the shadow's edge on the wall
    // sits at an elevation of 35 to 40 degrees, near the polar boundary where a lookup that ignored the gutter shrink on the v
    // axis would be several texels off.
    [Fact]
    public void The_Shadow_Edge_Of_A_Raised_Slab_On_A_Tall_Wall_Lands_Where_The_Geometry_Puts_It()
    {
        FireShadowMirror.Box slab = new(Fire + new Vector3(3.4f, 2.0f, -1.5f), Fire + new Vector3(3.6f, 2.4f, 1.5f));
        FireShadowMirror.Plane wall = new(-Vector3.UnitX, -(Fire.X + 5f));
        Rig rig = new(Slot(Fire, Radius), [slab], [wall], ground: false);

        Vector3 dark = new(Fire.X + 5f, Fire.Y + 3.2f, Fire.Z);
        Vector3 lit = new(Fire.X + 5f, Fire.Y + 4.1f, Fire.Z);
        Assert.True(rig.Visibility(dark, -Vector3.UnitX) < 0.01f, "The lower end must be shadowed.");
        Assert.True(rig.Visibility(lit, -Vector3.UnitX) > 0.99f, "The upper end must be lit.");
        float lo = 0f;
        float hi = 1f;
        float exactLo = 0f;
        float exactHi = 1f;
        for (int i = 0; i < 40; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (rig.Visibility(Vector3.Lerp(dark, lit, mid), -Vector3.UnitX) < 0.5f)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }

            float exactMid = 0.5f * (exactLo + exactHi);
            Vector3 toReceiver = Vector3.Lerp(dark, lit, exactMid) - Fire;
            if (FireShadowMirror.RayBox(Fire, Vector3.Normalize(toReceiver), slab.Min, slab.Max) < toReceiver.Length)
            {
                exactLo = exactMid;
            }
            else
            {
                exactHi = exactMid;
            }
        }

        float errorWorld = MathF.Abs(0.5f * (lo + hi) - 0.5f * (exactLo + exactHi)) * (lit - dark).Length;
        float texelAtWall = 2f * 5f / (ShadowSettings.TileSide - 2f * ShadowSettings.TileGutter);
        Assert.True(errorWorld < 1.2f * texelAtWall, $"The edge is {errorWorld} units off ({errorWorld / texelAtWall:0.0} texels).");
    }

    // ---- constants and conventions ----

    [Fact]
    public void The_Glsl_Constants_Equal_The_Csharp_Constants()
    {
        string source = Read("dream", "shaders", "fireShadows.glsl");
        Assert.Equal(ShadowSettings.TileGutter, GlslFloat(source, "FireShadowGutterTexels"));
        Assert.Equal(ShadowSettings.GrazingCosineFloor, GlslFloat(source, "FireShadowGrazingFloor"), 5);
        Assert.Equal(ShadowSettings.SlopeTanMax, GlslFloat(source, "FireShadowTanMax"), 5);
        Assert.Equal(ShadowSettings.NormalOffsetTexels, GlslFloat(source, "FireShadowNormalOffsetTexels"), 5);
        Assert.Equal(ShadowSettings.ConstantBiasWorld, GlslFloat(source, "FireShadowConstantBias"), 5);
        Assert.Equal(FireShadowList.SlopeBiasTexels, GlslFloat(source, "FireShadowSlopeTexels"), 5);
        Assert.Equal(FireShadowList.TapOffsetTexels, GlslFloat(source, "FireShadowTapOffset"), 5);

        // The tap count is fixed in GLSL (the block has no spare word): exactly ShadowSettings.FireTaps fetches.
        Match function = Regex.Match(source, @"float dreamFireVisibility\(int light, vec3 fragPos, vec3 n\)\s*\{(.*)\n\}", RegexOptions.Singleline);
        Assert.True(function.Success);
        Assert.Equal(ShadowSettings.FireTaps, Regex.Matches(function.Groups[1].Value, @"texture\(uFireShadowAtlas").Count);

        // The average is the reciprocal of the tap count, written in the GLSL as a division it is read back from.
        Match average = Regex.Match(function.Groups[1].Value, @"mix\(1\.0, \(1\.0 / ([0-9.]+)\) \* sum,");
        Assert.True(average.Success, "The averaging factor must be a 1.0 / <taps> division.");
        Assert.Equal(1f / ShadowSettings.FireTaps, 1f / float.Parse(average.Groups[1].Value, CultureInfo.InvariantCulture), 6);

        // Every tap is clamped into the tile's inner rect, and the grid is the full FireTapsPerSide square: each of
        // the three offsets on each axis appears FireTapsPerSide times.
        Assert.Equal(ShadowSettings.FireTaps, Regex.Matches(function.Groups[1].Value, @"clamp\(uv \+ vec2\(\s*(?:-o\.x|0\.0|o\.x),\s*(?:-o\.y|0\.0|o\.y)\), lo, hi\)").Count);
        foreach (string axis in new[] { "x", "y" })
        {
            foreach (string sign in new[] { "-o." + axis, "0.0", "o." + axis })
            {
                string pattern = axis == "x"
                    ? @"vec2\(\s*" + Regex.Escape(sign) + @","
                    : @",\s*" + Regex.Escape(sign) + @"\), lo, hi\)";
                Assert.Equal(ShadowSettings.FireTapsPerSide, Regex.Matches(function.Groups[1].Value, pattern).Count);
            }
        }
    }

    [Fact]
    public void The_Glsl_Face_Selection_And_Depth_Encode_Are_The_Ones_The_Mirror_Implements()
    {
        string source = Normalise(Read("dream", "shaders", "fireShadows.glsl"));
        Assert.Contains("if (a0.y >= max(a0.x, a0.z)) { return 1.0; }", source);
        Assert.Contains("face = d.x > 0.0 ? 0 : 1;", source);
        Assert.Contains("lat = vec2(d.x > 0.0 ? d.z : -d.z, d.y);", source);
        Assert.Contains("face = d.z > 0.0 ? 2 : 3;", source);
        Assert.Contains("lat = vec2(d.z > 0.0 ? -d.x : d.x, d.y);", source);
        Assert.Contains("float zn = (range.y + range.x) / (range.y - range.x) - (2.0 * range.y * range.x) / ((range.y - range.x) * mRef);", source);
        Assert.Contains("float refDepth = clamp(0.5 * zn + 0.5, 0.0, 1.0);", source);
        Assert.Contains("vec2 uv = tile.xy + tile.zw * (lat / major * (0.5 * range.z) + 0.5);", source);
        Assert.Contains("vec4 tile = uFireShadowTile[slot * 4 + face];", source);
    }

    // The arithmetic UV and depth must be the ones the perspective matrices of the caster pass give, on every face.
    [Theory]
    [InlineData(FireShadowList.FacePlusX)]
    [InlineData(FireShadowList.FaceMinusX)]
    [InlineData(FireShadowList.FacePlusZ)]
    [InlineData(FireShadowList.FaceMinusZ)]
    public void The_Arithmetic_Tile_Uv_And_Depth_Equal_The_Matrix_Pipeline_On_Every_Face(int face)
    {
        float near = FireShadowMirror.Near;
        float far = FireShadowMirror.FarFor(Radius);
        float scale = FireShadowList.InnerScale(ShadowSettings.TileSide, ShadowSettings.TileGutter);
        Matrix4 view = Matrix4.LookAt(Vector3.Zero, FireShadowList.FaceForward(face), Vector3.UnitY);
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(ShadowSettings.FaceFovRadians, 1f, near, far);
        Random random = new(1234 + face);
        int checkedPoints = 0;
        for (int i = 0; i < 400; i++)
        {
            Vector3 forward = FireShadowList.FaceForward(face);
            Vector3 right = Vector3.Cross(forward, Vector3.UnitY);
            float depth = 0.5f + (float)random.NextDouble() * 10f;
            Vector3 d = forward * depth + right * ((float)random.NextDouble() * 2f - 1f) * depth + Vector3.UnitY * ((float)random.NextDouble() * 2f - 1f) * depth;

            Vector4 clip = new Vector4(d, 1f) * view * projection;
            Vector2 expectedUv = new Vector2(clip.X / clip.W, clip.Y / clip.W) * 0.5f + new Vector2(0.5f);
            float expectedDepth = clip.Z / clip.W * 0.5f + 0.5f;

            FireShadowMirror.FaceProjection projected = FireShadowMirror.Project(d, near, far, scale);

            Assert.Equal(face, projected.Face);
            Assert.InRange(projected.TileUv.X, expectedUv.X - 2e-4f, expectedUv.X + 2e-4f);
            Assert.InRange(projected.TileUv.Y, expectedUv.Y - 2e-4f, expectedUv.Y + 2e-4f);
            Assert.InRange(projected.Depth, expectedDepth - 2e-4f, expectedDepth + 2e-4f);
            checkedPoints++;
        }

        Assert.Equal(400, checkedPoints);
    }

    [Fact]
    public void The_Tile_Of_Each_Slot_And_Face_Is_Where_The_Caster_Pass_Renders_It()
    {
        // The pass viewports into ShadowMapTargets.TileOrigin(slot, face, side); the block carries the same rectangle as UV.
        FireShadowSlot a = Slot(Fire, Radius, 0);
        FireShadowSlot b = Slot(Fire, Radius, 1);
        FireShadowSlot c = Slot(Fire, Radius, 2);
        FireShadowSlot d = Slot(Fire, Radius, 3);
        ShadowFrame frame = FrameOf(a, b, c, d);
        FireShadowList list = FireShadowList.FromFrame(in frame);

        for (int slot = 0; slot < 4; slot++)
        {
            for (int face = 0; face < 4; face++)
            {
                (int x, int y) = ShadowMapTargets.TileOrigin(slot, face, ShadowSettings.TileSide);
                Vector4 tile = list.Tile[slot * 4 + face];
                Assert.Equal(x / (float)ShadowSettings.AtlasResolution, tile.X);
                Assert.Equal(y / (float)ShadowSettings.AtlasResolution, tile.Y);
                Assert.Equal(ShadowSettings.TileSide / (float)ShadowSettings.AtlasResolution, tile.Z);
                Assert.Equal(ShadowSettings.TileSide / (float)ShadowSettings.AtlasResolution, tile.W);
            }
        }

        // Spelled out for the corner cases, so a change to the layout shows up here as a plain number.
        Assert.Equal(new Vector4(0.75f, 0.75f, 0.25f, 0.25f), list.Tile[15]);
        Assert.Equal(new Vector4(0.25f, 0.5f, 0.25f, 0.25f), list.Tile[9]);
        Assert.Equal(new Vector4(0f, 0f, 0.25f, 0.25f), list.Tile[0]);
    }

    // ---- helpers ----

    private static string Normalise(string text) => text.Replace("\r\n", "\n");

    private static float GlslFloat(string source, string name)
    {
        Match m = Regex.Match(source, @"const float " + name + @" = ([0-9.]+);");
        Assert.True(m.Success, name);
        return float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    internal static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    internal static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

// The shader's lookup, line for line, over a CPU atlas. Shared by the sampling, flicker and identity tests.
internal static class FireShadowMirror
{
    public const float Near = 0.10f;

    // The design: far = Radius * 1.15, because the windowed falloff is exactly 0 at the radius, so clipping past it is invisible.
    public static float FarFor(float radius) => radius * 1.15f;

    public readonly record struct Box(Vector3 Min, Vector3 Max);

    // An infinite plane, n . p = D (both sides solid to a ray, like the open shells the caster pass draws without culling).
    public readonly record struct Plane(Vector3 Normal, float D);

    public readonly record struct FaceProjection(int Face, Vector2 TileUv, float Depth);

    public readonly record struct Probe(Vector2 UnclampedUv, Vector2 ClampedUv);

    // ---- the atlas ----

    public sealed class Atlas
    {
        public Atlas(int resolution)
        {
            Resolution = resolution;
            Depth = new float[resolution * resolution];
            Array.Fill(Depth, 1f);
        }

        public int Resolution { get; }
        public float[] Depth { get; }

        // GL_CLAMP_TO_BORDER with border (1,1,1,1).
        public float Texel(int x, int y) =>
            x < 0 || y < 0 || x >= Resolution || y >= Resolution ? 1f : Depth[y * Resolution + x];

        // Hardware 2x2 PCF: compare each of the four texels (LEQUAL: ref <= stored is lit), then bilinear-weight.
        public float Compare(Vector2 uv, float reference)
        {
            float fx = uv.X * Resolution - 0.5f;
            float fy = uv.Y * Resolution - 0.5f;
            int x0 = (int)MathF.Floor(fx);
            int y0 = (int)MathF.Floor(fy);
            float wx = fx - x0;
            float wy = fy - y0;
            float a = reference <= Texel(x0, y0) ? 1f : 0f;
            float b = reference <= Texel(x0 + 1, y0) ? 1f : 0f;
            float c = reference <= Texel(x0, y0 + 1) ? 1f : 0f;
            float d = reference <= Texel(x0 + 1, y0 + 1) ? 1f : 0f;
            return MathHelper.Lerp(MathHelper.Lerp(a, b, wx), MathHelper.Lerp(c, d, wx), wy);
        }

        public void FillTile(int slot, int face, float value)
        {
            (int x0, int y0) = ShadowMapTargets.TileOrigin(slot, face, ShadowSettings.TileSide);
            for (int y = y0; y < y0 + ShadowSettings.TileSide; y++)
            {
                for (int x = x0; x < x0 + ShadowSettings.TileSide; x++)
                {
                    Depth[y * Resolution + x] = value;
                }
            }
        }

        // Renders one face tile exactly as the caster pass would: the fire's flickered position as the origin, the slot's
        // near/far, the widened FOV, 24 bit depth, nearest hit wins, the clear value 1.0 elsewhere.
        public void RenderTile(in FireShadowSlot fire, int slot, int face, ReadOnlySpan<Box> boxes, bool ground = true, ReadOnlySpan<Plane> planes = default)
        {
            Matrix4 view = Matrix4.LookAt(fire.Position, fire.Position + FireShadowList.FaceForward(face), Vector3.UnitY);
            Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(ShadowSettings.FaceFovRadians, 1f, fire.Near, fire.Far);
            Matrix4 viewProjection = view * projection;
            Matrix4.Invert(in viewProjection, out Matrix4 inverse);
            (int x0, int y0) = ShadowMapTargets.TileOrigin(slot, face, ShadowSettings.TileSide);
            int side = ShadowSettings.TileSide;
            for (int py = 0; py < side; py++)
            {
                for (int px = 0; px < side; px++)
                {
                    float ndcX = (px + 0.5f) / side * 2f - 1f;
                    float ndcY = (py + 0.5f) / side * 2f - 1f;
                    Vector4 nearPoint = new Vector4(ndcX, ndcY, -1f, 1f) * inverse;
                    Vector4 farPoint = new Vector4(ndcX, ndcY, 1f, 1f) * inverse;
                    Vector3 direction = Vector3.Normalize(farPoint.Xyz / farPoint.W - nearPoint.Xyz / nearPoint.W);
                    float t = Nearest(fire.Position, direction, boxes, ground, planes);
                    if (float.IsPositiveInfinity(t))
                    {
                        continue;
                    }

                    Vector4 clip = new Vector4(fire.Position + direction * t, 1f) * viewProjection;
                    float depth = clip.Z / clip.W * 0.5f + 0.5f;
                    if (depth < 0f || depth > 1f)
                    {
                        continue; // clipped by near or far: the clear value stays
                    }

                    // DEPTH_COMPONENT24
                    Depth[(y0 + py) * Resolution + x0 + px] = MathF.Round(depth * 16777215f) / 16777215f;
                }
            }
        }
    }

    public static float Nearest(Vector3 origin, Vector3 direction, ReadOnlySpan<Box> boxes, bool ground, ReadOnlySpan<Plane> planes = default)
    {
        float best = float.PositiveInfinity;
        if (ground && direction.Y < 0f)
        {
            float tGround = -origin.Y / direction.Y;
            if (tGround > 0f)
            {
                best = tGround;
            }
        }

        foreach (Box box in boxes)
        {
            best = MathF.Min(best, RayBox(origin, direction, box.Min, box.Max));
        }

        foreach (Plane plane in planes)
        {
            float denominator = Vector3.Dot(plane.Normal, direction);
            if (MathF.Abs(denominator) > 1e-9f)
            {
                float tPlane = (plane.D - Vector3.Dot(plane.Normal, origin)) / denominator;
                if (tPlane > 0f)
                {
                    best = MathF.Min(best, tPlane);
                }
            }
        }

        return best;
    }

    public static float RayBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max)
    {
        float t0 = float.NegativeInfinity;
        float t1 = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            float d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            float lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            float hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < lo || o > hi)
                {
                    return float.PositiveInfinity;
                }

                continue;
            }

            float ta = (lo - o) / d;
            float tb = (hi - o) / d;
            t0 = MathF.Max(t0, MathF.Min(ta, tb));
            t1 = MathF.Min(t1, MathF.Max(ta, tb));
        }

        return t0 <= t1 && t1 > 0f ? MathF.Max(t0, 0f) : float.PositiveInfinity;
    }

    // ---- the lookup ----

    // The face, the tile-local UV (before the tile rectangle) and the stored depth of the offset position d (light to fragment).
    public static FaceProjection Project(Vector3 d, float near, float far, float scale)
    {
        Vector3 a = new(MathF.Abs(d.X), MathF.Abs(d.Y), MathF.Abs(d.Z));
        int face;
        float major;
        Vector2 lat;
        if (a.X > a.Z)
        {
            face = d.X > 0f ? 0 : 1;
            major = a.X;
            lat = new Vector2(d.X > 0f ? d.Z : -d.Z, d.Y);
        }
        else
        {
            face = d.Z > 0f ? 2 : 3;
            major = a.Z;
            lat = new Vector2(d.Z > 0f ? -d.X : d.X, d.Y);
        }

        return new FaceProjection(face, new Vector2(lat.X / major * (0.5f * scale) + 0.5f, lat.Y / major * (0.5f * scale) + 0.5f), DepthOf(major, near, far));
    }

    // The perspective depth the pass writes for a point `major` in front of the light along the face axis.
    public static float DepthOf(float major, float near, float far)
    {
        float zn = (far + near) / (far - near) - (2f * far * near) / ((far - near) * major);
        return Math.Clamp(0.5f * zn + 0.5f, 0f, 1f);
    }

    private const float GutterTexels = 4f;
    private const float GrazingFloor = 0.15f;
    private const float TanMax = 4f;
    private const float NormalOffsetTexels = 1.5f;
    private const float ConstantBias = 0.006f;
    private static readonly float TapOffset = FireShadowList.TapOffsetTexels;

    private static Vector2 Clamp(Vector2 v, Vector2 lo, Vector2 hi) => new(Math.Clamp(v.X, lo.X, hi.X), Math.Clamp(v.Y, lo.Y, hi.Y));

    // Line for line dreamFireVisibility.
    public static float Visibility(in FireShadowList list, Atlas atlas, int light, Vector3 fragPos, Vector3 n) =>
        Lookup(in list, atlas, light, fragPos, n, out _);

    public static Probe ProbeLookup(in FireShadowList list, int light, Vector3 fragPos, Vector3 n)
    {
        Lookup(in list, null, light, fragPos, n, out Probe probe);
        return probe;
    }

    private static float Lookup(in FireShadowList list, Atlas? atlas, int light, Vector3 fragPos, Vector3 n, out Probe probe)
    {
        probe = default;
        if (list.Count <= 0)
        {
            return 1f;
        }

        int slot = -1;
        int count = Math.Min(list.Count, 4);
        for (int s = 0; s < count; s++)
        {
            if ((int)(list.Light[s].W + 0.5f) == light)
            {
                slot = s;
                break;
            }
        }

        if (slot < 0)
        {
            return 1f;
        }

        Vector4 range = list.Range[slot];
        if (range.W <= 0f)
        {
            return 1f;
        }

        Vector3 d0 = fragPos - list.Light[slot].Xyz;
        Vector3 a0 = new(MathF.Abs(d0.X), MathF.Abs(d0.Y), MathF.Abs(d0.Z));
        if (a0.Y >= MathF.Max(a0.X, a0.Z))
        {
            return 1f;
        }

        float major0 = MathF.Max(a0.X, a0.Z);
        float dist = d0.Length;

        float atlasResolution = atlas?.Resolution ?? ShadowSettings.AtlasResolution;
        Vector2 texel = new(1f / atlasResolution);
        float tilePixels = list.Tile[0].Z / texel.X;
        float texelWorld = 2f * major0 / (tilePixels * range.Z);

        float ndl = MathF.Abs(Vector3.Dot(n, d0)) / dist;
        float tanL = MathF.Min(MathF.Sqrt(MathF.Max(1f - ndl * ndl, 0f)) / MathF.Max(ndl, GrazingFloor), TanMax);
        Vector3 d = d0 + n * (NormalOffsetTexels * texelWorld * tanL);
        Vector3 a = new(MathF.Abs(d.X), MathF.Abs(d.Y), MathF.Abs(d.Z));

        int face;
        float major;
        Vector2 lat;
        float axisN;
        if (a.X > a.Z)
        {
            face = d.X > 0f ? 0 : 1;
            major = a.X;
            lat = new Vector2(d.X > 0f ? d.Z : -d.Z, d.Y);
            axisN = MathF.Abs(n.X);
        }
        else
        {
            face = d.Z > 0f ? 2 : 3;
            major = a.Z;
            lat = new Vector2(d.Z > 0f ? -d.X : d.X, d.Y);
            axisN = MathF.Abs(n.Z);
        }

        float slope = MathF.Min(MathF.Sqrt(MathF.Max(1f - axisN * axisN, 0f)) / MathF.Max(ndl * dist / major0, GrazingFloor), TanMax);
        float bias = ConstantBias + FireShadowList.SlopeBiasTexels * texelWorld * slope;

        float mRef = MathF.Max(major - bias, range.X);
        float refDepth = DepthOf(mRef, range.X, range.Y);

        Vector4 tile = list.Tile[slot * 4 + face];
        Vector2 uv = new(
            tile.X + tile.Z * (lat.X / major * (0.5f * range.Z) + 0.5f),
            tile.Y + tile.W * (lat.Y / major * (0.5f * range.Z) + 0.5f));
        Vector2 lo = new(tile.X + GutterTexels * texel.X, tile.Y + GutterTexels * texel.Y);
        Vector2 hi = new(tile.X + tile.Z - GutterTexels * texel.X, tile.Y + tile.W - GutterTexels * texel.Y);
        Vector2 o = TapOffset * texel;
        probe = new Probe(uv, Clamp(uv, lo, hi));
        if (atlas == null)
        {
            return 1f;
        }

        float sum =
            atlas.Compare(Clamp(uv + new Vector2(-o.X, -o.Y), lo, hi), refDepth)
            + atlas.Compare(Clamp(uv + new Vector2(0f, -o.Y), lo, hi), refDepth)
            + atlas.Compare(Clamp(uv + new Vector2(o.X, -o.Y), lo, hi), refDepth)
            + atlas.Compare(Clamp(uv + new Vector2(-o.X, 0f), lo, hi), refDepth)
            + atlas.Compare(Clamp(uv + new Vector2(0f, 0f), lo, hi), refDepth)
            + atlas.Compare(Clamp(uv + new Vector2(o.X, 0f), lo, hi), refDepth)
            + atlas.Compare(Clamp(uv + new Vector2(-o.X, o.Y), lo, hi), refDepth)
            + atlas.Compare(Clamp(uv + new Vector2(0f, o.Y), lo, hi), refDepth)
            + atlas.Compare(Clamp(uv + new Vector2(o.X, o.Y), lo, hi), refDepth);
        return MathHelper.Lerp(1f, sum / ShadowSettings.FireTaps, Math.Clamp(range.W, 0f, 1f));
    }
}
