using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.World;

public static class StaticWorldChunkMeshBuilder
{
    private const float Epsilon = 0.0001f;

    public static StaticWorldChunkMesh Build(
        StaticWorldChunkKey key,
        GeometryMaterialId materialId,
        IReadOnlyList<StaticVoxelRenderCell> materialCells)
    {
        return Build(key, materialId, StaticTerrainMaterialRole.Single, materialCells, materialCells);
    }

    public static StaticWorldChunkMesh Build(
        StaticWorldChunkKey key,
        GeometryMaterialId materialId,
        IReadOnlyList<StaticVoxelRenderCell> materialCells,
        IReadOnlyList<StaticVoxelRenderCell> occluderCells)
    {
        return Build(key, materialId, StaticTerrainMaterialRole.Single, materialCells, occluderCells);
    }

    public static StaticWorldChunkMesh Build(
        StaticWorldChunkKey key,
        GeometryMaterialId materialId,
        StaticTerrainMaterialRole materialRole,
        IReadOnlyList<StaticVoxelRenderCell> materialCells,
        IReadOnlyList<StaticVoxelRenderCell> occluderCells)
    {
        List<StaticWorldVertex> vertices = new();
        List<uint> indices = new();
        int exposedFaceCount = 0;
        FaceOccluderIndex occluderIndex = FaceOccluderIndex.Create(occluderCells);

        foreach (StaticVoxelRenderCell cell in materialCells)
        {
            foreach (FaceDefinition face in FaceDefinition.All)
            {
                if (materialRole != StaticTerrainMaterialRole.Single &&
                    StaticTerrainMaterialCatalog.ResolveRoleForNormal(materialId, face.Normal) != materialRole)
                {
                    continue;
                }

                if (IsFaceCovered(cell, face.Direction, occluderIndex))
                {
                    continue;
                }

                AppendFace(cell, face, vertices, indices);
                exposedFaceCount++;
            }
        }

        return new StaticWorldChunkMesh(
            key,
            materialId,
            materialRole,
            vertices,
            indices,
            StaticWorldChunkBounds.FromCells(materialCells),
            materialCells.Count,
            exposedFaceCount);
    }

