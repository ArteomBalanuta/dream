using OpenTK.Mathematics;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;

namespace Dream.Simulation.World;

// SCALE-2 delta actor snapshots. In memory every ServerSnapshotEnvelope still carries full
// ActorSnapshot records; only the wire is delta coded. Three types take part:
//
// - ActorDeltaContext (server): attached by WorldSessionRuntime to each broadcast export, inline
//   (a struct). It names the export's frame sequence and, for a delta frame, the previous
//   export's actors (the base). The encoder writes one ActorWireRecord per actor from it, so the
//   base is never copied and building a frame allocates nothing.
// - ActorWireFrame (decoded): the records exactly as they came off the wire. The client's
//   ActorStateCache folds them into full actors; re-encoding a decoded snapshot writes them back
//   byte for byte.
// - ActorWireRecord: one actor's record, a change mask over ActorFieldGroups plus the groups it
//   names. A Full record is absolute; any other record holds the changes against the base.
//
// Frames chain by sequence, not by tick: a delta frame names BaseSequence, the frame its records
// were diffed against, and only applies on top of exactly that frame. Snapshots outside the chain
// (a world resync, a test-built envelope, JoinAccepted's initial actors) carry no frame and
// encode the legacy full actor list.
[Flags]
public enum ActorFieldGroups
{
    None = 0,
    // Identity (kind, session id, display name). A record with Full is absolute and carries
    // every group; the encoder sends one for an actor that is new against its base, whose
    // identity changed, or in a keyframe.
    Full = 1,
    // Quantized position (ActorQuantization), as a difference from the base unless Full.
    Position = 2,
    // Quantized facing vector, as a difference from the base unless Full.
    Facing = 4,
    // Current health, max health, alive.
    Health = 8,
    // Interaction mode (players) and enemy behaviour state.
    Mode = 16,
    // Player combat stats.
    Combat = 32,
    // Player level, experience and experience to the next level.
    Progression = 64,
    // StarvedTicks (W9-2).
    Starved = 128,
    // Every group a Full record always carries. Equipment is not part of it.
    All = Full | Position | Facing | Health | Mode | Combat | Progression | Starved,
    // HeldLight (T2f, protocol v6). Not implied by Full: a delta record sets it when the held
    // light differs from the base, a Full record only when the held light is not None, so a world
    // with no light equipped keeps its v5 bytes. A Full record without it means None.
    Equipment = 256,
    // EnemyLevel and VisualKey (wave I, protocol v11). Not implied by Full: a Full record sets it
    // only when the level is not 0 or the key is not null, a delta record when either differs from
    // the base. A Full record without it means level 1 and the default visual.
    EnemyPresentation = 512,
    // Every bit a record may carry.
    Valid = All | Equipment | EnemyPresentation
}

// Transforms are quantized at 1/4096 m, the step the compact world format already uses for moved
// rubble (WorldSnapshotJsonConverter), so actors and cubes agree on one position grid. Facing
// vectors use the same step per component, about 0.014 degrees on a unit vector. Values past
// the int range (over 500 km from the origin) clamp; a non-finite component encodes as 0.
public static class ActorQuantization
{
    public const float UnitsPerMeter = 4096f;

    // Half a step: the most a reconstituted transform component differs from the server's. The
    // bound holds while |value| * UnitsPerMeter < 2^24 (|value| under about 4096 m), where a float
    // still holds every quantization step exactly; farther out the float spacing of the value
    // itself exceeds a step and the error grows with it.
    public const float MaxError = 0.5f / UnitsPerMeter;

