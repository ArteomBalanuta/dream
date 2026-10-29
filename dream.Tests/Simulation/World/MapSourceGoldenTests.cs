using System.Reflection;
using BulletSharp;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Xunit;

namespace Dream.Tests.Simulation.World;

// Wave D (terrain as data) goldens, recorded on the code BEFORE the Map source seam existed. They
// pin what a startup map is: the order of its geometry cells and their content, the world checksum
// and the order and shape of its physics bodies. The Map source seam (IMapSource) must reproduce
// them byte for byte; if one of these moves, the startup insertion order or a body order changed.
// The cell order depends on the generator's Dictionary insertion order and on HashSet<WorldMicroCellKey>
// enumeration order, so a runtime or collection change shows up here first.
public class MapSourceGoldenTests
{
    private sealed record Golden(
        int Width,
        int Depth,
        bool Enrich,
        int GeneratedCellCount,
        ulong GeneratedCellOrderHash,
        int MapCellCount,
        ulong MapCellOrderHash,
        ulong WorldChecksum,
        int MapCubeBodyCount,
        int CollisionObjectCount,
        ulong PhysicsBodyOrderHash,
        int MergedStaticBodyCount,
        int CellSupportBodyCount);

    private static readonly Golden[] Goldens =
    [
        // Measured on integrate/engine-iteration 9f699b9, before the Map source seam.
        new(56, 56, true, 3764, 0x469B9FB5406B739AUL, 3764, 0x469B9FB5406B739AUL, 0xC2A079599FEFD2B3UL, 1036, 1036, 0xCD19A7599080C77DUL, 407, 407),
        new(100, 100, true, 10628, 0xDA0340CBE3A6B888UL, 10628, 0xDA0340CBE3A6B888UL, 0x10CC0B1E7DF45A0AUL, 1036, 1036, 0x5E8D9717FA2708ADUL, 407, 407),
        new(48, 48, true, 2932, 0x66BC5F1D5345ACBCUL, 2932, 0x66BC5F1D5345ACBCUL, 0x277336C57A17F1AEUL, 1036, 1036, 0x2702AD42395118E5UL, 407, 407),
        new(20, 20, true, 589, 0x791D93EABB72DF27UL, 589, 0x791D93EABB72DF27UL, 0xFBC27C80CF449852UL, 190, 190, 0xFF8F164F2F9CA299UL, 0, 0),
        new(19, 19, true, 361, 0xF4BA32E4755410EFUL, 361, 0xF4BA32E4755410EFUL, 0xA706DD2F4D197E6FUL, 1, 1, 0x15D8D87FB6497B09UL, 0, 0),
        new(25, 25, false, 625, 0xA25862A9126DD1AFUL, 625, 0xA25862A9126DD1AFUL, 0xA706DD2F4D197E6FUL, 1, 1, 0x08EB1BAA2CF19049UL, 0, 0),
    ];