    private static bool IsFaceCovered(
        StaticVoxelRenderCell cell,
        FaceDirection direction,
        FaceOccluderIndex occluderIndex)
    {
        foreach (StaticVoxelRenderCell occluder in occluderIndex.GetCandidates(cell, direction))
        {
            if (CoversFace(cell, occluder, direction))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CoversFace(
        StaticVoxelRenderCell cell,
        StaticVoxelRenderCell occluder,
        FaceDirection direction)
    {
        return direction switch
        {
            FaceDirection.PositiveX =>
                NearlyEqual(occluder.Min.X, cell.Max.X) &&
                Covers(occluder.Min.Y, occluder.Max.Y, cell.Min.Y, cell.Max.Y) &&
                Covers(occluder.Min.Z, occluder.Max.Z, cell.Min.Z, cell.Max.Z),
            FaceDirection.NegativeX =>
                NearlyEqual(occluder.Max.X, cell.Min.X) &&
                Covers(occluder.Min.Y, occluder.Max.Y, cell.Min.Y, cell.Max.Y) &&
                Covers(occluder.Min.Z, occluder.Max.Z, cell.Min.Z, cell.Max.Z),
            FaceDirection.PositiveY =>
                NearlyEqual(occluder.Min.Y, cell.Max.Y) &&
                Covers(occluder.Min.X, occluder.Max.X, cell.Min.X, cell.Max.X) &&
                Covers(occluder.Min.Z, occluder.Max.Z, cell.Min.Z, cell.Max.Z),
            FaceDirection.NegativeY =>
                NearlyEqual(occluder.Max.Y, cell.Min.Y) &&
                Covers(occluder.Min.X, occluder.Max.X, cell.Min.X, cell.Max.X) &&
                Covers(occluder.Min.Z, occluder.Max.Z, cell.Min.Z, cell.Max.Z),
            FaceDirection.PositiveZ =>
                NearlyEqual(occluder.Min.Z, cell.Max.Z) &&
                Covers(occluder.Min.X, occluder.Max.X, cell.Min.X, cell.Max.X) &&
                Covers(occluder.Min.Y, occluder.Max.Y, cell.Min.Y, cell.Max.Y),
            FaceDirection.NegativeZ =>
                NearlyEqual(occluder.Max.Z, cell.Min.Z) &&
                Covers(occluder.Min.X, occluder.Max.X, cell.Min.X, cell.Max.X) &&
                Covers(occluder.Min.Y, occluder.Max.Y, cell.Min.Y, cell.Max.Y),
            _ => false
        };
    }

    private static bool Covers(float candidateMin, float candidateMax, float targetMin, float targetMax)
    {
        return candidateMin <= targetMin + Epsilon &&
            candidateMax >= targetMax - Epsilon;
    }

    private static bool NearlyEqual(float a, float b)
    {
        return MathF.Abs(a - b) <= Epsilon;
    }

    private static void AppendFace(
        StaticVoxelRenderCell cell,
        FaceDefinition face,
        List<StaticWorldVertex> vertices,
        List<uint> indices)
    {
        uint firstIndex = (uint)vertices.Count;
        Vector3[] positions = face.GetPositions(cell);
        vertices.Add(new StaticWorldVertex(positions[0], face.Normal, new Vector2(0f, 0f)));
        vertices.Add(new StaticWorldVertex(positions[1], face.Normal, new Vector2(1f, 0f)));
        vertices.Add(new StaticWorldVertex(positions[2], face.Normal, new Vector2(1f, 1f)));
        vertices.Add(new StaticWorldVertex(positions[3], face.Normal, new Vector2(0f, 1f)));

        indices.Add(firstIndex);
        indices.Add(firstIndex + 1);
        indices.Add(firstIndex + 2);
        indices.Add(firstIndex);
        indices.Add(firstIndex + 2);
        indices.Add(firstIndex + 3);
    }

    private enum FaceDirection
    {
        PositiveX,
        NegativeX,
        PositiveY,
        NegativeY,
        PositiveZ,
        NegativeZ
    }

    private readonly record struct FaceDefinition(FaceDirection Direction, Vector3 Normal)
    {
        public static readonly FaceDefinition[] All =
        [
            new(FaceDirection.PositiveX, Vector3.UnitX),
            new(FaceDirection.NegativeX, -Vector3.UnitX),
            new(FaceDirection.PositiveY, Vector3.UnitY),
            new(FaceDirection.NegativeY, -Vector3.UnitY),
            new(FaceDirection.PositiveZ, Vector3.UnitZ),
            new(FaceDirection.NegativeZ, -Vector3.UnitZ)
        ];

        public Vector3[] GetPositions(StaticVoxelRenderCell cell)
        {
            Vector3 min = cell.Min;
            Vector3 max = cell.Max;

            return Direction switch
            {
                FaceDirection.PositiveX =>
                [
                    new(max.X, min.Y, min.Z),
                    new(max.X, max.Y, min.Z),
                    new(max.X, max.Y, max.Z),
                    new(max.X, min.Y, max.Z)
                ],
                FaceDirection.NegativeX =>
                [
                    new(min.X, min.Y, max.Z),
                    new(min.X, max.Y, max.Z),
                    new(min.X, max.Y, min.Z),
                    new(min.X, min.Y, min.Z)
                ],
                FaceDirection.PositiveY =>
                [
                    new(min.X, max.Y, max.Z),
                    new(max.X, max.Y, max.Z),
                    new(max.X, max.Y, min.Z),
                    new(min.X, max.Y, min.Z)
                ],
                FaceDirection.NegativeY =>
                [
                    new(min.X, min.Y, min.Z),
                    new(max.X, min.Y, min.Z),
                    new(max.X, min.Y, max.Z),
                    new(min.X, min.Y, max.Z)
                ],
                FaceDirection.PositiveZ =>
                [
                    new(min.X, min.Y, max.Z),
                    new(max.X, min.Y, max.Z),
                    new(max.X, max.Y, max.Z),
                    new(min.X, max.Y, max.Z)
                ],
                FaceDirection.NegativeZ =>
                [
                    new(max.X, min.Y, min.Z),
                    new(min.X, min.Y, min.Z),
                    new(min.X, max.Y, min.Z),
                    new(max.X, max.Y, min.Z)
                ],
                _ => throw new InvalidOperationException("Unknown face direction.")
            };
        }
    }

    private sealed class FaceOccluderIndex
    {
        private readonly Dictionary<int, List<StaticVoxelRenderCell>> _positiveX = new();
        private readonly Dictionary<int, List<StaticVoxelRenderCell>> _negativeX = new();
        private readonly Dictionary<int, List<StaticVoxelRenderCell>> _positiveY = new();
        private readonly Dictionary<int, List<StaticVoxelRenderCell>> _negativeY = new();
        private readonly Dictionary<int, List<StaticVoxelRenderCell>> _positiveZ = new();
        private readonly Dictionary<int, List<StaticVoxelRenderCell>> _negativeZ = new();

        public static FaceOccluderIndex Create(IReadOnlyList<StaticVoxelRenderCell> occluderCells)
        {
            FaceOccluderIndex index = new();
            foreach (StaticVoxelRenderCell occluder in occluderCells)
            {
                index.Add(index._positiveX, occluder.Min.X, occluder);
                index.Add(index._negativeX, occluder.Max.X, occluder);
                index.Add(index._positiveY, occluder.Min.Y, occluder);
                index.Add(index._negativeY, occluder.Max.Y, occluder);
                index.Add(index._positiveZ, occluder.Min.Z, occluder);
                index.Add(index._negativeZ, occluder.Max.Z, occluder);
            }

            return index;
        }

        public IEnumerable<StaticVoxelRenderCell> GetCandidates(
            StaticVoxelRenderCell cell,
            FaceDirection direction)
        {
            Dictionary<int, List<StaticVoxelRenderCell>> index = GetIndex(direction);
            int key = Quantize(GetFacePlane(cell, direction));
            for (int offset = -1; offset <= 1; offset++)
            {
                if (!index.TryGetValue(key + offset, out List<StaticVoxelRenderCell>? candidates))
                {
                    continue;
                }

                foreach (StaticVoxelRenderCell candidate in candidates)
                {
                    yield return candidate;
                }
            }
        }

        private Dictionary<int, List<StaticVoxelRenderCell>> GetIndex(FaceDirection direction)
        {
            return direction switch
            {
                FaceDirection.PositiveX => _positiveX,
                FaceDirection.NegativeX => _negativeX,
                FaceDirection.PositiveY => _positiveY,
                FaceDirection.NegativeY => _negativeY,
                FaceDirection.PositiveZ => _positiveZ,
                FaceDirection.NegativeZ => _negativeZ,
                _ => throw new InvalidOperationException("Unknown face direction.")
            };
        }

        private static float GetFacePlane(StaticVoxelRenderCell cell, FaceDirection direction)
        {
            return direction switch
            {
                FaceDirection.PositiveX => cell.Max.X,
                FaceDirection.NegativeX => cell.Min.X,
                FaceDirection.PositiveY => cell.Max.Y,
                FaceDirection.NegativeY => cell.Min.Y,
                FaceDirection.PositiveZ => cell.Max.Z,
                FaceDirection.NegativeZ => cell.Min.Z,
                _ => throw new InvalidOperationException("Unknown face direction.")
            };
        }

        private void Add(
            Dictionary<int, List<StaticVoxelRenderCell>> index,
            float plane,
            StaticVoxelRenderCell occluder)
        {
            int key = Quantize(plane);
            if (!index.TryGetValue(key, out List<StaticVoxelRenderCell>? candidates))
            {
                candidates = new List<StaticVoxelRenderCell>();
                index[key] = candidates;
            }

            candidates.Add(occluder);
        }

        private static int Quantize(float value)
        {
            return (int)MathF.Round(value / Epsilon);
        }
    }
}
