using OpenTK.Mathematics;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Multiplayer.Protocol;

// Seeded random server snapshots for the WIRE-11 envelope converter tests: every mutation kind
// with every optional field present or absent, damage events, the world checksum and the full
// world present or absent, empty and large actor lists, edge floats and strings that need
// escaping. With roundTrippable set, no string contains a lone surrogate (written as U+FFFD).
internal static class ServerSnapshotSamples
{
    public static readonly float[] EdgeFloats =
    [
        0f, -0f, 1f, -1f, 0.1f, 1f / 3f, 1e-7f, -1e-7f, 16777217f, -123456.789f, 3.4e38f,
        float.Epsilon, -float.Epsilon, float.MaxValue, float.MinValue, 1e-45f, 2.5f, 100f, 0.5f, 1e10f
    ];

    public static readonly string[] EdgeStrings =
    [
        "",
        "session-a",
        "Wolf",
        "Q\"uote <b>&</b> caf" + (char)0x00E9 + " " + char.ConvertFromUtf32(0x1F389) + " back\\slash\ttab\nline '" + (char)0x2028 + "'",
        "+plus/slash?query=1&x=<y>",
        "\u0000\u0001\u001f\u007f",
        "lone " + (char)0xD800 + " surrogate",
        new string('x', 300)
    ];

    public static ServerSnapshotEnvelope RandomSnapshot(Random random, bool roundTrippable = false, int? actorCount = null)
    {
        int actors = actorCount ?? random.Next(5) switch
        {
            0 => 0,
            1 => random.Next(40, 120),
            _ => random.Next(1, 12)
        };

        return new ServerSnapshotEnvelope(
            random.Next(5) == 0 ? ulong.MaxValue : (ulong)random.NextInt64(),
            random.Next(12) == 0 ? null! : RandomSequences(random, roundTrippable),
            random.Next(20) == 0 ? null! : RandomActors(random, actors, roundTrippable),
            (PlayerInteractionMode)random.Next(-1, 3),
            random.Next(4) == 0 ? RandomWorld(random) : null,
            random.Next(12) == 0 ? null! : RandomMutations(random, random.Next(4) == 0 ? random.Next(20, 60) : random.Next(0, 6)),
            random.Next(12) == 0 ? null! : RandomDamageEvents(random, random.Next(0, 6)),
            random.Next(2) == 0 ? null : (random.Next(4) == 0 ? ulong.MaxValue : (ulong)random.NextInt64()),
            random.Next(3) == 0 ? 0 : (ulong)random.NextInt64());
    }

    // Insertion with removals and re-adds, so the dictionary's enumeration order is not plain
    // insertion order (a removed slot is reused by the next add).
    public static Dictionary<string, ulong> RandomSequences(Random random, bool roundTrippable)
    {
        Dictionary<string, ulong> sequences = new(StringComparer.Ordinal);
        int count = random.Next(0, 10);
        for (int i = 0; i < count; i++)
        {
            sequences[RandomString(random, roundTrippable) + (random.Next(3) == 0 ? "" : "-" + i)] =
                random.Next(4) == 0 ? ulong.MaxValue : (ulong)random.NextInt64();
            if (sequences.Count > 2 && random.Next(4) == 0)
            {
                sequences.Remove(sequences.Keys.ElementAt(random.Next(sequences.Count)));
            }
        }

        return sequences;
    }

    public static WorldMutationDelta[] RandomMutations(Random random, int count)
    {
        WorldMutationDelta[] mutations = new WorldMutationDelta[count];
        for (int i = 0; i < count; i++)
        {
            mutations[i] = RandomMutation(random, (WorldMutationKind)random.Next(0, 5), random.Next(64));
        }

        return mutations;
    }

    // Each of the six low mask bits makes one optional field present.
    public static WorldMutationDelta RandomMutation(Random random, WorldMutationKind kind, int mask)
    {
        return new WorldMutationDelta(
            kind,
            RandomVector(random),
            (mask & 1) != 0 ? RandomVector(random) : null,
            (mask & 2) != 0 ? RandomVector(random) : null,
            (mask & 4) != 0 ? (GeometryMaterialId)random.Next(-1, 9) : null,
            (mask & 8) != 0 ? RandomPieces(random, random.Next(0, 9)) : null,
            (mask & 16) != 0 ? (random.Next(4) == 0 ? ulong.MaxValue : (ulong)random.NextInt64()) : 0,
            (mask & 32) != 0 ? RandomInt(random) : null);
    }

