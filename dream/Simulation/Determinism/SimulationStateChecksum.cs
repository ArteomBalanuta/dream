using Dream.Simulation.Combat;
using OpenTK.Mathematics;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Determinism;

// Order-independent fingerprint of the authoritative simulation state, for detecting divergence
// between two runs (two servers with the same seed and input, or a replay against a recording).
// It covers more than WorldStateChecksum, which is replicated to clients and fingerprints only
// cell occupancy, kind and material. The parts are:
//   - World: WorldStateChecksum.Compute(map);
//   - Actors: every actor's id, kind, session, position and facing quantized to PositionQuantum,
//     current health, max health, alive flag, and, each hashed as absent or present plus value,
//     interaction mode, combat stats (strength, exact attack speed bits, vitality), progression
//     (level, experience) and enemy behavior state, plus the actor count. DisplayName is not
//     covered;
//   - Rubble: every valid rubble piece's cell, piece index, center quantized to PositionQuantum,
//     exact side length and material, plus the piece count;
//   - Cubes: every solid cube in Map.SolidCubes, keyed by its replicated id when it has one
//     (Map.TryGetReplicatedId) and by its cell otherwise, with its position quantized to
//     PositionQuantum, plus the cube count. This catches a physics-driven cube that moves
//     within its cell, and two servers that gave the same cube different replicated ids, which
//     WorldStateChecksum cannot see. The id and cell keys use distinct salts so a collision is
//     improbable (they are 64-bit mixes, not injective); a cell holds at most one cube and an id maps to one cube, so each key is unique;
//   - WorldMutationSequence: the last world mutation sequence.
// Quantizing positions absorbs most float noise below PositionQuantum, so tiny differences that
// would not change gameplay usually leave the checksum unchanged. It is not a guarantee: two
// values that straddle a rounding boundary land in adjacent buckets and hash differently however
// close they are. Any movement larger than the quantum always changes the checksum.
//
// Order independence: each actor, rubble piece and cube is hashed on its own (its key, the actor
// id, the cell and piece index, or the cube id or cell, is part of its hash) and the element hashes
// are added, the same scheme WorldStateChecksum uses. Addition is commutative, so enumeration
// order (List, Dictionary) never matters, and no sorting or scratch buffer is needed.
//
// Cost: a diagnostic, not a per-frame path. It is O(cells + actors + rubble pieces + cubes).
// WorldStateChecksum.Compute is O(1), and the rubble walk (Map.SumRubblePieceHashes) goes
// through the map's own dictionary with its struct enumerator, so the world and rubble parts,
// like actor and cube hashing, allocate nothing. Compute(WorldSessionRuntime) additionally allocates the runtime's actor
// snapshot list (CaptureActorSnapshots). Call it per tick in tests and tools, or at a low rate
// in production.
public static class SimulationStateChecksum
{
    // Positions, facings and rubble centers are rounded to multiples of this before hashing.
    public const float PositionQuantum = 1e-4f;

    private const double InversePositionQuantum = 10000.0;

