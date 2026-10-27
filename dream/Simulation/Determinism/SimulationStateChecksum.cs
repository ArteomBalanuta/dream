using OpenTK.Mathematics;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Determinism;

// Order-independent fingerprint of the authoritative simulation state, for detecting divergence
// between two runs (two servers with the same seed and input, or a replay against a recording).
// It covers more than WorldStateChecksum, which is replicated to clients and fingerprints only
// cell occupancy and material:
//   - WorldStateChecksum.Compute(map);
//   - every actor: id, kind, session, position and facing quantized to PositionQuantum, health,
//     max health, alive flag, and level and experience when the actor has progression;
//   - every valid rubble piece: cell, piece index, center quantized to PositionQuantum, exact
//     side length and material;
//   - the last world mutation sequence.
// Quantizing positions keeps the checksum stable against float noise below 1e-4 that would not
// change gameplay, while any real movement changes it.
//
// Order independence: each actor and each rubble piece is hashed on its own (its key, the actor
// id or the cell and piece index, is part of its hash) and the element hashes are added, the
// same scheme WorldStateChecksum uses. Addition is commutative, so actor and rubble enumeration
// order (List, Dictionary) never matters, and no sorting or scratch buffer is needed.
//
// Cost: a diagnostic, not a per-frame path. It is O(cells + actors + rubble pieces).
// WorldStateChecksum.Compute and the rubble dictionary enumeration through
// IReadOnlyDictionary allocate a few enumerator objects per call; actor hashing allocates
// nothing. Compute(WorldSessionRuntime) additionally allocates the runtime's actor snapshot
// list (CaptureActorSnapshots). Call it per tick in tests and tools, or at a low rate in
// production.
public static class SimulationStateChecksum
{
    // Positions, facings and rubble centers are rounded to multiples of this before hashing.
    public const float PositionQuantum = 1e-4f;

    private const double InversePositionQuantum = 10000.0;

    // The checksum of one state, split by component so a divergence can be attributed.
    public readonly record struct Parts(ulong World, ulong Actors, ulong Rubble, ulong WorldMutationSequence)
    {
        public ulong Combined => Seed.Combine(World, Actors, Rubble, WorldMutationSequence);
    }

    public static ulong Compute(WorldSessionRuntime runtime)
    {
        return ComputeParts(runtime).Combined;
    }

    public static ulong Compute(Map map, IReadOnlyList<ActorSnapshot> actors, ulong lastWorldMutationSequence)
    {
        return ComputeParts(map, actors, lastWorldMutationSequence).Combined;
    }

    public static Parts ComputeParts(WorldSessionRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return ComputeParts(runtime.Map, runtime.CaptureActorSnapshots(), runtime.LastWorldMutationSequence);
    }

    public static Parts ComputeParts(Map map, IReadOnlyList<ActorSnapshot> actors, ulong lastWorldMutationSequence)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(actors);

        ulong actorSum = 0;
        for (int index = 0; index < actors.Count; index++)
        {
            actorSum = unchecked(actorSum + HashActor(actors[index]));
        }

        ulong rubbleSum = 0;
        ulong rubbleCount = 0;
        foreach (GeometryCellRecord record in map.RubbleCells.Values)
        {
            IReadOnlyList<GeometryFracturePiece>? pieces = record.FracturePieces;
            if (pieces == null)
            {
                continue;
            }

            ulong material = (ulong)record.Definition.MaterialId;
            for (int index = 0; index < pieces.Count; index++)
            {
                GeometryFracturePiece piece = pieces[index];
                if (!piece.IsValid())
                {
                    continue;
                }

                rubbleSum = unchecked(rubbleSum + HashFracturePiece(record.Key, index, piece, material));
                rubbleCount++;
            }
        }

        return new Parts(
            WorldStateChecksum.Compute(map),
            Seed.Combine(actorSum, (ulong)actors.Count),
            Seed.Combine(rubbleSum, rubbleCount),
            lastWorldMutationSequence);
    }

    // The hash of one actor. Exposed so a divergence report can name the actors that differ.
    public static ulong HashActor(in ActorSnapshot actor)
    {
        ulong identity = Seed.Combine(
            Seed.FromInt(actor.ActorId),
            (ulong)actor.Kind,
            actor.SessionId == null ? 0UL : StableHash.Of(actor.SessionId));
        ulong position = Seed.Combine(identity, Quantize(actor.Position.X), Quantize(actor.Position.Y), Quantize(actor.Position.Z));
        ulong facing = Seed.Combine(position, Quantize(actor.Facing.X), Quantize(actor.Facing.Y), Quantize(actor.Facing.Z));
        ulong health = Seed.Combine(
            facing,
            Seed.FromInt(actor.CurrentHealth),
            Seed.FromInt(actor.MaxHealth),
            actor.IsAlive ? 1UL : 0UL);
        return actor.Progression is { } progression
            ? Seed.Combine(health, 1UL, Seed.FromInt(progression.Level), Seed.FromInt(progression.Experience))
            : Seed.Combine(health, 0UL);
    }

    private static ulong HashFracturePiece(GeometryCellKey cell, int pieceIndex, GeometryFracturePiece piece, ulong material)
    {
        ulong key = Seed.Combine(Seed.FromCell(cell.X, cell.Y, cell.Z), Seed.FromInt(pieceIndex));
        ulong center = Seed.Combine(key, Quantize(piece.Center.X), Quantize(piece.Center.Y), Quantize(piece.Center.Z));
        return Seed.Combine(center, BitConverter.SingleToUInt32Bits(piece.SideLength), material);
    }

    // Rounds to the nearest multiple of PositionQuantum (ties to even). Non-finite values hash by
    // their bits, tagged so they never collide with a finite quantized value.
    private static ulong Quantize(float value)
    {
        if (!float.IsFinite(value))
        {
            return (1UL << 63) | BitConverter.SingleToUInt32Bits(value);
        }

        return unchecked((ulong)(long)Math.Round(value * InversePositionQuantum));
    }
}