    public static GeometryFracturePiece[] RandomPieces(Random random, int count)
    {
        GeometryFracturePiece[] pieces = new GeometryFracturePiece[count];
        for (int i = 0; i < count; i++)
        {
            pieces[i] = new GeometryFracturePiece(RandomVector(random), RandomFloat(random));
        }

        return pieces;
    }

    public static DamageEventDelta[] RandomDamageEvents(Random random, int count)
    {
        DamageEventDelta[] events = new DamageEventDelta[count];
        for (int i = 0; i < count; i++)
        {
            events[i] = new DamageEventDelta(
                RandomInt(random),
                (CreatureKind)random.Next(-1, 4),
                RandomInt(random),
                random.Next(2) == 0,
                RandomInt(random),
                RandomInt(random));
        }

        return events;
    }

    // Edge floats and ints for every field, and kinds outside the enum, which the wire keeps as sent.
    public static CastEventDelta[] RandomCastEvents(Random random, int count)
    {
        CastEventDelta[] events = new CastEventDelta[count];
        for (int i = 0; i < count; i++)
        {
            events[i] = new CastEventDelta(
                RandomInt(random),
                (SkillKind)random.Next(-1, 4),
                RandomVector(random),
                RandomVector(random),
                RandomFloat(random));
        }

        return events;
    }

    // Protocol v3 encodes worlds in a compact integer form (WorldSnapshotJsonConverter), so the
    // random values are mapped onto what a world can hold: integer cells, centers and rubble
    // within reach of their cell, valid piece sizes. The random stream is consumed exactly as
    // before, so every sample after a world is unchanged.
    public static WorldSnapshot RandomWorld(Random random)
    {
        int width = random.Next(1, 64);
        int height = random.Next(1, 32);
        int depth = random.Next(1, 64);
        Vector3[] placedCells = ToCells(RandomVectors(random, random.Next(0, 6)));
        Vector3[]? fracturedCells = random.Next(2) == 0 ? null : ToCells(RandomVectors(random, random.Next(0, 4)));
        Vector3[]? subcells = random.Next(2) == 0 ? null : ToCells(RandomVectors(random, random.Next(0, 4)));
        PlacedCubeSnapshot[]? placed = null;
        if (random.Next(2) != 0)
        {
            Vector3 cell = ToCell(RandomVector(random));
            Vector3 center = cell + new Vector3(0.5f) + ToOffset(RandomVector(random));
            placed = [new PlacedCubeSnapshot(cell, center, (GeometryMaterialId)random.Next(0, 8), random.Next(2) == 0 ? null : random.Next())];
        }

        FracturedCubeSnapshot[]? fractured = null;
        if (random.Next(2) != 0)
        {
            Vector3 cell = ToCell(RandomVector(random));
            GeometryFracturePiece[] pieces = RandomPieces(random, random.Next(0, 4));
            for (int i = 0; i < pieces.Length; i++)
            {
                pieces[i] = new GeometryFracturePiece(
                    cell + new Vector3(0.5f) + ToOffset(pieces[i].Center),
                    0.05f + (Fraction(pieces[i].SideLength) * 0.9f));
            }

            fractured = [new FracturedCubeSnapshot(cell, GeometryMaterialId.Stone, pieces)];
        }

        ulong? sequence = random.Next(2) == 0 ? null : (ulong)random.NextInt64();
        // One consistent world: the cell lists are the cubes' and the rubble's cells.
        return new WorldSnapshot(
            width,
            height,
            depth,
            placed == null ? placedCells : [.. placed.Select(cube => cube.CellPosition)],
            fractured == null ? fracturedCells : [.. fractured.Select(rubble => rubble.CellPosition)],
            subcells,
            placed,
            fractured,
            sequence);
    }

    // A whole cell in [-1000, 1000] on each axis (non-finite values land on 0).
    private static Vector3 ToCell(Vector3 value)
    {
        return new Vector3(MathF.Floor(Clamp(value.X)), MathF.Floor(Clamp(value.Y)), MathF.Floor(Clamp(value.Z)));
    }

    private static Vector3[] ToCells(Vector3[] values)
    {
        return Array.ConvertAll(values, ToCell);
    }

    // An offset inside the cell, on the 1/4096 m grid the compact world quantizes to.
    private static Vector3 ToOffset(Vector3 value)
    {
        return new Vector3(
            MathF.Round((Fraction(value.X) - 0.5f) * 4096f) / 4096f,
            MathF.Round((Fraction(value.Y) - 0.5f) * 4096f) / 4096f,
            MathF.Round((Fraction(value.Z) - 0.5f) * 4096f) / 4096f);
    }

    private static float Clamp(float value)
    {
        return float.IsFinite(value) ? Math.Clamp(value, -1000f, 1000f) : 0f;
    }

