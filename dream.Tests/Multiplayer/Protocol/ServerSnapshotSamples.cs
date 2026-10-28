using OpenTK.Mathematics;
using Dream.Simulation.Combat;
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

    public static WorldSnapshot RandomWorld(Random random)
    {
        return new WorldSnapshot(
            random.Next(1, 64),
            random.Next(1, 32),
            random.Next(1, 64),
            RandomVectors(random, random.Next(0, 6)),
            random.Next(2) == 0 ? null : RandomVectors(random, random.Next(0, 4)),
            random.Next(2) == 0 ? null : RandomVectors(random, random.Next(0, 4)),
            random.Next(2) == 0
                ? null
                : [new PlacedCubeSnapshot(RandomVector(random), RandomVector(random), (GeometryMaterialId)random.Next(0, 8), random.Next(2) == 0 ? null : random.Next())],
            random.Next(2) == 0
                ? null
                : [new FracturedCubeSnapshot(RandomVector(random), GeometryMaterialId.Stone, RandomPieces(random, random.Next(0, 4)))],
            random.Next(2) == 0 ? null : (ulong)random.NextInt64());
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
                (mask & 4) != 0 ? new PlayerCombatStatsSnapshot(RandomInt(random), RandomFloat(random), RandomInt(random)) : null,
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
                isPlayer ? new PlayerCombatStatsSnapshot(16, 3f, 11) : null,
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
