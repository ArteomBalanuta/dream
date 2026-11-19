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
    // Highest shape value + 1: the size of a per-shape array.
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
            default:
                AppendBox(writer);
                break;
        }

        return writer.ToMesh();
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

    // A curved shell: a 150-degree cylindrical arc about Y, outer radius 0.5, wall 0.14 thick, open
    // at the back (-Z). The workhorse of plate armour, because it wraps a limb instead of boxing it.
    // Smooth around the arc on the outer and the inner surface, hard at the four lip corners, and
    // the top and bottom rims are closed with a strip so the shell is a solid, not a sheet.
    private static void AppendPlate(MeshWriter writer)
    {
        const int segments = 12;
        const float sweep = 150f * (MathF.PI / 180f);
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

        bool[] hardPoint = Repeat(points, false);
        hardPoint[0] = true;
        hardPoint[segments] = true;
        hardPoint[segments + 1] = true;
        hardPoint[points - 1] = true;

        AppendTube(writer, rings, null, hardPoint, TubeCap.Strip, TubeCap.Strip);
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