    public static IEnumerable<object[]> Cases()
    {
        yield return [56, 56, true];
        yield return [100, 100, true];
        yield return [48, 48, true];
        yield return [20, 20, true];
        yield return [19, 19, true];
        yield return [25, 25, false];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void StartupMapMatchesGolden(int width, int depth, bool enrich)
    {
        Golden actual = Measure(width, depth, enrich);
        Golden? expected = Goldens.FirstOrDefault(g => g.Width == width && g.Depth == depth && g.Enrich == enrich);
        Assert.True(expected != null, "No golden for this case. Measured: " + Format(actual));
        Assert.True(expected == actual, "Expected: " + Format(expected) + "\nActual:   " + Format(actual));
    }

    private static string Format(Golden g)
    {
        return $"new({g.Width}, {g.Depth}, {(g.Enrich ? "true" : "false")}, {g.GeneratedCellCount}, 0x{g.GeneratedCellOrderHash:X16}UL, " +
            $"{g.MapCellCount}, 0x{g.MapCellOrderHash:X16}UL, 0x{g.WorldChecksum:X16}UL, {g.MapCubeBodyCount}, " +
            $"{g.CollisionObjectCount}, 0x{g.PhysicsBodyOrderHash:X16}UL, {g.MergedStaticBodyCount}, {g.CellSupportBodyCount})";
    }

    private static Golden Measure(int width, int depth, bool enrich)
    {
        const int height = 8;
        IReadOnlyList<GeometryCellRecord> generated =
            new WorldGenerator().CreateStartupGeometry(width, height, depth, enrich);
        ulong generatedHash = HashCells(generated);

        using PhysicsIntegrator physics = new();
        Map map = new(physics, generateStartupTerrain: enrich, width: width, height: height, depth: depth);

        ulong mapHash = HashCells(map.GeometryCells.Values);
        Assert.Empty(map.FindInvariantViolations());

        ulong bodyHash = Fnv.Start();
        bodyHash = HashBodies(bodyHash, physics.MapCubeBodies);
        bodyHash = Fnv.Add(bodyHash, 0xB011E7UL);
        List<RigidBody> collisionObjects = [];
        foreach (CollisionObject collisionObject in physics.World.CollisionObjectArray)
        {
            collisionObjects.Add((RigidBody)collisionObject);
        }

        bodyHash = HashBodies(bodyHash, collisionObjects);

        FieldInfo mergedField = typeof(Map).GetField(
            "_mergedStaticSubcellCollisionBodies",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        int mergedCount = ((IReadOnlyCollection<GeometryCollisionBodyDescriptor>)mergedField.GetValue(map)!).Count;

        return new Golden(
            width,
            depth,
            enrich,
            generated.Count,
            generatedHash,
            map.GeometryCells.Count,
            mapHash,
            map.WorldChecksum,
            physics.MapCubeBodies.Count,
            collisionObjects.Count,
            bodyHash,
            mergedCount,
            map.CellSupportBodyCount);
    }

    private static ulong HashCells(IEnumerable<GeometryCellRecord> cells)
    {
        ulong h = Fnv.Start();
        foreach (GeometryCellRecord cell in cells)
        {
            h = Fnv.Add(h, cell.Key.X);
            h = Fnv.Add(h, cell.Key.Y);
            h = Fnv.Add(h, cell.Key.Z);
            h = Fnv.Add(h, (int)cell.Definition.ShapeKind);
            h = Fnv.Add(h, (int)cell.Definition.MaterialId);
            h = Fnv.Add(h, cell.Definition.SubdivisionResolution);
            h = Fnv.Add(h, cell.Definition.IsSolid ? 1 : 0);
            h = Fnv.Add(h, cell.Definition.IsFloor ? 1 : 0);
            GeometrySubdivisionState? state = cell.SubdivisionState;
            h = Fnv.Add(h, state != null ? 1 : 0);
            if (state == null)
            {
                continue;
            }

            h = Fnv.Add(h, state.AllChildrenEmpty ? 1 : 0);
            int res = state.Resolution;
            h = Fnv.Add(h, res);
            // In-range overrides, ascending by linear index (x * res + y) * res + z.
            for (int x = 0; x < res; x++)
            {
                for (int y = 0; y < res; y++)
                {
                    for (int z = 0; z < res; z++)
                    {
                        if (state.TryGetChildSolid(new GeometrySubcellKey(x, y, z), out bool solid))
                        {
                            h = Fnv.Add(h, (x * res + y) * res + z);
                            h = Fnv.Add(h, solid ? 1 : 0);
                        }
                    }
                }
            }
        }

        return h;
    }

    private static ulong HashBodies(ulong h, IEnumerable<RigidBody> bodies)
    {
        foreach (RigidBody body in bodies)
        {
            BulletSharp.Math.Vector3 origin = body.WorldTransform.Origin;
            h = Fnv.Add(h, BitConverter.SingleToInt32Bits(origin.X));
            h = Fnv.Add(h, BitConverter.SingleToInt32Bits(origin.Y));
            h = Fnv.Add(h, BitConverter.SingleToInt32Bits(origin.Z));
            h = Fnv.Add(h, (int)body.CollisionShape.ShapeType);
            body.GetAabb(out BulletSharp.Math.Vector3 min, out BulletSharp.Math.Vector3 max);
            h = Fnv.Add(h, BitConverter.SingleToInt32Bits(max.X - min.X));
            h = Fnv.Add(h, BitConverter.SingleToInt32Bits(max.Y - min.Y));
            h = Fnv.Add(h, BitConverter.SingleToInt32Bits(max.Z - min.Z));
            h = Fnv.Add(h, BitConverter.SingleToInt32Bits(body.InvMass));
            h = Fnv.Add(h, (int)body.CollisionFlags);
        }

        return h;
    }

    private static class Fnv
    {
        public static ulong Start() => 14695981039346656037UL;

        public static ulong Add(ulong h, ulong value) => AddBits(h, value);

        public static ulong Add(ulong h, long value) => AddBits(h, (ulong)value);

        private static ulong AddBits(ulong h, ulong v)
        {
            unchecked
            {
                for (int i = 0; i < 8; i++)
                {
                    h ^= (v >> (i * 8)) & 0xFF;
                    h *= 1099511628211UL;
                }

                return h;
            }
        }
    }
}