    public static int Quantize(float value)
    {
        if (!float.IsFinite(value))
        {
            return 0;
        }

        float scaled = MathF.Round(value * UnitsPerMeter);
        if (scaled >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return scaled <= int.MinValue ? int.MinValue : (int)scaled;
    }

    public static float Dequantize(int value)
    {
        return value / UnitsPerMeter;
    }

    public static Vector3 Dequantize(int x, int y, int z)
    {
        return new Vector3(Dequantize(x), Dequantize(y), Dequantize(z));
    }

    // The value a client reconstitutes for v.
    public static Vector3 Round(Vector3 v)
    {
        return Dequantize(Quantize(v.X), Quantize(v.Y), Quantize(v.Z));
    }
}

// One actor on the wire. For a Full record the transform fields are absolute quantized values;
// otherwise they hold the change against the base actor and only the groups in Groups are
// meaningful. Groups == None is an actor unchanged since the base (written as its bare id).
public readonly record struct ActorWireRecord(
    int ActorId,
    ActorFieldGroups Groups,
    CreatureKind Kind = default,
    string? SessionId = null,
    string? DisplayName = null,
    int PositionX = 0,
    int PositionY = 0,
    int PositionZ = 0,
    int FacingX = 0,
    int FacingY = 0,
    int FacingZ = 0,
    int CurrentHealth = 0,
    int MaxHealth = 0,
    bool IsAlive = false,
    PlayerInteractionMode? InteractionMode = null,
    EnemyBehaviorState? EnemyBehaviorState = null,
    PlayerCombatStatsSnapshot? CombatStats = null,
    PlayerProgressionSnapshot? Progression = null,
    int StarvedTicks = 0,
    HeldLightKind HeldLight = HeldLightKind.None,
    int EnemyLevel = 0,
    string? VisualKey = null)
{
    public bool IsFull => (Groups & ActorFieldGroups.Full) != 0;

    // The actor a Full record describes, transforms at their quantized values.
    public ActorSnapshot ToActorSnapshot()
    {
        if (!IsFull)
        {
            throw new InvalidOperationException($"Actor {ActorId}'s record is a delta; it needs its base actor.");
        }

        return new ActorSnapshot(
            ActorId,
            Kind,
            SessionId,
            ActorQuantization.Dequantize(PositionX, PositionY, PositionZ),
            ActorQuantization.Dequantize(FacingX, FacingY, FacingZ),
            CurrentHealth,
            MaxHealth,
            IsAlive,
            InteractionMode,
            CombatStats,
            DisplayName,
            Progression,
            EnemyBehaviorState,
            StarvedTicks,
            HeldLight,
            0UL,
            EnemyLevel,
            VisualKey);
    }

    public static ActorWireRecord Full(in ActorSnapshot actor)
    {
        ActorFieldGroups fullGroups = ActorFieldGroups.All;
        if (actor.HeldLight != HeldLightKind.None)
        {
            fullGroups |= ActorFieldGroups.Equipment;
        }

        if (actor.EnemyLevel != 0 || actor.VisualKey is not null)
        {
            fullGroups |= ActorFieldGroups.EnemyPresentation;
        }

        return new ActorWireRecord(
            actor.ActorId,
            fullGroups,
            actor.Kind,
            actor.SessionId,
            actor.DisplayName,
            ActorQuantization.Quantize(actor.Position.X),
            ActorQuantization.Quantize(actor.Position.Y),
            ActorQuantization.Quantize(actor.Position.Z),
            ActorQuantization.Quantize(actor.Facing.X),
            ActorQuantization.Quantize(actor.Facing.Y),
            ActorQuantization.Quantize(actor.Facing.Z),
            actor.CurrentHealth,
            actor.MaxHealth,
            actor.IsAlive,
            actor.InteractionMode,
            actor.EnemyBehaviorState,
            actor.CombatStats,
            actor.Progression,
            actor.StarvedTicks,
            actor.HeldLight,
            actor.EnemyLevel,
            actor.VisualKey);
    }

    // The record that turns baseActor (as the client holds it) into actor. Transforms compare by
    // their quantized values, so motion below one step costs nothing on the wire.
    public static ActorWireRecord Diff(in ActorSnapshot actor, in ActorSnapshot baseActor)
    {
        if (actor.Kind != baseActor.Kind
            || !string.Equals(actor.SessionId, baseActor.SessionId, StringComparison.Ordinal)
            || !string.Equals(actor.DisplayName, baseActor.DisplayName, StringComparison.Ordinal))
        {
            return Full(actor);
        }

        ActorFieldGroups groups = ActorFieldGroups.None;
        int px = ActorQuantization.Quantize(actor.Position.X) - ActorQuantization.Quantize(baseActor.Position.X);
        int py = ActorQuantization.Quantize(actor.Position.Y) - ActorQuantization.Quantize(baseActor.Position.Y);
        int pz = ActorQuantization.Quantize(actor.Position.Z) - ActorQuantization.Quantize(baseActor.Position.Z);
        if ((px | py | pz) != 0)
        {
            groups |= ActorFieldGroups.Position;
        }

        int fx = ActorQuantization.Quantize(actor.Facing.X) - ActorQuantization.Quantize(baseActor.Facing.X);
        int fy = ActorQuantization.Quantize(actor.Facing.Y) - ActorQuantization.Quantize(baseActor.Facing.Y);
        int fz = ActorQuantization.Quantize(actor.Facing.Z) - ActorQuantization.Quantize(baseActor.Facing.Z);
        if ((fx | fy | fz) != 0)
        {
            groups |= ActorFieldGroups.Facing;
        }

        if (actor.CurrentHealth != baseActor.CurrentHealth
            || actor.MaxHealth != baseActor.MaxHealth
            || actor.IsAlive != baseActor.IsAlive)
        {
            groups |= ActorFieldGroups.Health;
        }

        if (actor.InteractionMode != baseActor.InteractionMode
            || actor.EnemyBehaviorState != baseActor.EnemyBehaviorState)
        {
            groups |= ActorFieldGroups.Mode;
        }

        if (actor.CombatStats != baseActor.CombatStats)
        {
            groups |= ActorFieldGroups.Combat;
        }

        if (actor.Progression != baseActor.Progression)
        {
            groups |= ActorFieldGroups.Progression;
        }

        if (actor.StarvedTicks != baseActor.StarvedTicks)
        {
            groups |= ActorFieldGroups.Starved;
        }

        if (actor.HeldLight != baseActor.HeldLight)
        {
            groups |= ActorFieldGroups.Equipment;
        }

        if (actor.EnemyLevel != baseActor.EnemyLevel
            || !string.Equals(actor.VisualKey, baseActor.VisualKey, StringComparison.Ordinal))
        {
            groups |= ActorFieldGroups.EnemyPresentation;
        }

        return new ActorWireRecord(
            actor.ActorId,
            groups,
            PositionX: px,
            PositionY: py,
            PositionZ: pz,
            FacingX: fx,
            FacingY: fy,
            FacingZ: fz,
            CurrentHealth: actor.CurrentHealth,
            MaxHealth: actor.MaxHealth,
            IsAlive: actor.IsAlive,
            InteractionMode: actor.InteractionMode,
            EnemyBehaviorState: actor.EnemyBehaviorState,
            CombatStats: actor.CombatStats,
            Progression: actor.Progression,
            StarvedTicks: actor.StarvedTicks,
            HeldLight: actor.HeldLight,
            EnemyLevel: actor.EnemyLevel,
            VisualKey: actor.VisualKey);
    }
}

// Server side: how one broadcast export's actors are delta coded. A value carried inline in the
// envelope, so building a frame allocates nothing: it names the frame and, for a delta, the
// previous export's actor list (the base), which stays immutable once exported. Encoders on
// writer threads only read it. A keyframe has no base.
public readonly record struct ActorDeltaContext
{
    // A full keyframe at least this often (server ticks), so a client that somehow lost the chain
    // recovers within about a second even without the server noticing.
    public const ulong KeyframeIntervalTicks = 64;

    private ActorDeltaContext(ulong sequence, ulong baseSequence, IReadOnlyList<ActorSnapshot>? baseActors)
    {
        Sequence = sequence;
        BaseSequence = baseSequence;
        BaseActors = baseActors;
    }

    // This frame's place in the chain (never 0 for a built context).
    public ulong Sequence { get; }

    // The frame the delta records are diffed against; 0 for a keyframe.
    public ulong BaseSequence { get; }

    // The base frame's actors; null for a keyframe.
    public IReadOnlyList<ActorSnapshot>? BaseActors { get; }

    public bool IsKeyframe => BaseActors == null;

    // The same frame as a keyframe: what a connection gets when it does not hold BaseSequence.
    public ActorDeltaContext Keyframe => new(Sequence, 0, null);

    public static ActorDeltaContext CreateKeyframe(ulong sequence)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sequence);
        return new ActorDeltaContext(sequence, 0, null);
    }

    public static ActorDeltaContext CreateDelta(ulong sequence, ulong baseSequence, IReadOnlyList<ActorSnapshot> baseActors)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sequence);
        ArgumentOutOfRangeException.ThrowIfZero(baseSequence);
        ArgumentNullException.ThrowIfNull(baseActors);
        return new ActorDeltaContext(sequence, baseSequence, baseActors);
    }

    // The record for actors[index], the envelope's list this context was built for. Call it for
    // index 0, 1, 2, ... with one cursor (starting at 0): the runtime keeps actors in a stable order
    // (players, then enemies), so each base actor is normally found right at the cursor, and the
    // whole frame costs O(actors) with nothing allocated. An actor not found from the cursor on is
    // looked up from the start (moved in the order); one not in the base at all is new and goes
    // out whole.
    public ActorWireRecord BuildRecord(IReadOnlyList<ActorSnapshot> actors, int index, ref int cursor)
    {
        ActorSnapshot actor = actors[index];
        if (BaseActors is not IReadOnlyList<ActorSnapshot> baseActors)
        {
            return ActorWireRecord.Full(actor);
        }

        int count = baseActors.Count;
        for (int j = cursor; j < count; j++)
        {
            if (baseActors[j].ActorId == actor.ActorId)
            {
                cursor = j + 1;
                return ActorWireRecord.Diff(actor, baseActors[j]);
            }
        }

        for (int j = 0; j < cursor && j < count; j++)
        {
            if (baseActors[j].ActorId == actor.ActorId)
            {
                return ActorWireRecord.Diff(actor, baseActors[j]);
            }
        }

        return ActorWireRecord.Full(actor);
    }
}

