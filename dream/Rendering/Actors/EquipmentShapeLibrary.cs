using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.Actors;

// Builds the sculpted unit meshes the equipment parts are drawn with, one per EquipmentShape.
//
// Every mesh is authored in the same unit frame the old cube used: centred on the origin, inside
// [-0.5, 0.5] on all three axes, touching both -0.5 and +0.5 on at least one axis, length along +Y.
// The part's own matrix (Size, Pitch, Roll, Yaw, Center) then places it, so swapping a part's shape
// changes its form and not its place or its mass.
//
// The meshes are pure data: no GL, no file, no clock, no randomness that is not derived from the
// shape itself. EquipmentMeshCache turns them into GL buffers once, lazily, on the render thread.
//
// Counts are deliberately small. The whole figure is 28 x 101 pixels at the shipped isometric zoom,
// so what reads is the silhouette and the RUN OF LIGHT across a surface, not vertex density. A
// 10-sided shaft and a 12-segment plate already give a smooth value gradient; 40 would cost tris and
// show nothing.
public static class EquipmentShapeLibrary
{
    // The size of a per-shape array, which EquipmentMeshCache indexes by (int)shape. That is only
    // the same thing as "highest value + 1" while the enum is contiguous from zero, so
    // EquipmentShapeLibraryTests.TheShapeEnumIsContiguousFromZero pins exactly that: a shape given
    // an explicit out-of-range value would otherwise be handed another shape's mesh in silence.
    public static readonly int ShapeCount = Enum.GetValues<EquipmentShape>().Length;

    // One unit mesh for the shape. Pure; callers cache.
    public static StaticWorldChunkMesh Build(EquipmentShape shape)
    {
        MeshWriter writer = new();
        switch (shape)
        {
            case EquipmentShape.BevelBox:
                AppendBevelBox(writer);
                break;
            case EquipmentShape.Shaft:
                AppendShaft(writer);
                break;
            case EquipmentShape.Taper:
                AppendTaper(writer);
                break;
            case EquipmentShape.Blade:
                AppendBlade(writer);
                break;
            case EquipmentShape.Plate:
                AppendPlate(writer);
                break;
            case EquipmentShape.Dome:
                AppendDome(writer);
                break;
            case EquipmentShape.Spike:
                AppendSpike(writer);
                break;
            case EquipmentShape.Ring:
                AppendRing(writer);
                break;
            case EquipmentShape.Crescent:
                AppendCrescent(writer);
                break;
            case EquipmentShape.Gem:
                AppendGem(writer);
                break;
            case EquipmentShape.Panel:
                AppendPanel(writer);
                break;
            case EquipmentShape.Horn:
                AppendHorn(writer);
                break;
            case EquipmentShape.Flare:
                AppendFlare(writer);
                break;
            case EquipmentShape.Pauldron:
                AppendPauldron(writer);
                break;
            case EquipmentShape.Cuirass:
                AppendCuirass(writer);
                break;
            case EquipmentShape.Greave:
                AppendGreave(writer);
                break;
            case EquipmentShape.Lame:
                AppendLame(writer);
                break;
            case EquipmentShape.Membrane:
                AppendMembrane(writer);
                break;
            default:
                AppendBox(writer);
                break;
        }

        // Fit the finished mesh to the unit box on the axes the shape is MEANT to fill, so a part's
        // authored Size is its real extent and a shape is a true drop-in for a Box. Three things
        // make this necessary, and none of them is a mistake in the shape itself:
        //   - an arc of less than 360 degrees has an asymmetric range on its open axis (Plate),
        //   - a polygon inscribed in a circle only touches the circle at its vertices, so a 10-gon
        //     shaft reaches 0.951 of its radius on an axis that falls between two vertices,
        //   - a surface with a bulge or a wave sits off-centre on its thin axis (Panel).
        // Left unfitted these are small, silent errors in where a part is drawn, and the armour
        // cover gates cannot see them because they measure the part's box, not its mesh.
        //
        // Two shapes keep an axis unfitted, because on that axis the shape IS the measurement:
        //   - Ring's Y is a torus's MINOR diameter; stretching it to fill would make a barrel.
        //   - Panel's Z is a sheet's thickness plus its bulge; stretching it to fill would scale the
        //     wall up with the curve and turn a cloak into a slab.
        // Both stay centred on their fitted axes; Panel's small residual Z offset (it bulges toward
        // -Z) is a fraction of a sheet's thickness in engine units. Everything else fills all three.
        // EquipmentShape.cs states this, and EquipmentShapeLibraryTests pins every real extent.
        writer.FitToUnitBox(
            fitX: true,
            fitY: shape is not EquipmentShape.Ring,
            fitZ: shape is not (EquipmentShape.Panel or EquipmentShape.Membrane));

        return writer.ToMesh();
    }

    // A curved, tapering horn: a cone swept along a circular arc that starts pointing along +Y and
    // leans toward +Z as it rises, ending in a point. Spike is the straight version of this and
    // reads as a stud; the sweep is the whole difference, because it puts the silhouette's weight
    // off the vertical and that is what the eye calls a horn.
    //
    // The section is a circle perpendicular to the path, so the horn stays round as it bends - a
    // horn built as stacked axis-aligned rings pinches on the inside of the curve. The tip ring is
    // tiny rather than a single point: a zero-radius ring would stitch degenerate triangles.
    private static void AppendHorn(MeshWriter writer)
    {
        const int segments = 8;
        const float sweep = 1.2f;   // radians, about 69 degrees of lean by the tip
        const float pathRadius = 0.75f;
        ReadOnlySpan<float> stops = [0f, 0.22f, 0.44f, 0.66f, 0.85f, 1f];

        Vector3[][] rings = new Vector3[stops.Length][];
        for (int i = 0; i < stops.Length; i++)
        {
            float t = stops[i];
            float angle = sweep * t;
            float sin = MathF.Sin(angle);
            float cos = MathF.Cos(angle);
            // The arc: tangent is +Y at the base and rotates toward +Z.
            Vector3 centre = new(0f, pathRadius * sin, pathRadius * (1f - cos));
            Vector3 across = Vector3.UnitX;
            Vector3 up = new(0f, -sin, cos);
            // A touch of entasis on the taper, so the horn swells out of its base instead of
            // starting to shrink immediately.
            float radius = i == stops.Length - 1
                ? 0.012f
                : 0.30f * MathF.Pow(1f - t, 0.82f) * (1f + (0.10f * MathF.Sin(MathF.PI * t)));

            Vector3[] ring = new Vector3[segments];
            for (int k = 0; k < segments; k++)
            {
                float theta = MathF.Tau * k / segments;
                ring[k] = centre + (radius * ((MathF.Cos(theta) * across) + (MathF.Sin(theta) * up)));
            }

            rings[i] = ring;
        }

        AppendTube(writer, rings, null, Repeat(segments, false), TubeCap.Fan, TubeCap.Fan);
    }

    // A bell: narrow at the base, widening along a CONCAVE profile to a flared lip, hollow, with a
    // wall and a rolled rim. A cuff, a boot top, a fauld skirt, a helm brim.
    //
    // Built as a lathe of a CLOSED profile rather than a stack of rings, because the thing that has
    // to read is the rim: the profile runs up the outside, across the lip and back down the inside,
    // so the lip is a real edge with a thickness instead of a cut-off cone. The profile never
    // touches the axis, so the solid is a ring in topology and stays watertight.
    private static void AppendFlare(MeshWriter writer)
    {
        // (radius, height), counter-clockwise, so the 2D edge normal (dy, -dx) points outward.
        ReadOnlySpan<Vector2> profile =
        [
            new Vector2(0.18f, -0.50f),   // outer wall, from the narrow base
            new Vector2(0.26f, -0.18f),
            new Vector2(0.38f, 0.14f),
            new Vector2(0.50f, 0.42f),    // the flare
            new Vector2(0.50f, 0.50f),    // outer lip
            new Vector2(0.43f, 0.50f),    // across the rim
            new Vector2(0.41f, 0.36f),    // inner wall, back down
            new Vector2(0.31f, 0.06f),
            new Vector2(0.21f, -0.24f),
            new Vector2(0.13f, -0.50f),   // inner base; the bottom annulus closes to point 0
        ];

        // Creases: the lip and both rim corners, and the bottom annulus. The walls stay smooth, so
        // the bell has a run of light up it and a hard bright line along the rim.
        bool[] hard = [true, false, false, true, true, true, false, false, false, true];

        AppendLathe(writer, profile, hard, segments: 10);
    }