    // The checksum of one state, split by component so a divergence can be attributed.
    public readonly record struct Parts(ulong World, ulong Actors, ulong Rubble, ulong Cubes, ulong WorldMutationSequence)
    {
        public ulong Combined => Seed.Combine(World, Actors, Rubble, Cubes, WorldMutationSequence);
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

        map.SumRubblePieceHashes(out ulong rubbleSum, out ulong rubbleCount);

        ulong cubeSum = 0;
        IReadOnlyList<Cube> cubes = map.SolidCubes;
        for (int index = 0; index < cubes.Count; index++)
        {
            cubeSum = unchecked(cubeSum + HashCube(map, cubes[index]));
        }

        return new Parts(
            WorldStateChecksum.Compute(map),
            Seed.Combine(actorSum, (ulong)actors.Count),
            Seed.Combine(rubbleSum, rubbleCount),
            Seed.Combine(cubeSum, (ulong)cubes.Count),
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
        ulong progressionHash = actor.Progression is { } progression
            ? Seed.Combine(health, 1UL, Seed.FromInt(progression.Level), Seed.FromInt(progression.Experience))
            : Seed.Combine(health, 0UL);
        ulong mode = actor.InteractionMode is { } interactionMode
            ? Seed.Combine(progressionHash, 1UL, (ulong)interactionMode)
            : Seed.Combine(progressionHash, 0UL);
        ulong combat = actor.CombatStats is { } stats
            ? Seed.Combine(
                Seed.Combine(mode, 1UL),
                Seed.FromInt(stats.Strength),
                BitConverter.SingleToUInt32Bits(stats.AttackSpeed),
                Seed.FromInt(stats.Vitality))
            : Seed.Combine(mode, 0UL);
        // Agility and Energy (protocol 19) only when either is set, so a stats block without them hashes as
        // it did before.
        if (actor.CombatStats is { } extra && (extra.Agility | extra.Energy) != 0)
        {
            combat = Seed.Combine(
                combat,
                CombatAttributesSalt,
                Seed.Combine(Seed.FromInt(extra.Agility), Seed.FromInt(extra.Energy)));
        }

        ulong hash = actor.EnemyBehaviorState is { } behavior
            ? Seed.Combine(combat, 1UL, (ulong)behavior)
            : Seed.Combine(combat, 0UL);
        // Enemy level and visual only when non-default, so a wolf-only world hashes as it did before wave I.
        // A player's VisualKey (the worn weapon and shield) is derived from the inventory, like the held light, so it is excluded.
        string? presentationKey = actor.Kind == CreatureKind.Player ? null : actor.VisualKey;
        hash = (actor.EnemyLevel == 0 && presentationKey is null)
            ? hash
            : Seed.Combine(
                hash,
                EnemyPresentationSalt,
                Seed.FromInt(actor.EnemyLevel),
                presentationKey is null ? 0UL : StableHash.Of(presentationKey));
        // Rolled equipment only; zero (nothing rolled equipped) leaves the hash as it was before wave H.
        return actor.ModifierDigest == 0UL ? hash : Seed.Combine(hash, ModifierDigestSalt, actor.ModifierDigest);
    }

    private const ulong CombatAttributesSalt = 0x61747472696275UL; // "attribu"
    private const ulong EnemyPresentationSalt = 0x656E656D792D7072UL; // "enemy-pr"
    private const ulong ModifierDigestSalt = 0x6D6F642D64696765UL; // "mod-dige"

    // Key kinds are salted so a collision between a replicated-id key, a cell key and a position key is improbable (64-bit mixes, not injective).
    private const ulong CubeIdSalt = 0x637562652D696421UL; // "cube-id!"
    private const ulong CubeCellSalt = 0x637562652D63656CUL; // "cube-cel"
    private const ulong CubeUnindexedSalt = 0x637562652D6E6F78UL; // "cube-nox"

    // The hash of one solid cube: its replicated id when it has one, else its cell (from the map's
    // cube index), else its quantized position; plus its quantized position.
    private static ulong HashCube(Map map, Cube cube)
    {
        Vector3 position = cube.Position;
        ulong key;
        if (map.TryGetReplicatedId(cube, out int replicatedId))
        {
            key = Seed.Combine(CubeIdSalt, Seed.FromInt(replicatedId));
        }
        else if (map.TryGetCellForCube(cube, out GeometryCellKey cell))
        {
            key = Seed.Combine(CubeCellSalt, Seed.FromCell(cell.X, cell.Y, cell.Z));
        }
        else
        {
            key = Seed.Combine(CubeUnindexedSalt, Quantize(position.X), Quantize(position.Y), Quantize(position.Z));
        }

        return Seed.Combine(key, Quantize(position.X), Quantize(position.Y), Quantize(position.Z));
    }

    // Map.SumRubblePieceHashes walks the rubble with this.
    internal static ulong HashFracturePiece(GeometryCellKey cell, int pieceIndex, GeometryFracturePiece piece, ulong material)
    {
        ulong key = Seed.Combine(Seed.FromCell(cell.X, cell.Y, cell.Z), Seed.FromInt(pieceIndex));
        ulong center = Seed.Combine(key, Quantize(piece.Center.X), Quantize(piece.Center.Y), Quantize(piece.Center.Z));
        return Seed.Combine(center, BitConverter.SingleToUInt32Bits(piece.SideLength), material);
    }

    // Rounds to the nearest multiple of PositionQuantum (ties to even). Non-finite values hash by
    // their bits, salted and mixed so they land at pseudo-random 64-bit values rather than in the
    // range a finite quantized value occupies. A collision with a finite value is then only as
    // likely as any other 64-bit hash collision, not structural.
    private const ulong NonFiniteSalt = 0x6E6F6E66696E6974UL; // "nonfinit"

    private static ulong Quantize(float value)
    {
        if (!float.IsFinite(value))
        {
            return Seed.Mix(NonFiniteSalt ^ BitConverter.SingleToUInt32Bits(value));
        }

        return unchecked((ulong)(long)Math.Round(value * InversePositionQuantum));
    }
}