// The server's frame chain: every Next is the next frame, diffed against the previous call's
// actors (the previous broadcast), with a keyframe every KeyframeIntervalTicks server ticks.
// The chain is shared by every connection; a connection that does not hold a frame's base gets
// the frame's keyframe twin instead (ServerConnection), so the payload is still encoded once.
// Tick thread only. Allocates nothing: the export's own actor list is kept as the next base.
public sealed class ActorDeltaChain
{
    private ulong _sequence;
    private IReadOnlyList<ActorSnapshot>? _base;
    private ulong _lastKeyframeTick;

    public ulong Sequence => _sequence;

    public ActorDeltaContext Next(ulong serverTick, IReadOnlyList<ActorSnapshot> actors)
    {
        ArgumentNullException.ThrowIfNull(actors);
        ulong sequence = ++_sequence;
        IReadOnlyList<ActorSnapshot>? baseActors = _base;
        _base = actors;
        bool keyframeDue = baseActors == null
            || serverTick < _lastKeyframeTick
            || serverTick - _lastKeyframeTick >= ActorDeltaContext.KeyframeIntervalTicks;
        if (keyframeDue)
        {
            _lastKeyframeTick = serverTick;
            return ActorDeltaContext.CreateKeyframe(sequence);
        }

        return ActorDeltaContext.CreateDelta(sequence, sequence - 1, baseActors!);
    }
}

// Decoded: one snapshot's actor records as they were on the wire. BaseSequence is 0 for a
// keyframe.
public sealed class ActorWireFrame
{
    public ActorWireFrame(ulong sequence, ulong baseSequence, ActorWireRecord[] records)
    {
        ArgumentNullException.ThrowIfNull(records);
        Sequence = sequence;
        BaseSequence = baseSequence;
        Records = records;
    }

    public ulong Sequence { get; }

    public ulong BaseSequence { get; }

    public bool IsKeyframe => BaseSequence == 0;

    public ActorWireRecord[] Records { get; }
}