    // The legacy unit cube: six flat-shaded quads, outward normals, each face UV 0..1.
    private static void AppendBox(MeshWriter writer)
    {
        // (normal, the four corners counter-clockwise seen from outside)
        Span<int> axes = [0, 1, 2];
        foreach (int axis in axes)
        {
            for (int sign = -1; sign <= 1; sign += 2)
            {
                Vector3 normal = Vector3.Zero;
                normal[axis] = sign;
                Vector3 u = Vector3.Zero;
                Vector3 v = Vector3.Zero;
                u[(axis + 1) % 3] = sign;
                v[(axis + 2) % 3] = 1f;
                Vector3 centre = normal * 0.5f;
                writer.Quad(
                    centre - (u * 0.5f) - (v * 0.5f),
                    centre + (u * 0.5f) - (v * 0.5f),
                    centre + (u * 0.5f) + (v * 0.5f),
                    centre - (u * 0.5f) + (v * 0.5f),
                    normal);
            }
        }
    }

    // A cube whose twelve edges and eight corners are chamfered by 0.10 of the unit. The silhouette
    // is the cube's, but the chamfer catches a different light value than the face it borders, so at
    // 28 pixels a straight piece of plate reads as a bevelled edge instead of a voxel. The value STEP
    // is the whole point, so face and bevel both get flat normals.
    private static void AppendBevelBox(MeshWriter writer)
    {
        const float chamfer = 0.10f;
        const float inner = 0.5f - chamfer;

        // The six shrunk faces.
        for (int axis = 0; axis < 3; axis++)
        {
            for (int sign = -1; sign <= 1; sign += 2)
            {
                Vector3 normal = Axis(axis, sign);
                Vector3 u = Axis((axis + 1) % 3, inner);
                Vector3 v = Axis((axis + 2) % 3, inner);
                Vector3 centre = normal * 0.5f;
                FlatQuad(writer, centre - u - v, centre + u - v, centre + u + v, centre - u + v, normal);
            }
        }

        // The twelve edge bevels: one quad per edge, spanning the third axis between the two corners.
        for (int a = 0; a < 3; a++)
        {
            for (int b = a + 1; b < 3; b++)
            {
                int c = 3 - a - b;
                for (int sa = -1; sa <= 1; sa += 2)
                {
                    for (int sb = -1; sb <= 1; sb += 2)
                    {
                        Vector3 outward = Vector3.Normalize(Axis(a, sa) + Axis(b, sb));
                        Vector3 alongA = Axis(a, sa * 0.5f) + Axis(b, sb * inner);
                        Vector3 alongB = Axis(a, sa * inner) + Axis(b, sb * 0.5f);
                        Vector3 low = Axis(c, -inner);
                        Vector3 high = Axis(c, inner);
                        FlatQuad(writer, alongA + low, alongA + high, alongB + high, alongB + low, outward);
                    }
                }
            }
        }

        // The eight corner triangles.
        for (int sx = -1; sx <= 1; sx += 2)
        {
            for (int sy = -1; sy <= 1; sy += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 outward = Vector3.Normalize(new Vector3(sx, sy, sz));
                    FlatTriangle(
                        writer,
                        new Vector3(sx * 0.5f, sy * inner, sz * inner),
                        new Vector3(sx * inner, sy * 0.5f, sz * inner),
                        new Vector3(sx * inner, sy * inner, sz * 0.5f),
                        outward);
                }
            }
        }
    }

    // A ten-sided column along Y, barrelled about 6% at mid height and chamfered into flat caps at
    // both ends: hafts, poles, grips. Smooth around the ring so the light runs round the column, and
    // a hard ring at each chamfer so the end reads as a cut rather than a melt.
    private static void AppendShaft(MeshWriter writer)
    {
        const int segments = 10;
        Vector3[][] rings =
        [
            LatheRing(segments, -0.5f, 0.42f),
            LatheRing(segments, -0.45f, ShaftRadius(-0.45f)),
            LatheRing(segments, 0f, ShaftRadius(0f)),
            LatheRing(segments, 0.45f, ShaftRadius(0.45f)),
            LatheRing(segments, 0.5f, 0.42f),
        ];

        AppendTube(writer, rings, [false, true, false, true, false], Repeat(segments, false), TubeCap.Fan, TubeCap.Fan);
    }

    // 0.5 at mid height falling to about 0.47 at the ends: a 6% barrel that never leaves the unit.
    private static float ShaftRadius(float y) => 0.5f - (0.03f * (2f * y) * (2f * y));

    // An eight-sided truncated cone, wide at -Y and narrow at +Y, with a touch of concavity in the
    // profile so a ferrule or a collar flares instead of reading as a plain cone. Smooth around,
    // flat caps.
    private static void AppendTaper(MeshWriter writer)
    {
        const int segments = 8;
        static float Radius(float t) => 0.5f + ((0.18f - 0.5f) * MathF.Pow(t, 1.15f));
        Vector3[][] rings =
        [
            LatheRing(segments, -0.5f, Radius(0f)),
            LatheRing(segments, 0f, Radius(0.5f)),
            LatheRing(segments, 0.5f, Radius(1f)),
        ];

        AppendTube(writer, rings, null, Repeat(segments, false), TubeCap.Fan, TubeCap.Fan);
    }

    // A sword blade along +Y with a lenticular cross-section: a raised ridge at z = 0 on each flat
    // face (the part frame puts the thin side on X and the width on Z), a cutting edge at z = +-w,
    // a full-width ricasso below y = -0.4, a distal taper and a clipped point above y = +0.35.
    // Smooth along the length so the light runs the blade; split at the ridge and at both edges, so
    // the four long facets hold four separate values - that is what reads as a blade at 28 pixels.
    private static void AppendBlade(MeshWriter writer)
    {
        ReadOnlySpan<float> heights = [-0.5f, -0.4f, -0.1f, 0.15f, 0.35f, 0.45f, 0.5f];
        Vector3[][] rings = new Vector3[heights.Length][];
        for (int i = 0; i < heights.Length; i++)
        {
            float y = heights[i];
            float width = BladeHalfWidth(y);
            float thickness = BladeHalfThickness(y);
            float centre = BladeCentreZ(y);
            rings[i] =
            [
                new Vector3(thickness, y, centre),
                new Vector3(0f, y, centre + width),
                new Vector3(-thickness, y, centre),
                new Vector3(0f, y, centre - width),
            ];
        }

        AppendTube(writer, rings, null, Repeat(4, true), TubeCap.Fan, TubeCap.Fan);
    }

    // Half the blade's width in Z: full to the ricasso, distal taper to 0.275, clipped to a point.
    private static float BladeHalfWidth(float y)
    {
        if (y <= -0.4f)
        {
            return 0.5f;
        }

        return y <= 0.35f
            ? Lerp(0.5f, 0.275f, Smooth((y + 0.4f) / 0.75f))
            : Lerp(0.275f, 0.06f, (y - 0.35f) / 0.15f);
    }

    // Half the blade's thickness in X at the ridge: thickest at the ricasso, thinnest at the point.
    private static float BladeHalfThickness(float y)
    {
        if (y <= -0.4f)
        {
            return 0.5f;
        }

        return y <= 0.35f
            ? Lerp(0.5f, 0.25f, Smooth((y + 0.4f) / 0.75f))
            : Lerp(0.25f, 0.05f, (y - 0.35f) / 0.15f);
    }

    // The clipped point: above y = 0.35 the section slides back off the leading edge.
    private static float BladeCentreZ(float y) => y <= 0.35f ? 0f : Lerp(0f, -0.12f, (y - 0.35f) / 0.15f);

    // A curved shell: a 230-degree cylindrical arc about Y, wall 0.14 thick, open at the back (-Z).
    // The workhorse of plate armour, because it wraps a limb instead of boxing it. Smooth around the
    // arc on the outer and the inner surface, hard at the four lip corners, and the top and bottom
    // rims are closed with a strip so the shell is a solid, not a sheet.
    //
    // Two things here are deliberate and were measured in the engine, not guessed:
    //
    // 1. The sweep is 230 degrees, not the 150 it started at. A 150-degree arc is a shield boss, not
    //    a piece of armour: it covers the front of a limb and leaves the back bare, which is exactly
    //    what the first in-engine pass showed on the cuirass and the boots.
    // 2. The finished ring is FITTED to the unit box in X and Z. An arc of any sweep under 360 has
    //    an asymmetric Z range, so without this the shell's centroid sits forward of the origin and
    //    a part's Center stops meaning the part's centre - the drawn piece slides toward +Z and the
    //    coverage gates, which measure the part's BOX, keep passing while the armour misses the body.
    //    Fitting also means a Plate fills its authored Size like a Box did, so it is a true drop-in.
    //    The fit is affine per axis, so the section becomes an ellipse rather than a circle; that is
    //    an improvement for a torso and harmless everywhere else.
    private static void AppendPlate(MeshWriter writer)
    {
        const int segments = 14;
        const float sweep = 230f * (MathF.PI / 180f);
        const float outerRadius = 0.5f;
        const float innerRadius = outerRadius - 0.14f;
        int points = (segments + 1) * 2;

        ReadOnlySpan<float> heights = [-0.5f, 0f, 0.5f];
        ReadOnlySpan<float> scales = [0.96f, 1f, 0.96f];
        Vector3[][] rings = new Vector3[heights.Length][];
        for (int i = 0; i < heights.Length; i++)
        {
            Vector3[] ring = new Vector3[points];
            for (int s = 0; s <= segments; s++)
            {
                // The arc is centred on +Z and opens toward -Z.
                float angle = (-sweep * 0.5f) + (sweep * s / segments);
                float sin = MathF.Sin(angle);
                float cos = MathF.Cos(angle);
                float outer = outerRadius * scales[i];
                float inner = innerRadius * scales[i];
                ring[s] = new Vector3(outer * sin, heights[i], outer * cos);
                ring[points - 1 - s] = new Vector3(inner * sin, heights[i], inner * cos);
            }

            rings[i] = ring;
        }

        FitRingsToUnitBox(rings, fitX: true, fitZ: true);

        bool[] hardPoint = Repeat(points, false);
        hardPoint[0] = true;
        hardPoint[segments] = true;
        hardPoint[segments + 1] = true;
        hardPoint[points - 1] = true;

        AppendTube(writer, rings, null, hardPoint, TubeCap.Strip, TubeCap.Strip);
    }

    // Scales and shifts every ring point so the rings' bounding box fills [-0.5, 0.5] on the chosen
    // axes. Applied BEFORE AppendTube, so the normals are derived from the fitted geometry and no
    // normal has to be corrected for the non-uniform scale. An axis whose extent is already zero or
    // degenerate is left alone.
    private static void FitRingsToUnitBox(Vector3[][] rings, bool fitX, bool fitZ)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (Vector3[] ring in rings)
        {
            foreach (Vector3 point in ring)
            {
                min = Vector3.ComponentMin(min, point);
                max = Vector3.ComponentMax(max, point);
            }
        }

        float scaleX = fitX && max.X - min.X > 1e-5f ? 1f / (max.X - min.X) : 1f;
        float scaleZ = fitZ && max.Z - min.Z > 1e-5f ? 1f / (max.Z - min.Z) : 1f;
        float midX = (max.X + min.X) * 0.5f;
        float midZ = (max.Z + min.Z) * 0.5f;
        for (int r = 0; r < rings.Length; r++)
        {
            for (int i = 0; i < rings[r].Length; i++)
            {
                Vector3 point = rings[r][i];
                rings[r][i] = new Vector3(
                    fitX ? (point.X - midX) * scaleX : point.X,
                    point.Y,
                    fitZ ? (point.Z - midZ) * scaleZ : point.Z);
            }
        }
    }

    // A hemi-ogive on a short cylindrical skirt: helm skulls, bosses, pommels. The cap is ogival
    // rather than spherical, so the top is slightly pointed and the highlight sits high. Smooth
    // everywhere except the skirt/cap junction, which stays a crisp line; flat closed bottom.
    private static void AppendDome(MeshWriter writer)
    {
        const int segments = 12;
        const float skirtTop = -0.15f;
        static float CapRadius(float t) => 0.5f * MathF.Pow(MathF.Cos(t * MathF.PI * 0.5f), 1.3f);

        Vector3[][] rings =
        [
            LatheRing(segments, -0.5f, 0.5f),
            LatheRing(segments, skirtTop, 0.5f),
            LatheRing(segments, Lerp(skirtTop, 0.5f, 0.25f), CapRadius(0.25f)),
            LatheRing(segments, Lerp(skirtTop, 0.5f, 0.5f), CapRadius(0.5f)),
            LatheRing(segments, Lerp(skirtTop, 0.5f, 0.75f), CapRadius(0.75f)),
            LatheRing(segments, 0.5f, 0.035f),
        ];

        AppendTube(
            writer,
            rings,
            [false, true, false, false, false, false],
            Repeat(segments, false),
            TubeCap.Fan,
            TubeCap.Fan);
    }

    // A four-sided pyramid with a slight entasis: the side edges bow out about 5% at mid height, so a
    // stud or a crown spike looks swollen at the root instead of ruler-straight. Flat normals per
    // face, as asked: at this size the facets ARE the read. Closed base.
    private static void AppendSpike(MeshWriter writer)
    {
        ReadOnlySpan<float> heights = [-0.5f, 0f, 0.5f];
        ReadOnlySpan<float> extents = [0.5f, 0.3f, 0.02f];
        Vector3[][] rings = new Vector3[heights.Length][];
        for (int i = 0; i < heights.Length; i++)
        {
            float e = extents[i];
            float y = heights[i];
            rings[i] =
            [
                new Vector3(e, y, e),
                new Vector3(-e, y, e),
                new Vector3(-e, y, -e),
                new Vector3(e, y, -e),
            ];
        }

        AppendTube(writer, rings, [true, true, true], Repeat(4, true), TubeCap.Fan, TubeCap.Fan);
    }

    // A torus lying in XZ with its hole on Y: crown bands, pommel rings, shield rims. Fully smooth,
    // with analytic normals, because a ring is nothing but its highlight running round the roll.
    private static void AppendRing(MeshWriter writer)
    {
        const int major = 14;
        const int minor = 8;
        const float majorRadius = 0.35f;
        const float minorRadius = 0.15f;

        for (int i = 0; i < major; i++)
        {
            float t0 = MathF.Tau * i / major;
            float t1 = MathF.Tau * (i + 1) / major;
            for (int j = 0; j < minor; j++)
            {
                float p0 = MathF.Tau * j / minor;
                float p1 = MathF.Tau * (j + 1) / minor;
                SmoothQuad(
                    writer,
                    TorusVertex(majorRadius, minorRadius, t0, p0, (float)i / major, (float)j / minor),
                    TorusVertex(majorRadius, minorRadius, t1, p0, (float)(i + 1) / major, (float)j / minor),
                    TorusVertex(majorRadius, minorRadius, t1, p1, (float)(i + 1) / major, (float)(j + 1) / minor),
                    TorusVertex(majorRadius, minorRadius, t0, p1, (float)i / major, (float)(j + 1) / minor));
            }
        }
    }

    private static (Vector3 Position, Vector3 Normal, Vector2 Uv) TorusVertex(
        float majorRadius,
        float minorRadius,
        float around,
        float roll,
        float u,
        float v)
    {
        float cosAround = MathF.Cos(around);
        float sinAround = MathF.Sin(around);
        float cosRoll = MathF.Cos(roll);
        float sinRoll = MathF.Sin(roll);
        Vector3 normal = new(cosRoll * cosAround, sinRoll, cosRoll * sinAround);
        float ring = majorRadius + (minorRadius * cosRoll);
        Vector3 position = new(ring * cosAround, minorRadius * sinRoll, ring * sinAround);
        return (position, normal, Uv(u, v));
    }

    // A crescent swept in the YZ plane and thin in X: axe bits, glaive edges, gothic guards. The
    // convex edge at +Z is ground to a bevel over the first third of the section, the spine behind it
    // keeps the full thickness, and the body tapers to a horn at y = +-0.5. Smooth along the sweep,
    // split around the section so the ground bevel holds its own value against the flat of the blade.
    private static void AppendCrescent(MeshWriter writer)
    {
        ReadOnlySpan<float> sweep = [-1f, -0.85f, -0.6f, -0.3f, 0f, 0.3f, 0.6f, 0.85f, 1f];
        ReadOnlySpan<float> depths = [0f, 0.35f, 1f];
        Vector3[][] rings = new Vector3[sweep.Length][];
        for (int i = 0; i < sweep.Length; i++)
        {
            float s = sweep[i];
            // The cutting edge rides a circle of radius 0.95 centred behind the blade at z = -0.45.
            float y = 0.5f * s;
            float edgeZ = -0.45f + MathF.Sqrt(0.9025f - (y * y));
            Vector2 radial = Vector2.Normalize(new Vector2(y, edgeZ + 0.45f));
            float thickness = (0.95f * MathF.Pow(1f - (s * s), 0.6f)) + 0.03f;

            Vector3[] ring = new Vector3[depths.Length * 2];
            for (int d = 0; d < depths.Length; d++)
            {
                float u = depths[d];
                float half = CrescentHalfThickness(u);
                Vector2 plane = new Vector2(y, edgeZ) - (radial * (thickness * u));
                ring[d] = new Vector3(half, plane.X, plane.Y);
                ring[ring.Length - 1 - d] = new Vector3(-half, plane.X, plane.Y);
            }

            rings[i] = ring;
        }

        AppendTube(writer, rings, null, Repeat(depths.Length * 2, true), TubeCap.Fan, TubeCap.Fan);
    }

    // Half the crescent's thickness in X: a ground bevel over the first 35% of the section, then the
    // full flat of the blade back to the spine.
    private static float CrescentHalfThickness(float depth) => 0.075f + (0.425f * MathF.Min(1f, depth / 0.35f));

    // A faceted bipyramid: an octagonal girdle at y = 0, a flat table at +Y joined by crown facets,
    // and a point at -Y. ALL normals are flat on purpose - a jewel is a handful of separate values,
    // not a gradient, and at 28 pixels those values are the only thing that says "gem".
    private static void AppendGem(MeshWriter writer)
    {
        const int segments = 8;
        const float tableRadius = 0.22f;
        Vector3[] girdle = LatheRing(segments, 0f, 0.5f);
        Vector3[] table = LatheRing(segments, 0.5f, tableRadius);
        Vector3 point = new(0f, -0.5f, 0f);

        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            Vector3 crownCentre = (girdle[i] + girdle[next] + table[next] + table[i]) * 0.25f;
            FlatQuad(writer, girdle[i], girdle[next], table[next], table[i], crownCentre);
            Vector3 pavilionCentre = (girdle[i] + girdle[next] + point) / 3f;
            FlatTriangle(writer, girdle[next], girdle[i], point, pavilionCentre);
        }

        for (int i = 1; i + 1 < segments; i++)
        {
            FlatTriangle(writer, table[0], table[i], table[i + 1], Vector3.UnitY);
        }
    }

    // A thin slab in the XY plane for cloth: cloaks, tabards, banners. The surface is a gentle
    // cylindrical bulge toward -Z plus two low sine waves across X, so a cloak catches a RUN of light
    // across its width instead of one flat value. Front and back are smooth; the rim is closed and
    // split, so the cloth has an edge.
    private static void AppendPanel(MeshWriter writer)
    {
        const int columns = 6;
        const int rows = 4;
        const float halfThickness = 0.07f;

        for (int i = 0; i < columns; i++)
        {
            float x0 = -0.5f + ((float)i / columns);
            float x1 = -0.5f + ((float)(i + 1) / columns);
            for (int j = 0; j < rows; j++)
            {
                float y0 = -0.5f + ((float)j / rows);
                float y1 = -0.5f + ((float)(j + 1) / rows);
                float u0 = (float)i / columns;
                float u1 = (float)(i + 1) / columns;
                float v0 = (float)j / rows;
                float v1 = (float)(j + 1) / rows;

                SmoothQuad(
                    writer,
                    PanelVertex(x0, y0, front: true, u0, v0),
                    PanelVertex(x1, y0, front: true, u1, v0),
                    PanelVertex(x1, y1, front: true, u1, v1),
                    PanelVertex(x0, y1, front: true, u0, v1));
                SmoothQuad(
                    writer,
                    PanelVertex(x0, y0, front: false, u0, v0),
                    PanelVertex(x1, y0, front: false, u1, v0),
                    PanelVertex(x1, y1, front: false, u1, v1),
                    PanelVertex(x0, y1, front: false, u0, v1));
            }
        }

        // Top and bottom rims.
        for (int i = 0; i < columns; i++)
        {
            float x0 = -0.5f + ((float)i / columns);
            float x1 = -0.5f + ((float)(i + 1) / columns);
            for (int edge = 0; edge < 2; edge++)
            {
                float y = edge == 0 ? -0.5f : 0.5f;
                Vector3 outward = new(0f, y * 2f, 0f);
                FlatQuad(
                    writer,
                    new Vector3(x0, y, PanelSurface(x0) - halfThickness),
                    new Vector3(x1, y, PanelSurface(x1) - halfThickness),
                    new Vector3(x1, y, PanelSurface(x1) + halfThickness),
                    new Vector3(x0, y, PanelSurface(x0) + halfThickness),
                    outward);
            }
        }

        // Left and right rims.
        for (int j = 0; j < rows; j++)
        {
            float y0 = -0.5f + ((float)j / rows);
            float y1 = -0.5f + ((float)(j + 1) / rows);
            for (int edge = 0; edge < 2; edge++)
            {
                float x = edge == 0 ? -0.5f : 0.5f;
                float z = PanelSurface(x);
                Vector3 outward = new(x * 2f, 0f, 0f);
                FlatQuad(
                    writer,
                    new Vector3(x, y0, z - halfThickness),
                    new Vector3(x, y1, z - halfThickness),
                    new Vector3(x, y1, z + halfThickness),
                    new Vector3(x, y0, z + halfThickness),
                    outward);
            }
        }
    }

    // The cloth's mid-surface: a cylindrical bulge toward -Z plus two sine waves across the width.
    private static float PanelSurface(float x) =>
        (-0.12f * (1f - (4f * x * x))) + (0.05f * MathF.Sin(MathF.Tau * 2f * (x + 0.5f)));

    private static float PanelSlope(float x) =>
        (0.96f * x) + (0.05f * MathF.Tau * 2f * MathF.Cos(MathF.Tau * 2f * (x + 0.5f)));

    private static (Vector3 Position, Vector3 Normal, Vector2 Uv) PanelVertex(
        float x,
        float y,
        bool front,
        float u,
        float v)
    {
        const float halfThickness = 0.07f;
        float slope = PanelSlope(x);
        float side = front ? -1f : 1f;
        Vector3 position = new(x, y, PanelSurface(x) + (side * halfThickness));
        Vector3 normal = Vector3.Normalize(new Vector3(-slope * side, 0f, side));
        return (position, normal, Uv(u, v));
    }

    // ---- pass 4: profiled shells ----------------------------------------
    //
    // Plate is the workhorse of the catalogue and it is a CYLINDER: one radius, one sweep, three
    // rings at 0.96 / 1.00 / 0.96. Wrapped round a limb that reads fine, but used as a shoulder cap
    // or a chest it gives the slab that the reference sets never have - the eye reads a constant
    // section as an extruded shape however curved its ends are.
    //
    // The four shapes below are all the same construction with a different ring table: an open arc
    // about +Z whose RADIUS, SWEEP and CENTRE vary per ring. That is the one technique the low-poly
    // armour generator skill is built on (its loft(centers, radii, angles)), expressed in the unit
    // frame this catalogue uses. A varying radius is what makes a pauldron swell and tuck; a varying
    // sweep is what makes it close round the top of the arm and open at the hem; a moving centre is
    // what makes a greave lean forward over the shin instead of standing as a pipe.
    //
    // All of them are fitted to the unit box afterwards, exactly as Plate is, so a part's authored
    // Size is still its real extent and any of them is a drop-in for a Plate.

    // A shoulder cap: wide and nearly closed at the top where it caps the deltoid, swelling to its
    // widest at a third of the way down, then tucking IN and opening out at the hem. The profile is
    // the difference between a pauldron and a bucket - a shell whose bottom rim is narrower than its
    // belly throws a shadow under itself and reads as a cap sitting ON a shoulder.
    //
    // Unrotated the opening faces -Z like Plate's, so the same rule applies: to cap a shoulder,
    // pitch it -90 so the opening turns downward. See EquipmentShape.cs.
    private static void AppendPauldron(MeshWriter writer)
    {
        ReadOnlySpan<float> heights = [-0.5f, -0.22f, 0.06f, 0.32f, 0.5f];
        ReadOnlySpan<float> radiusX = [0.44f, 0.50f, 0.47f, 0.37f, 0.26f];
        ReadOnlySpan<float> radiusZ = [0.42f, 0.50f, 0.48f, 0.40f, 0.30f];
        ReadOnlySpan<float> sweeps = [250f, 238f, 224f, 206f, 190f];
        ReadOnlySpan<float> offsetZ = [0.02f, 0f, -0.01f, -0.02f, -0.03f];
        AppendArcShell(writer, 11, heights, radiusX, radiusZ, sweeps, offsetZ, 0.13f);
    }

    // A torso: an HOURGLASS, which is the second of the six things a worn piece is judged on. Wide
    // across the chest, pinched at the waist, flared again over the hips, and the section is an
    // ELLIPSE - a body is wider across than it is deep, so rz stays well under rx and the chest does
    // not read as a barrel. The sweep widens toward the hem so the fauld stands off the thighs.
    private static void AppendCuirass(MeshWriter writer)
    {
        ReadOnlySpan<float> heights = [-0.5f, -0.32f, -0.06f, 0.18f, 0.40f, 0.5f];
        ReadOnlySpan<float> radiusX = [0.44f, 0.50f, 0.32f, 0.44f, 0.50f, 0.46f];
        ReadOnlySpan<float> radiusZ = [0.34f, 0.39f, 0.24f, 0.33f, 0.38f, 0.34f];
        ReadOnlySpan<float> sweeps = [258f, 252f, 238f, 234f, 230f, 226f];
        ReadOnlySpan<float> offsetZ = [0.01f, 0f, 0f, 0.01f, 0.02f, 0.02f];

        // The waist ring is HARD. A pinch of 0.50 -> 0.32 is a real hourglass in the geometry, but a
        // smooth one, and a smooth change of section across a part twenty pixels wide is a gradient
        // the resolve flattens. Splitting the normal at the waist turns the pinch into a LINE - the
        // belt line every cuirass in the references has - and a line survives where a gradient does
        // not. The two rings either side of it stay smooth, so the chest and the fauld still curve.
        bool[] hardRing = [false, false, true, false, false, false];
        AppendArcShell(writer, 11, heights, radiusX, radiusZ, sweeps, offsetZ, 0.12f, hardRing);
    }

    // A limb shell that TAPERS: a greave, a bracer, a vambrace, a boot shaft. Wide at the +Y end
    // (the knee or the elbow), narrow at the -Y end, and the centre walks toward +Z as it falls, so
    // the piece leans over the front of the limb the way a real greave does. A tube reads as a pipe;
    // the taper and the lean are the whole fix.
    private static void AppendGreave(MeshWriter writer)
    {
        ReadOnlySpan<float> heights = [-0.5f, -0.2f, 0.12f, 0.38f, 0.5f];
        ReadOnlySpan<float> radiusX = [0.29f, 0.35f, 0.44f, 0.50f, 0.48f];
        ReadOnlySpan<float> radiusZ = [0.30f, 0.36f, 0.45f, 0.50f, 0.47f];
        ReadOnlySpan<float> sweeps = [238f, 234f, 230f, 226f, 222f];
        ReadOnlySpan<float> offsetZ = [0.06f, 0.04f, 0.01f, -0.01f, -0.02f];
        AppendArcShell(writer, 11, heights, radiusX, radiusZ, sweeps, offsetZ, 0.12f);
    }

    // Layered lames with visible steps - the sixth of the six things a worn piece is judged on, and
    // the one no single shape gave before. Three short arc bands stacked up +Y, each one standing a
    // little further out than the one below, with a hard rim between them. At 20 pixels what reads
    // is three value steps in a row, which is what a fauld, a tasset, a couter or a gorget looks
    // like from the game camera.
    private static void AppendLame(MeshWriter writer)
    {
        // A SAWTOOTH radius, not a ramp. Each tier flares out as it rises and the next one starts
        // again further in, so the rim of every plate stands proud of the plate above it and the
        // step casts a hard line. The first attempt ramped 0.40 -> 0.50 smoothly with hard rings at
        // the joins, and it rendered as a plain tapering shell: a hard normal split across a 0.01
        // radius change is a crease nobody can see. The jump has to be in the GEOMETRY.
        ReadOnlySpan<float> heights = [-0.5f, -0.34f, -0.33f, -0.10f, -0.09f, 0.14f, 0.15f, 0.5f];
        ReadOnlySpan<float> radiusX = [0.34f, 0.46f, 0.36f, 0.48f, 0.38f, 0.50f, 0.40f, 0.50f];
        ReadOnlySpan<float> radiusZ = [0.34f, 0.46f, 0.36f, 0.48f, 0.38f, 0.50f, 0.40f, 0.50f];
        ReadOnlySpan<float> sweeps = [232f, 232f, 230f, 230f, 228f, 228f, 226f, 226f];
        ReadOnlySpan<float> offsetZ = [0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f];
        bool[] hardRing = [false, true, true, true, true, true, true, false];
        AppendArcShell(writer, 10, heights, radiusX, radiusZ, sweeps, offsetZ, 0.11f, hardRing);
    }

    // The shared construction: an open arc about +Z, swept sweepDegrees and centred on +Z, with an
    // outer wall at (radiusX, radiusZ) and an inner wall one wall thinner, closed top and bottom
    // with a strip so the shell is a solid and not a sheet. Rings run -Y to +Y.
    //
    // The lip corners are hard so the shell has an edge; everything else is smooth, which is what
    // makes the varying radius read as a curve rather than as facets.
    private static void AppendArcShell(
        MeshWriter writer,
        int segments,
        ReadOnlySpan<float> heights,
        ReadOnlySpan<float> radiusX,
        ReadOnlySpan<float> radiusZ,
        ReadOnlySpan<float> sweepDegrees,
        ReadOnlySpan<float> offsetZ,
        float wall,
        bool[]? hardRing = null)
    {
        int points = (segments + 1) * 2;
        Vector3[][] rings = new Vector3[heights.Length][];
        for (int i = 0; i < heights.Length; i++)
        {
            float sweep = sweepDegrees[i] * (MathF.PI / 180f);
            float outerX = radiusX[i];
            float outerZ = radiusZ[i];

            // Never let the inner wall cross the axis: a shell thinner than its own wall would turn
            // inside out, and the winding gate would catch it only on the ring it happened on.
            float innerX = MathF.Max(outerX - wall, outerX * 0.25f);
            float innerZ = MathF.Max(outerZ - wall, outerZ * 0.25f);

            Vector3[] ring = new Vector3[points];
            for (int s = 0; s <= segments; s++)
            {
                float angle = (-sweep * 0.5f) + (sweep * s / segments);
                float sin = MathF.Sin(angle);
                float cos = MathF.Cos(angle);
                ring[s] = new Vector3(outerX * sin, heights[i], offsetZ[i] + (outerZ * cos));
                ring[points - 1 - s] = new Vector3(innerX * sin, heights[i], offsetZ[i] + (innerZ * cos));
            }

            rings[i] = ring;
        }

        FitRingsToUnitBox(rings, fitX: true, fitZ: true);

        bool[] hardPoint = Repeat(points, false);
        hardPoint[0] = true;
        hardPoint[segments] = true;
        hardPoint[segments + 1] = true;
        hardPoint[points - 1] = true;

        AppendTube(writer, rings, hardRing, hardPoint, TubeCap.Strip, TubeCap.Strip);
    }

    // A ribbed membrane: a wing, a tattered banner, a fin. Panel is a cloth sheet with a gentle
    // bulge, and at this zoom a bulge of a tenth of a unit over a part twenty pixels wide is one
    // value - which is why the gossamer wings read FLAT. This is the fix, and it is a frequency fix
    // rather than an amplitude one: four RIBS run the length of the sheet, each a narrow ridge
    // standing proud of the membrane between them, with the normal split at every ridge line. Four
    // ribs across twenty pixels is a rib every five pixels, which survives MSAA; a dozen would not.
    //
    // Like Panel this is a sheet, so Z is thickness plus camber and is left unfitted.
    private static void AppendMembrane(MeshWriter writer)
    {
        const int ribs = 4;
        const int columns = ribs * 3;
        const int rows = 5;
        const float halfThickness = 0.045f;

        for (int i = 0; i < columns; i++)
        {
            float x0 = -0.5f + ((float)i / columns);
            float x1 = -0.5f + ((float)(i + 1) / columns);
            float u0 = (float)i / columns;
            float u1 = (float)(i + 1) / columns;

            // A ridge line every third column: split the normal there so the rib has an edge.
            bool ridgeLeft = i % 3 == 0;
            bool ridgeRight = (i + 1) % 3 == 0;

            for (int j = 0; j < rows; j++)
            {
                float y0 = -0.5f + ((float)j / rows);
                float y1 = -0.5f + ((float)(j + 1) / rows);
                float v0 = (float)j / rows;
                float v1 = (float)(j + 1) / rows;

                for (int side = 0; side < 2; side++)
                {
                    bool front = side == 0;
                    SmoothQuad(
                        writer,
                        MembraneVertex(x0, y0, front, u0, v0, ridgeLeft),
                        MembraneVertex(x1, y0, front, u1, v0, ridgeRight),
                        MembraneVertex(x1, y1, front, u1, v1, ridgeRight),
                        MembraneVertex(x0, y1, front, u0, v1, ridgeLeft));
                }
            }
        }

        // The rim, closed all the way round so the membrane is a solid sheet with an edge.
        for (int i = 0; i < columns; i++)
        {
            float x0 = -0.5f + ((float)i / columns);
            float x1 = -0.5f + ((float)(i + 1) / columns);
            for (int edge = 0; edge < 2; edge++)
            {
                float y = edge == 0 ? -0.5f : 0.5f;
                Vector3 outward = new(0f, y * 2f, 0f);
                FlatQuad(
                    writer,
                    new Vector3(x0, y, MembraneSurface(x0) - halfThickness),
                    new Vector3(x1, y, MembraneSurface(x1) - halfThickness),
                    new Vector3(x1, y, MembraneSurface(x1) + halfThickness),
                    new Vector3(x0, y, MembraneSurface(x0) + halfThickness),
                    outward);
            }
        }

        for (int j = 0; j < rows; j++)
        {
            float y0 = -0.5f + ((float)j / rows);
            float y1 = -0.5f + ((float)(j + 1) / rows);
            for (int edge = 0; edge < 2; edge++)
            {
                float x = edge == 0 ? -0.5f : 0.5f;
                float z = MembraneSurface(x);
                Vector3 outward = new(x * 2f, 0f, 0f);
                FlatQuad(
                    writer,
                    new Vector3(x, y0, z - halfThickness),
                    new Vector3(x, y1, z - halfThickness),
                    new Vector3(x, y1, z + halfThickness),
                    new Vector3(x, y0, z + halfThickness),
                    outward);
            }
        }
    }

    // The membrane's mid-surface: a strong camber toward -Z plus a rib ripple. The ripple is a
    // cosine of four periods across the width, so the ridges land on the columns the normals are
    // split at.
    private static float MembraneSurface(float x)
    {
        float camber = -0.16f * (1f - (4f * x * x));
        float ripple = 0.055f * MathF.Cos(MathF.Tau * 4f * (x + 0.5f));
        return camber + ripple;
    }

    private static float MembraneSlope(float x) =>
        (1.28f * x) - (0.055f * MathF.Tau * 4f * MathF.Sin(MathF.Tau * 4f * (x + 0.5f)));

    // ridge flattens the slope used for the normal at a ridge line, so the two facets that meet
    // there get normals that differ instead of averaging into one smooth hump.
    private static (Vector3 Position, Vector3 Normal, Vector2 Uv) MembraneVertex(
        float x,
        float y,
        bool front,
        float u,
        float v,
        bool ridge)
    {
        const float halfThickness = 0.045f;
        float slope = ridge ? MembraneSlope(x) * 0.25f : MembraneSlope(x);
        float side = front ? -1f : 1f;
        Vector3 position = new(x, y, MembraneSurface(x) + (side * halfThickness));
        Vector3 normal = Vector3.Normalize(new Vector3(-slope * side, 0f, side));
        return (position, normal, Uv(u, v));
    }

    // ---- shared geometry helpers ----------------------------------------

    // How an end of a tube is closed.
    private enum TubeCap
    {
        // Left open.
        None,

        // Triangle fan from the loop's first point: convex loops only.
        Fan,

        // Quads pairing point i with point (count - 1 - i): a shell's rim, where the loop is an
        // outward run followed by the matching inward run.
        Strip,
    }

    // Stitches a stack of closed section loops into a tube, with the normals the shapes asked for.
    //
    // rings run from the -Y end to the +Y end and all have the same point count; a face is the quad
    // between ring i and ring i+1 at column k. A vertex normal averages the faces that meet at it,
    // which is what makes a curve read as a curve - EXCEPT across a ring flagged in hardRing or a
    // point flagged in hardPoint, where the normal is split so the edge stays a crease. That split is
    // the difference between a sculpted shape and a faceted one.
    private static void AppendTube(
        MeshWriter writer,
        IReadOnlyList<Vector3[]> rings,
        bool[]? hardRing,
        bool[] hardPoint,
        TubeCap startCap,
        TubeCap endCap)
    {
        int ringCount = rings.Count;
        int points = rings[0].Length;
        int bands = ringCount - 1;

        Vector3[] centres = new Vector3[ringCount];
        for (int i = 0; i < ringCount; i++)
        {
            Vector3 sum = Vector3.Zero;
            for (int j = 0; j < points; j++)
            {
                sum += rings[i][j];
            }

            centres[i] = sum / points;
        }

        Vector3[,] faceNormals = new Vector3[bands, points];
        for (int b = 0; b < bands; b++)
        {
            for (int k = 0; k < points; k++)
            {
                int k1 = (k + 1) % points;
                Vector3 a = rings[b][k];
                Vector3 bb = rings[b][k1];
                Vector3 c = rings[b + 1][k1];
                Vector3 d = rings[b + 1][k];
                Vector3 outward = ((a + bb + c + d) * 0.25f) - ((centres[b] + centres[b + 1]) * 0.5f);
                Vector3 normal = Vector3.Cross(c - a, d - bb);
                if (Vector3.Dot(normal, outward) < 0f)
                {
                    normal = -normal;
                }

                faceNormals[b, k] = normal.LengthSquared > 1e-12f
                    ? Vector3.Normalize(normal)
                    : Vector3.Normalize(outward);
            }
        }

        Vector3 VertexNormal(int band, int column, int ring, int point)
        {
            Vector3 sum = faceNormals[band, column];
            int otherBand = ring == band ? band - 1 : band + 1;
            bool smoothAlong = hardRing is null || !hardRing[ring];
            bool hasOtherBand = smoothAlong && otherBand >= 0 && otherBand < bands;
            if (hasOtherBand)
            {
                sum += faceNormals[otherBand, column];
            }

            if (!hardPoint[point])
            {
                int otherColumn = point == column
                    ? ((column - 1 + points) % points)
                    : ((column + 1) % points);
                sum += faceNormals[band, otherColumn];
                if (hasOtherBand)
                {
                    sum += faceNormals[otherBand, otherColumn];
                }
            }

            return sum;
        }

        float vSpan = MathF.Max(1f, bands);
        for (int b = 0; b < bands; b++)
        {
            for (int k = 0; k < points; k++)
            {
                int k1 = (k + 1) % points;
                float u0 = (float)k / points;
                float u1 = (float)(k + 1) / points;
                float v0 = b / vSpan;
                float v1 = (b + 1) / vSpan;
                SmoothQuad(
                    writer,
                    (rings[b][k], VertexNormal(b, k, b, k), Uv(u0, v0)),
                    (rings[b][k1], VertexNormal(b, k, b, k1), Uv(u1, v0)),
                    (rings[b + 1][k1], VertexNormal(b, k, b + 1, k1), Uv(u1, v1)),
                    (rings[b + 1][k], VertexNormal(b, k, b + 1, k), Uv(u0, v1)));
            }
        }

        AppendCap(writer, rings[0], centres[0] - centres[1], startCap);
        AppendCap(writer, rings[ringCount - 1], centres[ringCount - 1] - centres[ringCount - 2], endCap);
    }

    private static void AppendCap(MeshWriter writer, Vector3[] loop, Vector3 outward, TubeCap cap)
    {
        switch (cap)
        {
            case TubeCap.Fan:
                for (int i = 1; i + 1 < loop.Length; i++)
                {
                    FlatTriangle(writer, loop[0], loop[i], loop[i + 1], outward);
                }

                break;
            case TubeCap.Strip:
                for (int i = 0; i + 1 < loop.Length / 2; i++)
                {
                    FlatQuad(writer, loop[i], loop[i + 1], loop[loop.Length - 2 - i], loop[loop.Length - 1 - i], outward);
                }

                break;
            default:
                break;
        }
    }

    // One ring of a lathe: segments points on a circle of the given radius at the given height.
    private static Vector3[] LatheRing(int segments, float y, float radius)
    {
        Vector3[] ring = new Vector3[segments];
        for (int i = 0; i < segments; i++)
        {
            float angle = MathF.Tau * i / segments;
            ring[i] = new Vector3(radius * MathF.Cos(angle), y, radius * MathF.Sin(angle));
        }

        return ring;
    }

    // Revolves a CLOSED 2D profile of (radius, height) points about the Y axis. The profile must be
    // counter-clockwise in that plane and must not touch the axis, so the result is watertight with
    // no pole. A profile point flagged in `hard` keeps a crease: its two edges each get their own
    // normal instead of the average, which is how a rim stays a rim.
    private static void AppendLathe(MeshWriter writer, ReadOnlySpan<Vector2> source, bool[] hard, int segments)
    {
        // Copied out of the span: the local functions below capture it, and a ref struct cannot be
        // captured.
        Vector2[] profile = source.ToArray();
        int count = profile.Length;

        // The outward 2D normal of each edge i -> i+1.
        Vector2[] edgeNormal = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            Vector2 d = profile[(i + 1) % count] - profile[i];
            Vector2 n = new(d.Y, -d.X);
            edgeNormal[i] = n.LengthSquared > 1e-12f ? Vector2.Normalize(n) : Vector2.UnitX;
        }

        // The 2D normal to use at profile point i when it is the START of edge e, and when it is the
        // END of edge e: equal (the average) at a smooth point, each edge's own at a hard one.
        Vector2 NormalAt(int point, int edge)
        {
            if (hard[point])
            {
                return edgeNormal[edge];
            }

            int previous = (edge + count - 1) % count;
            Vector2 sum = edgeNormal[previous] + edgeNormal[edge];
            return sum.LengthSquared > 1e-12f ? Vector2.Normalize(sum) : edgeNormal[edge];
        }

        (Vector3 Position, Vector3 Normal, Vector2 Uv) Vertex(int point, int edge, int segment)
        {
            float theta = MathF.Tau * segment / segments;
            float cos = MathF.Cos(theta);
            float sin = MathF.Sin(theta);
            Vector2 p = profile[point];
            Vector2 n = NormalAt(point, edge);
            return (
                new Vector3(p.X * cos, p.Y, p.X * sin),
                new Vector3(n.X * cos, n.Y, n.X * sin),
                Uv((float)segment / segments, (float)point / count));
        }

        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            for (int k = 0; k < segments; k++)
            {
                int k1 = (k + 1) % segments;
                // a -> b along the profile, b -> c around the lathe: counter-clockwise from outside.
                writer.Quad(
                    Vertex(i, i, k),
                    Vertex(next, i, k),
                    Vertex(next, i, k1 == 0 ? segments : k1),
                    Vertex(i, i, k1 == 0 ? segments : k1));
            }
        }
    }

    private static bool[] Repeat(int count, bool value)
    {
        bool[] values = new bool[count];
        Array.Fill(values, value);
        return values;
    }

    private static Vector3 Axis(int index, float value)
    {
        Vector3 axis = Vector3.Zero;
        axis[index] = value;
        return axis;
    }

    private static Vector2 Uv(float u, float v) => new(Math.Clamp(u, 0f, 1f), Math.Clamp(v, 0f, 1f));

    private static float Lerp(float from, float to, float t) => from + ((to - from) * Math.Clamp(t, 0f, 1f));

    private static float Smooth(float t)
    {
        float clamped = Math.Clamp(t, 0f, 1f);
        return clamped * clamped * (3f - (2f * clamped));
    }

    // Writes a flat quad, flipping the winding if it does not already face the outward hint. Doing
    // this in one place is why no shape here can ship an inverted face.
    private static void FlatQuad(MeshWriter writer, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward)
    {
        Vector3 normal = Vector3.Cross(c - a, d - b);
        if (Vector3.Dot(normal, outward) < 0f)
        {
            (a, b, c, d) = (d, c, b, a);
            normal = -normal;
        }

        writer.Quad(a, b, c, d, normal.LengthSquared > 1e-12f ? normal : outward);
    }

    private static void FlatTriangle(MeshWriter writer, Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
    {
        Vector3 normal = Vector3.Cross(b - a, c - a);
        if (Vector3.Dot(normal, outward) < 0f)
        {
            (b, c) = (c, b);
            normal = -normal;
        }

        writer.Triangle(a, b, c, normal.LengthSquared > 1e-12f ? normal : outward);
    }

    // Writes a quad whose corners carry their own normals, flipping the winding if it disagrees with
    // them. The normals, not the vertex order, decide which way the face looks.
    private static void SmoothQuad(
        MeshWriter writer,
        (Vector3 Position, Vector3 Normal, Vector2 Uv) a,
        (Vector3 Position, Vector3 Normal, Vector2 Uv) b,
        (Vector3 Position, Vector3 Normal, Vector2 Uv) c,
        (Vector3 Position, Vector3 Normal, Vector2 Uv) d)
    {
        Vector3 geometric = Vector3.Cross(c.Position - a.Position, d.Position - b.Position);
        Vector3 average = a.Normal + b.Normal + c.Normal + d.Normal;
        if (Vector3.Dot(geometric, average) < 0f)
        {
            (b, d) = (d, b);
        }

        // A band quad can be non-convex where a section converges (the crescent's spine does it), and
        // then the a-c split makes one sliver triangle wound backwards. Splitting on b-d instead keeps
        // the same cycle and both halves facing out.
        if (!SplitFacesOutward(a, b, c, d, average) && SplitFacesOutward(b, c, d, a, average))
        {
            writer.Quad(b, c, d, a);
            return;
        }

        writer.Quad(a, b, c, d);
    }

    // Do both triangles of the a-c split agree with the quad's averaged normal?
    private static bool SplitFacesOutward(
        (Vector3 Position, Vector3 Normal, Vector2 Uv) a,
        (Vector3 Position, Vector3 Normal, Vector2 Uv) b,
        (Vector3 Position, Vector3 Normal, Vector2 Uv) c,
        (Vector3 Position, Vector3 Normal, Vector2 Uv) d,
        Vector3 average)
    {
        Vector3 first = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
        Vector3 second = Vector3.Cross(c.Position - a.Position, d.Position - a.Position);
        return Vector3.Dot(first, average) > 0f && Vector3.Dot(second, average) > 0f;
    }

    // Collects vertices and indices and turns them into the engine's static mesh record. Positions
    // are written as authored; normals are normalised on write; UVs default to the quad's corners.
    internal sealed class MeshWriter
    {
        private readonly List<StaticWorldVertex> _vertices = [];
        private readonly List<uint> _indices = [];

        public int VertexCount => _vertices.Count;

        public int TriangleCount => _indices.Count / 3;

        // One flat-shaded quad a-b-c-d, counter-clockwise seen from outside.
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            uint first = (uint)_vertices.Count;
            Vector3 n = normal.LengthSquared > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
            _vertices.Add(new StaticWorldVertex(a, n, new Vector2(0f, 0f)));
            _vertices.Add(new StaticWorldVertex(b, n, new Vector2(1f, 0f)));
            _vertices.Add(new StaticWorldVertex(c, n, new Vector2(1f, 1f)));
            _vertices.Add(new StaticWorldVertex(d, n, new Vector2(0f, 1f)));
            _indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        // One quad whose four corners carry their own (smooth) normals and UVs.
        public void Quad(
            (Vector3 Position, Vector3 Normal, Vector2 Uv) a,
            (Vector3 Position, Vector3 Normal, Vector2 Uv) b,
            (Vector3 Position, Vector3 Normal, Vector2 Uv) c,
            (Vector3 Position, Vector3 Normal, Vector2 Uv) d)
        {
            uint first = (uint)_vertices.Count;
            Add(a);
            Add(b);
            Add(c);
            Add(d);
            _indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        // One flat-shaded triangle a-b-c, counter-clockwise seen from outside.
        public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
        {
            uint first = (uint)_vertices.Count;
            Vector3 n = normal.LengthSquared > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
            _vertices.Add(new StaticWorldVertex(a, n, new Vector2(0f, 0f)));
            _vertices.Add(new StaticWorldVertex(b, n, new Vector2(1f, 0f)));
            _vertices.Add(new StaticWorldVertex(c, n, new Vector2(0.5f, 1f)));
            _indices.AddRange([first, first + 1, first + 2]);
        }

        public void Triangle(
            (Vector3 Position, Vector3 Normal, Vector2 Uv) a,
            (Vector3 Position, Vector3 Normal, Vector2 Uv) b,
            (Vector3 Position, Vector3 Normal, Vector2 Uv) c)
        {
            uint first = (uint)_vertices.Count;
            Add(a);
            Add(b);
            Add(c);
            _indices.AddRange([first, first + 1, first + 2]);
        }

        // Scales and shifts every vertex so the mesh's bounding box fills [-0.5, 0.5] on the chosen
        // axes. The normals are corrected for the per-axis scale the way a normal matrix does it -
        // n / s, renormalised - so a non-uniform fit does not tilt the shading. An axis that is
        // already filled is an exact no-op, and a degenerate axis is left alone.
        public void FitToUnitBox(bool fitX, bool fitY, bool fitZ)
        {
            if (_vertices.Count == 0)
            {
                return;
            }

            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);
            for (int i = 0; i < _vertices.Count; i++)
            {
                min = Vector3.ComponentMin(min, _vertices[i].Position);
                max = Vector3.ComponentMax(max, _vertices[i].Position);
            }

            Vector3 size = max - min;
            Vector3 mid = (max + min) * 0.5f;
            Vector3 scale = new(
                fitX && size.X > 1e-5f ? 1f / size.X : 1f,
                fitY && size.Y > 1e-5f ? 1f / size.Y : 1f,
                fitZ && size.Z > 1e-5f ? 1f / size.Z : 1f);
            Vector3 shift = new(fitX ? mid.X : 0f, fitY ? mid.Y : 0f, fitZ ? mid.Z : 0f);
            if (scale == Vector3.One && shift == Vector3.Zero)
            {
                return;
            }

            for (int i = 0; i < _vertices.Count; i++)
            {
                StaticWorldVertex vertex = _vertices[i];
                Vector3 position = (vertex.Position - shift) * scale;
                Vector3 normal = new(
                    vertex.Normal.X / scale.X,
                    vertex.Normal.Y / scale.Y,
                    vertex.Normal.Z / scale.Z);
                normal = normal.LengthSquared > 1e-12f ? Vector3.Normalize(normal) : vertex.Normal;
                _vertices[i] = new StaticWorldVertex(position, normal, vertex.TexCoord);
            }
        }

        public StaticWorldChunkMesh ToMesh()
        {
            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);
            for (int i = 0; i < _vertices.Count; i++)
            {
                min = Vector3.ComponentMin(min, _vertices[i].Position);
                max = Vector3.ComponentMax(max, _vertices[i].Position);
            }

            if (_vertices.Count == 0)
            {
                min = Vector3.Zero;
                max = Vector3.Zero;
            }

            return new StaticWorldChunkMesh(
                new StaticWorldChunkKey(0, 0, 0),
                GeometryMaterialId.Grass,
                StaticTerrainMaterialRole.Single,
                _vertices,
                _indices,
                new StaticWorldChunkBounds(min, max),
                SourceCellCount: 1,
                ExposedFaceCount: _indices.Count / 6);
        }

        private void Add((Vector3 Position, Vector3 Normal, Vector2 Uv) vertex)
        {
            Vector3 n = vertex.Normal.LengthSquared > 1e-12f ? Vector3.Normalize(vertex.Normal) : Vector3.UnitY;
            _vertices.Add(new StaticWorldVertex(vertex.Position, n, vertex.Uv));
        }
    }
}
