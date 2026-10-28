using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Mathematics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Multiplayer.Protocol;

// Protocol v3 compact world (SCALE-1). Replaces the reflection contract of WorldSnapshot, which
// spent about 127 B per placed cube and 620 B per rubble cell, with flat integer arrays:
//
//   {"w":width,"h":height,"d":depth,"seq":mutationSequence?,
//    "p":[cube...],"s":[subcell...],"r":[rubble...]}
//
//   cube    = dx,dy,dz,t[,material][,ox,oy,oz]
//             The cell, as a delta from the previous cube's cell (the first from 0,0,0).
//             t = idDelta * 8 + flags. Flag 4: the material follows (the first cube, then every
//             material change: runs of one material cost nothing). Flag 2: the cube has a replicated
//             id, idDelta from the previous id (the first from 0). Flag 1: the center is off the
//             cell center by (ox,oy,oz) / 4096 m; without it the cube rests at the cell center.
//   subcell = dx,dy,dz (delta from the previous subcell cell)
//   rubble  = dx,dy,dz,h[,material],(px,py,pz,side)*k
//             h = k * 2 + (1 when the material follows, as for cubes). k = 0 means the cell's
//             first-pass rubble (CubeFracturePattern.CreateFirstPassRubble), regenerated exactly on
//             decode; otherwise k pieces, each center relative to the cell origin and side length
//             in 1/4096 m.
//
// Missing arrays are empty. Each fragment of a chunked transfer (WorldChunkMessage) is a complete
// object in this shape with its own delta bases, so fragments decode independently and append.
// Cubes, subcells and rubble keep the order of the capture.
//
// Exactness: cells, materials, ids, subcells and first-pass rubble round-trip exactly; a cube
// resting at its cell center round-trips exactly; any other center and moved rubble pieces are
// quantized to 1/4096 m (0.24 mm). The world checksum covers only cells, kinds and materials.
public sealed class WorldSnapshotJsonConverter : JsonConverter<WorldSnapshot>
{
    public const int QuantizationScale = 4096;

    private const long FlagOffset = 1;
    private const long FlagId = 2;
    private const long FlagMaterial = 4;
    private const int CubeFlagBits = 8;

    internal static readonly JsonEncodedText Width = JsonEncodedText.Encode("w");
    internal static readonly JsonEncodedText Height = JsonEncodedText.Encode("h");
    internal static readonly JsonEncodedText Depth = JsonEncodedText.Encode("d");
    internal static readonly JsonEncodedText Sequence = JsonEncodedText.Encode("seq");
    internal static readonly JsonEncodedText Placed = JsonEncodedText.Encode("p");
    internal static readonly JsonEncodedText Subcells = JsonEncodedText.Encode("s");
    internal static readonly JsonEncodedText Rubble = JsonEncodedText.Encode("r");

    public override WorldSnapshot Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadWorld(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, WorldSnapshot value, JsonSerializerOptions options)
    {
        // A world over a capture is written from it; any other world is captured for the write
        // and that temporary capture's arrays go back to the pool afterwards.
        bool temporary = !WorldSnapshotCapture.TryGetCapture(value, out WorldSnapshotCapture? capture);
        capture ??= WorldSnapshotCapture.FromSnapshot(value);
        try
        {
            Cursor cursor = default;
            WriteWorld(writer, capture, ref cursor, long.MaxValue);
        }
        finally
        {
            if (temporary)
            {
                capture.Release();
            }
        }
    }

    // Where a chunked write stopped: the section (0 cubes, 1 subcells, 2 rubble, 3 done) and the
    // next item in it.
    public struct Cursor
    {
        public int Section;
        public int Index;

        public readonly bool IsComplete => Section >= 3;
    }

    // Writes one world object with the items from cursor onward. With a byte budget it stops after
    // the first item that brings the object (counted from its start) to the budget, so every call
    // writes at least one item and an object is at most the budget plus one item and the closing
    // brackets. Returns true when every item has been written.
    public static bool WriteWorld(Utf8JsonWriter writer, WorldSnapshotCapture capture, ref Cursor cursor, long byteBudget)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(capture);
        capture.ThrowIfReleased();
        long start = writer.BytesCommitted + writer.BytesPending;
        writer.WriteStartObject();
        writer.WriteNumber(Width, capture.Width);
        writer.WriteNumber(Height, capture.Height);
        writer.WriteNumber(Depth, capture.Depth);
        if (capture.MutationSequence is ulong sequence)
        {
            writer.WriteNumber(Sequence, sequence);
        }

