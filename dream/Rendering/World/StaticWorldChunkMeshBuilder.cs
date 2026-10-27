using System.Buffers;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.World;

// Runs on worker threads: scratch storage is rented per call from ArrayPool, never kept in statics.
public static class StaticWorldChunkMeshBuilder
{
    private const float Epsilon = 0.0001f;
    private const int VerticesPerFace = 4;
    private const int IndicesPerFace = 6;

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
        int cellCount = materialCells.Count;
        int includedFaceMask = GetIncludedFaceMask(materialId, materialRole);
        byte[] exposedFaceMasks = ArrayPool<byte>.Shared.Rent(cellCount);
        FaceOccluderIndex occluderIndex = FaceOccluderIndex.Create(occluderCells);
        try
        {
            // Pass 1 decides which faces are exposed so the output lists are allocated once at their final size.
            int exposedFaceCount = 0;
            for (int cellIndex = 0; cellIndex < cellCount; cellIndex++)
            {
                StaticVoxelRenderCell cell = materialCells[cellIndex];
                Vector3 min = cell.Min;
                Vector3 max = cell.Max;
                int exposedMask = 0;
                for (int faceIndex = 0; faceIndex < FaceDefinition.All.Length; faceIndex++)
                {
                    int faceBit = 1 << faceIndex;
                    if ((includedFaceMask & faceBit) == 0 ||
                        occluderIndex.IsFaceCovered(min, max, FaceDefinition.All[faceIndex].Direction))
                    {
                        continue;
                    }

                    exposedMask |= faceBit;
                    exposedFaceCount++;
                }

                exposedFaceMasks[cellIndex] = (byte)exposedMask;
            }

            List<StaticWorldVertex> vertices = new(exposedFaceCount * VerticesPerFace);
            List<uint> indices = new(exposedFaceCount * IndicesPerFace);
            for (int cellIndex = 0; cellIndex < cellCount; cellIndex++)
            {
                int exposedMask = exposedFaceMasks[cellIndex];
                if (exposedMask == 0)
                {
                    continue;
                }

                StaticVoxelRenderCell cell = materialCells[cellIndex];
                for (int faceIndex = 0; faceIndex < FaceDefinition.All.Length; faceIndex++)
                {
                    if ((exposedMask & (1 << faceIndex)) != 0)
                    {
                        AppendFace(cell, FaceDefinition.All[faceIndex], vertices, indices);
                    }
                }
            }

            return new StaticWorldChunkMesh(
                key,
                materialId,
                materialRole,
                vertices,
                indices,
                StaticWorldChunkBounds.FromCells(materialCells),
                cellCount,
                exposedFaceCount);
        }
        finally
        {
            occluderIndex.Release();
            ArrayPool<byte>.Shared.Return(exposedFaceMasks);
        }
    }

    private static int GetIncludedFaceMask(GeometryMaterialId materialId, StaticTerrainMaterialRole materialRole)
    {
        int mask = 0;
        for (int faceIndex = 0; faceIndex < FaceDefinition.All.Length; faceIndex++)
        {
            if (materialRole == StaticTerrainMaterialRole.Single ||
                StaticTerrainMaterialCatalog.ResolveRoleForNormal(materialId, FaceDefinition.All[faceIndex].Normal) == materialRole)
            {
                mask |= 1 << faceIndex;
            }
        }

        return mask;
    }

    private static bool CoversFace(
        Vector3 cellMin,
        Vector3 cellMax,
        Vector3 occluderMin,
        Vector3 occluderMax,
        FaceDirection direction)
    {
        return direction switch
        {
            FaceDirection.PositiveX =>
                NearlyEqual(occluderMin.X, cellMax.X) &&
                Covers(occluderMin.Y, occluderMax.Y, cellMin.Y, cellMax.Y) &&
                Covers(occluderMin.Z, occluderMax.Z, cellMin.Z, cellMax.Z),
            FaceDirection.NegativeX =>
                NearlyEqual(occluderMax.X, cellMin.X) &&
                Covers(occluderMin.Y, occluderMax.Y, cellMin.Y, cellMax.Y) &&
                Covers(occluderMin.Z, occluderMax.Z, cellMin.Z, cellMax.Z),
            FaceDirection.PositiveY =>
                NearlyEqual(occluderMin.Y, cellMax.Y) &&
                Covers(occluderMin.X, occluderMax.X, cellMin.X, cellMax.X) &&
                Covers(occluderMin.Z, occluderMax.Z, cellMin.Z, cellMax.Z),
            FaceDirection.NegativeY =>
                NearlyEqual(occluderMax.Y, cellMin.Y) &&
                Covers(occluderMin.X, occluderMax.X, cellMin.X, cellMax.X) &&
                Covers(occluderMin.Z, occluderMax.Z, cellMin.Z, cellMax.Z),
            FaceDirection.PositiveZ =>
                NearlyEqual(occluderMin.Z, cellMax.Z) &&
                Covers(occluderMin.X, occluderMax.X, cellMin.X, cellMax.X) &&
                Covers(occluderMin.Y, occluderMax.Y, cellMin.Y, cellMax.Y),
            FaceDirection.NegativeZ =>
                NearlyEqual(occluderMax.Z, cellMin.Z) &&
                Covers(occluderMin.X, occluderMax.X, cellMin.X, cellMax.X) &&
                Covers(occluderMin.Y, occluderMax.Y, cellMin.Y, cellMax.Y),
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
        Span<Vector3> positions = stackalloc Vector3[VerticesPerFace];
        Span<Vector2> texCoords = stackalloc Vector2[VerticesPerFace];
        face.WritePositions(cell, positions);
        face.WriteTexCoords(cell, texCoords);
        vertices.Add(new StaticWorldVertex(positions[0], face.Normal, texCoords[0]));
        vertices.Add(new StaticWorldVertex(positions[1], face.Normal, texCoords[1]));
        vertices.Add(new StaticWorldVertex(positions[2], face.Normal, texCoords[2]));
        vertices.Add(new StaticWorldVertex(positions[3], face.Normal, texCoords[3]));

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

        public void WritePositions(StaticVoxelRenderCell cell, Span<Vector3> positions)
        {
            Vector3 min = cell.Min;
            Vector3 max = cell.Max;

            switch (Direction)
            {
                case FaceDirection.PositiveX:
                    positions[0] = new Vector3(max.X, min.Y, min.Z);
                    positions[1] = new Vector3(max.X, max.Y, min.Z);
                    positions[2] = new Vector3(max.X, max.Y, max.Z);
                    positions[3] = new Vector3(max.X, min.Y, max.Z);
                    break;
                case FaceDirection.NegativeX:
                    positions[0] = new Vector3(min.X, min.Y, max.Z);
                    positions[1] = new Vector3(min.X, max.Y, max.Z);
                    positions[2] = new Vector3(min.X, max.Y, min.Z);
                    positions[3] = new Vector3(min.X, min.Y, min.Z);
                    break;
                case FaceDirection.PositiveY:
                    positions[0] = new Vector3(min.X, max.Y, max.Z);
                    positions[1] = new Vector3(max.X, max.Y, max.Z);
                    positions[2] = new Vector3(max.X, max.Y, min.Z);
                    positions[3] = new Vector3(min.X, max.Y, min.Z);
                    break;
                case FaceDirection.NegativeY:
                    positions[0] = new Vector3(min.X, min.Y, min.Z);
                    positions[1] = new Vector3(max.X, min.Y, min.Z);
                    positions[2] = new Vector3(max.X, min.Y, max.Z);
                    positions[3] = new Vector3(min.X, min.Y, max.Z);
                    break;
                case FaceDirection.PositiveZ:
                    positions[0] = new Vector3(min.X, min.Y, max.Z);
                    positions[1] = new Vector3(max.X, min.Y, max.Z);
                    positions[2] = new Vector3(max.X, max.Y, max.Z);
                    positions[3] = new Vector3(min.X, max.Y, max.Z);
                    break;
                case FaceDirection.NegativeZ:
                    positions[0] = new Vector3(max.X, min.Y, min.Z);
                    positions[1] = new Vector3(min.X, min.Y, min.Z);
                    positions[2] = new Vector3(min.X, max.Y, min.Z);
                    positions[3] = new Vector3(max.X, max.Y, min.Z);
                    break;
                default:
                    throw new InvalidOperationException("Unknown face direction.");
            }
        }

        public void WriteTexCoords(StaticVoxelRenderCell cell, Span<Vector2> texCoords)
        {
            Vector3 size = cell.Size;
            switch (Direction)
            {
                case FaceDirection.PositiveX:
                case FaceDirection.NegativeX:
                    texCoords[0] = new Vector2(0f, 0f);
                    texCoords[1] = new Vector2(0f, size.Y);
                    texCoords[2] = new Vector2(size.Z, size.Y);
                    texCoords[3] = new Vector2(size.Z, 0f);
                    break;
                case FaceDirection.PositiveY:
                    texCoords[0] = new Vector2(0f, size.Z);
                    texCoords[1] = new Vector2(size.X, size.Z);
                    texCoords[2] = new Vector2(size.X, 0f);
                    texCoords[3] = new Vector2(0f, 0f);
                    break;
                case FaceDirection.NegativeY:
                    texCoords[0] = new Vector2(0f, 0f);
                    texCoords[1] = new Vector2(size.X, 0f);
                    texCoords[2] = new Vector2(size.X, size.Z);
                    texCoords[3] = new Vector2(0f, size.Z);
                    break;
                case FaceDirection.PositiveZ:
                case FaceDirection.NegativeZ:
                    texCoords[0] = new Vector2(0f, 0f);
                    texCoords[1] = new Vector2(size.X, 0f);
                    texCoords[2] = new Vector2(size.X, size.Y);
                    texCoords[3] = new Vector2(0f, size.Y);
                    break;
                default:
                    throw new InvalidOperationException("Unknown face direction.");
            }
        }
    }

    // Per direction, occluders sorted by the quantized plane their touching face lies on.
    // A face looks up the occluders whose key is within one step of its own plane key.
    private readonly struct FaceOccluderIndex
    {
        private const int DirectionCount = 6;
        private readonly int _count;
        private readonly int[] _planeKeys;
        private readonly int[] _occluderSlots;
        private readonly Vector3[] _mins;
        private readonly Vector3[] _maxs;

        private FaceOccluderIndex(int count, int[] planeKeys, int[] occluderSlots, Vector3[] mins, Vector3[] maxs)
        {
            _count = count;
            _planeKeys = planeKeys;
            _occluderSlots = occluderSlots;
            _mins = mins;
            _maxs = maxs;
        }

        public static FaceOccluderIndex Create(IReadOnlyList<StaticVoxelRenderCell> occluderCells)
        {
            int count = occluderCells.Count;
            int[] planeKeys = ArrayPool<int>.Shared.Rent(count * DirectionCount);
            int[] occluderSlots = ArrayPool<int>.Shared.Rent(count * DirectionCount);
            Vector3[] mins = ArrayPool<Vector3>.Shared.Rent(count);
            Vector3[] maxs = ArrayPool<Vector3>.Shared.Rent(count);

            for (int i = 0; i < count; i++)
            {
                StaticVoxelRenderCell occluder = occluderCells[i];
                Vector3 min = occluder.Min;
                Vector3 max = occluder.Max;
                mins[i] = min;
                maxs[i] = max;
                planeKeys[((int)FaceDirection.PositiveX * count) + i] = Quantize(min.X);
                planeKeys[((int)FaceDirection.NegativeX * count) + i] = Quantize(max.X);
                planeKeys[((int)FaceDirection.PositiveY * count) + i] = Quantize(min.Y);
                planeKeys[((int)FaceDirection.NegativeY * count) + i] = Quantize(max.Y);
                planeKeys[((int)FaceDirection.PositiveZ * count) + i] = Quantize(min.Z);
                planeKeys[((int)FaceDirection.NegativeZ * count) + i] = Quantize(max.Z);
                for (int direction = 0; direction < DirectionCount; direction++)
                {
                    occluderSlots[(direction * count) + i] = i;
                }
            }

            for (int direction = 0; direction < DirectionCount; direction++)
            {
                planeKeys.AsSpan(direction * count, count).Sort(occluderSlots.AsSpan(direction * count, count));
            }

            return new FaceOccluderIndex(count, planeKeys, occluderSlots, mins, maxs);
        }

        public void Release()
        {
            ArrayPool<int>.Shared.Return(_planeKeys);
            ArrayPool<int>.Shared.Return(_occluderSlots);
            ArrayPool<Vector3>.Shared.Return(_mins);
            ArrayPool<Vector3>.Shared.Return(_maxs);
        }

        public bool IsFaceCovered(Vector3 cellMin, Vector3 cellMax, FaceDirection direction)
        {
            int segmentStart = (int)direction * _count;
            ReadOnlySpan<int> keys = _planeKeys.AsSpan(segmentStart, _count);
            ReadOnlySpan<int> slots = _occluderSlots.AsSpan(segmentStart, _count);
            int key = Quantize(GetFacePlane(cellMin, cellMax, direction));
            for (int offset = -1; offset <= 1; offset++)
            {
                int candidateKey = unchecked(key + offset);
                for (int i = LowerBound(keys, candidateKey); i < keys.Length && keys[i] == candidateKey; i++)
                {
                    int slot = slots[i];
                    if (CoversFace(cellMin, cellMax, _mins[slot], _maxs[slot], direction))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static int LowerBound(ReadOnlySpan<int> keys, int value)
        {
            int low = 0;
            int high = keys.Length;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (keys[middle] < value)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

        private static float GetFacePlane(Vector3 cellMin, Vector3 cellMax, FaceDirection direction)
        {
            return direction switch
            {
                FaceDirection.PositiveX => cellMax.X,
                FaceDirection.NegativeX => cellMin.X,
                FaceDirection.PositiveY => cellMax.Y,
                FaceDirection.NegativeY => cellMin.Y,
                FaceDirection.PositiveZ => cellMax.Z,
                FaceDirection.NegativeZ => cellMin.Z,
                _ => throw new InvalidOperationException("Unknown face direction.")
            };
        }

        private static int Quantize(float value)
        {
            return (int)MathF.Round(value / Epsilon);
        }
    }
}