    // A value in [0, 1) derived from any float.
    private static float Fraction(float value)
    {
        if (!float.IsFinite(value))
        {
            return 0f;
        }

        double fraction = Math.Abs((double)value) % 1d;
        return (float)Math.Min(fraction, 0.999d);
    }

    public static ActorSnapshot[] RandomActors(Random random, int count, bool roundTrippable = false)
    {
        ActorSnapshot[] actors = new ActorSnapshot[count];
        for (int i = 0; i < count; i++)
        {
            int mask = random.Next(64);
            actors[i] = new ActorSnapshot(
                RandomInt(random),
                (CreatureKind)random.Next(0, 4),
                (mask & 1) != 0 ? RandomString(random, roundTrippable) : null,
                RandomVector(random),
                RandomVector(random),
                RandomInt(random),
                RandomInt(random),
                random.Next(2) == 0,
                (mask & 2) != 0 ? (PlayerInteractionMode)random.Next(0, 3) : null,
                (mask & 4) != 0 ? new PlayerCombatStatsSnapshot(RandomInt(random), RandomFloat(random), RandomInt(random), RandomInt(random), RandomInt(random)) : null,
                (mask & 8) != 0 ? RandomString(random, roundTrippable) : null,
                (mask & 16) != 0 ? new PlayerProgressionSnapshot(RandomInt(random), RandomInt(random), RandomInt(random)) : null,
                (mask & 32) != 0 ? (EnemyBehaviorState)random.Next(0, 5) : null,
                // From the mask, so the random stream (and every sample after it) is unchanged.
                (mask & 3) == 3 ? mask >> 2 : 0);
        }

        return actors;
    }

    // The 24-actor mix of HandWrittenConverterTests and TransportJsonCodecDecodeTests: every third
    // actor is a player with every optional property, the rest are wolves; one placed cube and
    // one damage event.
    public static ServerSnapshotEnvelope TwentyFourActorSnapshot()
    {
        ActorSnapshot[] actors = new ActorSnapshot[24];
        Dictionary<string, ulong> sequences = new();
        for (int index = 0; index < actors.Length; index++)
        {
            bool isPlayer = index % 3 == 0;
            actors[index] = new ActorSnapshot(
                isPlayer ? 100 + index : 2000 + index,
                isPlayer ? CreatureKind.Player : CreatureKind.Enemy,
                isPlayer ? $"session-{index}" : null,
                new Vector3(index * 1.25f, 1.5f, -index * 0.75f),
                -Vector3.UnitZ,
                90 - index,
                100,
                true,
                isPlayer ? PlayerInteractionMode.Combat : null,
                isPlayer ? new PlayerCombatStatsSnapshot(16, 3f, 11, 7, 9) : null,
                isPlayer ? $"Player {index}" : "Wolf",
                isPlayer ? new PlayerProgressionSnapshot(2, 14, 1023) : null,
                isPlayer ? null : EnemyBehaviorState.Attacking);
            if (actors[index].SessionId is string sessionId)
            {
                sequences[sessionId] = (ulong)(100 + index);
            }
        }

        return new ServerSnapshotEnvelope(
            4242,
            sequences,
            actors,
            PlayerInteractionMode.Combat,
            null,
            [new WorldMutationDelta(WorldMutationKind.CubePlaced, new Vector3(1f, 2f, 3f), Sequence: 17, ActorId: 101)],
            [new DamageEventDelta(2000, CreatureKind.Enemy, 5, false, 35, 40)],
            WorldChecksum: 0x1234UL,
            WorldChecksumSequence: 17);
    }

    public static string RandomString(Random random, bool roundTrippable)
    {
        int pick = random.Next(EdgeStrings.Length + 1);
        if (pick == EdgeStrings.Length || (roundTrippable && EdgeStrings[pick].Contains((char)0xD800)))
        {
            return $"player-{random.Next()}";
        }

        return EdgeStrings[pick];
    }

    public static int RandomInt(Random random)
    {
        return random.Next(5) == 0 ? (random.Next(2) == 0 ? int.MinValue : int.MaxValue) : random.Next(-1000, 1000);
    }

    public static float RandomFloat(Random random)
    {
        return random.Next(3) == 0
            ? EdgeFloats[random.Next(EdgeFloats.Length)]
            : (float)((random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-8, 9)));
    }

    public static Vector3 RandomVector(Random random)
    {
        return new Vector3(RandomFloat(random), RandomFloat(random), RandomFloat(random));
    }

    private static Vector3[] RandomVectors(Random random, int count)
    {
        Vector3[] vectors = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            vectors[i] = RandomVector(random);
        }

        return vectors;
    }
}
