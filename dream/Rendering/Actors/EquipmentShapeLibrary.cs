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