        bool overBudget = false;
        if (cursor.Section == 0)
        {
            if (cursor.Index < capture.CubeCount)
            {
                overBudget = WriteCubes(writer, capture, ref cursor.Index, start, byteBudget);
            }

            if (cursor.Index >= capture.CubeCount)
            {
                cursor.Section = 1;
                cursor.Index = 0;
            }
        }

        if (!overBudget && cursor.Section == 1)
        {
            if (cursor.Index < capture.SubcellCount)
            {
                overBudget = WriteSubcells(writer, capture, ref cursor.Index, start, byteBudget);
            }

            if (cursor.Index >= capture.SubcellCount)
            {
                cursor.Section = 2;
                cursor.Index = 0;
            }
        }

        if (!overBudget && cursor.Section == 2)
        {
            if (cursor.Index < capture.RubbleCount)
            {
                WriteRubble(writer, capture, ref cursor.Index, start, byteBudget);
            }

            if (cursor.Index >= capture.RubbleCount)
            {
                cursor.Section = 3;
                cursor.Index = 0;
            }
        }

        writer.WriteEndObject();
        return cursor.IsComplete;
    }

    private static bool IsOverBudget(Utf8JsonWriter writer, long start, long byteBudget)
    {
        return writer.BytesCommitted + writer.BytesPending - start >= byteBudget;
    }

    private static bool WriteCubes(Utf8JsonWriter writer, WorldSnapshotCapture capture, ref int index, long start, long byteBudget)
    {
        writer.WritePropertyName(Placed);
        writer.WriteStartArray();
        int px = 0, py = 0, pz = 0;
        long previousId = 0;
        int previousMaterial = -1;
        bool overBudget = false;
        int[] cells = capture.CubeCells;
        float[] centers = capture.CubeCenters;
        while (index < capture.CubeCount && !overBudget)
        {
            int at = index * 3;
            int x = cells[at], y = cells[at + 1], z = cells[at + 2];
            writer.WriteNumberValue((long)x - px);
            writer.WriteNumberValue((long)y - py);
            writer.WriteNumberValue((long)z - pz);
            px = x;
            py = y;
            pz = z;

            long ox = Quantize(centers[at] - (x + 0.5f));
            long oy = Quantize(centers[at + 1] - (y + 0.5f));
            long oz = Quantize(centers[at + 2] - (z + 0.5f));
            bool hasOffset = ox != 0 || oy != 0 || oz != 0;
            int material = capture.CubeMaterials[index];
            bool materialFollows = material != previousMaterial;
            long flags = (hasOffset ? FlagOffset : 0) | (materialFollows ? FlagMaterial : 0);
            long idDelta = 0;
            if (capture.CubeHasId[index])
            {
                long id = capture.CubeIds[index];
                idDelta = id - previousId;
                previousId = id;
                flags |= FlagId;
            }

            writer.WriteNumberValue((idDelta * CubeFlagBits) + flags);
            if (materialFollows)
            {
                writer.WriteNumberValue(material);
                previousMaterial = material;
            }

            if (hasOffset)
            {
                writer.WriteNumberValue(ox);
                writer.WriteNumberValue(oy);
                writer.WriteNumberValue(oz);
            }

            index++;
            overBudget = IsOverBudget(writer, start, byteBudget);
        }

        writer.WriteEndArray();
        return overBudget;
    }

    private static bool WriteSubcells(Utf8JsonWriter writer, WorldSnapshotCapture capture, ref int index, long start, long byteBudget)
    {
        writer.WritePropertyName(Subcells);
        writer.WriteStartArray();
        int px = 0, py = 0, pz = 0;
        bool overBudget = false;
        int[] cells = capture.SubcellCells;
        while (index < capture.SubcellCount && !overBudget)
        {
            int at = index * 3;
            int x = cells[at], y = cells[at + 1], z = cells[at + 2];
            writer.WriteNumberValue((long)x - px);
            writer.WriteNumberValue((long)y - py);
            writer.WriteNumberValue((long)z - pz);
            px = x;
            py = y;
            pz = z;
            index++;
            overBudget = IsOverBudget(writer, start, byteBudget);
        }

        writer.WriteEndArray();
        return overBudget;
    }

    private static bool WriteRubble(Utf8JsonWriter writer, WorldSnapshotCapture capture, ref int index, long start, long byteBudget)
    {
        writer.WritePropertyName(Rubble);
        writer.WriteStartArray();
        int px = 0, py = 0, pz = 0;
        int previousMaterial = -1;
        bool overBudget = false;
        int[] cells = capture.RubbleCells;
        while (index < capture.RubbleCount && !overBudget)
        {
            int at = index * 3;
            int x = cells[at], y = cells[at + 1], z = cells[at + 2];
            writer.WriteNumberValue((long)x - px);
            writer.WriteNumberValue((long)y - py);
            writer.WriteNumberValue((long)z - pz);
            px = x;
            py = y;
            pz = z;

            int firstPiece = capture.RubblePieceStarts[index];
            int pieceCount = capture.RubblePieceStarts[index + 1] - firstPiece;
            // No pieces at all (every one was invalid) decodes to the first-pass rubble too, which
            // is what the client's fallback fracture made of an empty piece list.
            bool firstPass = pieceCount == 0 || IsFirstPassRubble(capture, x, y, z, firstPiece, pieceCount);
            int written = firstPass ? 0 : pieceCount;
            int material = capture.RubbleMaterials[index];
            bool materialFollows = material != previousMaterial;
            writer.WriteNumberValue(((long)written * 2) + (materialFollows ? 1 : 0));
            if (materialFollows)
            {
                writer.WriteNumberValue(material);
                previousMaterial = material;
            }

            float[] pieces = capture.Pieces;
            for (int p = 0; p < written; p++)
            {
                int piece = (firstPiece + p) * 4;
                writer.WriteNumberValue(Quantize(pieces[piece] - x));
                writer.WriteNumberValue(Quantize(pieces[piece + 1] - y));
                writer.WriteNumberValue(Quantize(pieces[piece + 2] - z));
                writer.WriteNumberValue(Quantize(pieces[piece + 3]));
            }

            index++;
            overBudget = IsOverBudget(writer, start, byteBudget);
        }

        writer.WriteEndArray();
        return overBudget;
    }

    // Exact float comparison: only rubble that never moved since its fracture qualifies.
    private static bool IsFirstPassRubble(WorldSnapshotCapture capture, int x, int y, int z, int firstPiece, int pieceCount)
    {
        IReadOnlyList<GeometryFracturePiece> pattern = CubeFracturePattern.CreateFirstPassRubble(new GeometryCellKey(x, y, z));
        if (pattern.Count != pieceCount)
        {
            return false;
        }

        for (int p = 0; p < pieceCount; p++)
        {
            if (capture.GetPiece(firstPiece + p) != pattern[p])
            {
                return false;
            }
        }

        return true;
    }

    // Non-finite values (never expected) quantize to 0, and the range is clamped so a corrupt
    // value cannot overflow the integer.
    private static long Quantize(float value)
    {
        if (!float.IsFinite(value))
        {
            return 0;
        }

        double scaled = Math.Round((double)value * QuantizationScale, MidpointRounding.AwayFromZero);
        return (long)Math.Clamp(scaled, int.MinValue, int.MaxValue);
    }

    private static float Dequantize(long units, int origin)
    {
        return (float)(origin + ((double)units / QuantizationScale));
    }

    public static WorldSnapshot ReadWorld(ref Utf8JsonReader reader)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(WorldSnapshot));
        int width = 0, height = 0, depth = 0;
        ulong? sequence = null;
        List<PlacedCubeSnapshot>? cubes = null;
        List<Vector3>? subcells = null;
        List<FracturedCubeSnapshot>? rubble = null;
        while (HotPathJson.NextProperty(ref reader, nameof(WorldSnapshot)))
        {
            if (reader.ValueTextEquals(Width.EncodedUtf8Bytes))
            {
                width = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(Height.EncodedUtf8Bytes))
            {
                height = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(Depth.EncodedUtf8Bytes))
            {
                depth = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(Sequence.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                if (reader.TokenType == JsonTokenType.Null)
                {
                    sequence = null;
                }
                else if (reader.TokenType == JsonTokenType.Number && reader.TryGetUInt64(out ulong value))
                {
                    sequence = value;
                }
                else
                {
                    throw new JsonException("Compact world seq must be an unsigned 64-bit integer.");
                }
            }
            else if (reader.ValueTextEquals(Placed.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                cubes = ReadCubes(ref reader);
            }
            else if (reader.ValueTextEquals(Subcells.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                subcells = ReadSubcells(ref reader);
            }
            else if (reader.ValueTextEquals(Rubble.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                rubble = ReadRubble(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        ValidateDimensions(width, height, depth);
        return Create(width, height, depth, sequence, cubes, subcells, rubble);
    }

    // The largest width, height or depth a decoded world may announce, and the most cells it may
    // span. Today's worlds are at most 100 x 8 x 100; the receiver sizes its item budget from these
    // dimensions (WorldTransferLimits), so they must be sane before anything trusts them.
    public const int MaxDimension = 1 << 14;
    public const long MaxCells = 1L << 30;

    private static void ValidateDimensions(int width, int height, int depth)
    {
        if (width < 0 || height < 0 || depth < 0)
        {
            throw new JsonException($"Compact world dimensions {width}x{height}x{depth} must not be negative.");
        }

        if (width > MaxDimension || height > MaxDimension || depth > MaxDimension ||
            (long)width * height * depth > MaxCells)
        {
            throw new JsonException(
                $"Compact world dimensions {width}x{height}x{depth} are out of range (at most {MaxDimension} per axis and {MaxCells} cells).");
        }
    }

    // A running delta sum. A crafted delta must not wrap the long and land back in the int range.
    private static long Accumulate(long total, long delta)
    {
        try
        {
            return checked(total + delta);
        }
        catch (OverflowException)
        {
            throw new JsonException("Compact world delta overflows.");
        }
    }

    // A decoded world in the list shape the list-based export produced: PlacedCubeCells holds the
    // cubes' cells and FracturedCubeCells the rubble cells.
    internal static WorldSnapshot Create(
        int width,
        int height,
        int depth,
        ulong? sequence,
        IReadOnlyList<PlacedCubeSnapshot>? cubes,
        IReadOnlyList<Vector3>? subcells,
        IReadOnlyList<FracturedCubeSnapshot>? rubble)
    {
        cubes ??= Array.Empty<PlacedCubeSnapshot>();
        rubble ??= Array.Empty<FracturedCubeSnapshot>();
        Vector3[] cubeCells = new Vector3[cubes.Count];
        for (int i = 0; i < cubeCells.Length; i++)
        {
            cubeCells[i] = cubes[i].CellPosition;
        }

        Vector3[] rubbleCells = new Vector3[rubble.Count];
        for (int i = 0; i < rubbleCells.Length; i++)
        {
            rubbleCells[i] = rubble[i].CellPosition;
        }

        return new WorldSnapshot(
            width,
            height,
            depth,
            cubeCells,
            rubbleCells,
            subcells ?? Array.Empty<Vector3>(),
            cubes,
            rubble,
            sequence);
    }

    private static List<PlacedCubeSnapshot>? ReadCubes(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        ExpectStartArray(ref reader, "p");
        List<PlacedCubeSnapshot> cubes = [];
        long x = 0, y = 0, z = 0;
        long previousId = 0;
        long material = -1;
        while (TryReadInteger(ref reader, out long dx))
        {
            x = Accumulate(x, dx);
            y = Accumulate(y, ReadInteger(ref reader));
            z = Accumulate(z, ReadInteger(ref reader));
            long t = ReadInteger(ref reader);
            long flags = t & (CubeFlagBits - 1);
            long idDelta = (t - flags) / CubeFlagBits;
            if ((flags & FlagMaterial) != 0)
            {
                material = ReadInteger(ref reader);
            }

            if (material < 0)
            {
                throw new JsonException("A compact world cube has no material.");
            }

            int cellX = ToInt32(x), cellY = ToInt32(y), cellZ = ToInt32(z);
            Vector3 center = new(cellX + 0.5f, cellY + 0.5f, cellZ + 0.5f);
            if ((flags & FlagOffset) != 0)
            {
                center = new Vector3(
                    (float)(cellX + 0.5 + ((double)ReadInteger(ref reader) / QuantizationScale)),
                    (float)(cellY + 0.5 + ((double)ReadInteger(ref reader) / QuantizationScale)),
                    (float)(cellZ + 0.5 + ((double)ReadInteger(ref reader) / QuantizationScale)));
            }

            int? actorId = null;
            if ((flags & FlagId) != 0)
            {
                previousId = Accumulate(previousId, idDelta);
                actorId = ToInt32(previousId);
            }
            else if (idDelta != 0)
            {
                throw new JsonException("A compact world cube without an id carries an id delta.");
            }

            cubes.Add(new PlacedCubeSnapshot(
                new Vector3(cellX, cellY, cellZ),
                center,
                (GeometryMaterialId)ToInt32(material),
                actorId));
        }

        return cubes;
    }

    private static List<Vector3>? ReadSubcells(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        ExpectStartArray(ref reader, "s");
        List<Vector3> cells = [];
        long x = 0, y = 0, z = 0;
        while (TryReadInteger(ref reader, out long dx))
        {
            x = Accumulate(x, dx);
            y = Accumulate(y, ReadInteger(ref reader));
            z = Accumulate(z, ReadInteger(ref reader));
            cells.Add(new Vector3(ToInt32(x), ToInt32(y), ToInt32(z)));
        }

        return cells;
    }

    private static List<FracturedCubeSnapshot>? ReadRubble(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        ExpectStartArray(ref reader, "r");
        List<FracturedCubeSnapshot> rubble = [];
        long x = 0, y = 0, z = 0;
        long material = -1;
        while (TryReadInteger(ref reader, out long dx))
        {
            x = Accumulate(x, dx);
            y = Accumulate(y, ReadInteger(ref reader));
            z = Accumulate(z, ReadInteger(ref reader));
            long h = ReadInteger(ref reader);
            if (h < 0)
            {
                throw new JsonException("A compact world rubble cell has a negative piece count.");
            }

            if ((h & 1) != 0)
            {
                material = ReadInteger(ref reader);
            }

            if (material < 0)
            {
                throw new JsonException("A compact world rubble cell has no material.");
            }

            long pieceCount = h / 2;
            int cellX = ToInt32(x), cellY = ToInt32(y), cellZ = ToInt32(z);
            IReadOnlyList<GeometryFracturePiece> pieces;
            if (pieceCount == 0)
            {
                pieces = CubeFracturePattern.CreateFirstPassRubble(new GeometryCellKey(cellX, cellY, cellZ));
            }
            else
            {
                if (pieceCount > 4096)
                {
                    throw new JsonException("A compact world rubble cell has too many pieces.");
                }

                GeometryFracturePiece[] read = new GeometryFracturePiece[pieceCount];
                for (int p = 0; p < read.Length; p++)
                {
                    float cx = Dequantize(ReadInteger(ref reader), cellX);
                    float cy = Dequantize(ReadInteger(ref reader), cellY);
                    float cz = Dequantize(ReadInteger(ref reader), cellZ);
                    float side = Dequantize(ReadInteger(ref reader), 0);
                    read[p] = new GeometryFracturePiece(new Vector3(cx, cy, cz), side);
                }

                pieces = read;
            }

            rubble.Add(new FracturedCubeSnapshot(
                new Vector3(cellX, cellY, cellZ),
                (GeometryMaterialId)ToInt32(material),
                pieces));
        }

        return rubble;
    }

    private static void ExpectStartArray(ref Utf8JsonReader reader, string property)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Compact world property '{property}' must be an array.");
        }
    }

    // The next array element, or false at the end of the array.
    private static bool TryReadInteger(ref Utf8JsonReader reader, out long value)
    {
        if (!reader.Read())
        {
            throw new JsonException("Compact world array is truncated.");
        }

        if (reader.TokenType == JsonTokenType.EndArray)
        {
            value = 0;
            return false;
        }

        value = GetInteger(ref reader);
        return true;
    }

    private static long ReadInteger(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
        {
            throw new JsonException("Compact world record is truncated.");
        }

        return GetInteger(ref reader);
    }

    private static long GetInteger(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out long value))
        {
            throw new JsonException("Compact world arrays hold integers only.");
        }

        return value;
    }

    private static int ToInt32(long value)
    {
        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new JsonException("Compact world value is out of range.");
        }

        return (int)value;
    }
}
