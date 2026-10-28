using OpenTK.Mathematics;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.World;

namespace Dream.Client.Replication;

// SCALE-2: turns the delta-coded actor frames of received snapshots back into full actors, so
// everything downstream (the presenter and its interpolation, the prediction reconciler, the
// correction, enemy and damage appliers) keeps reading ServerSnapshotEnvelope.Actors as before.
// RemoteClientSession runs every decoded snapshot through it, in wire order, before handing it on.
//
// The cache keeps each actor's last full state plus its quantized transform, and the sequence of
// the last frame applied:
// - a keyframe replaces the whole cache;
// - a delta frame applies on top of exactly the frame it names as its base. The server only sends a
//   delta to a connection that holds that base (ServerConnection): usually the previous broadcast,
//   and for a merged snapshot the last frame it sent, so the base need not be Sequence - 1. A
//   mismatch means a frame was lost or a bug: every cached actor is then marked stale;
// - a stale actor (also one the cache never saw, named by a delta) is left out of the snapshot's
//   actors, never shown with old values as if they were current, until a full record for it arrives:
//   the next keyframe (at most ActorDeltaContext.KeyframeIntervalTicks away), or a full record the
//   server sends for a new actor;
// - a snapshot that carries the legacy full actor list (a resync, JoinAccepted-era state) is passed
//   on unchanged and clears the chain, since the server sends the next frame as a keyframe then too.
//
// Single-threaded: the session's receive loop is the only caller.
public sealed class ActorStateCache
{
    private Dictionary<int, Entry> _entries = [];
    private Dictionary<int, Entry> _next = [];
    private readonly List<ActorSnapshot> _output = [];

    // The last frame applied; 0 before the first frame and after a snapshot without one.
    public ulong FrameSequence { get; private set; }

    // Delta frames whose base was not the last frame applied.
    public long ChainBreaks { get; private set; }

    public long KeyframesApplied { get; private set; }

    public long DeltaFramesApplied { get; private set; }

    // Actors currently held back as stale.
    public int StaleActorCount { get; private set; }

    // Every actor left out of a snapshot because it was stale, summed over snapshots.
    public long StaleActorsWithheld { get; private set; }

    public int Count => _entries.Count;

    public bool TryGetActor(int actorId, out ActorSnapshot actor, out bool stale)
    {
        if (_entries.TryGetValue(actorId, out Entry entry))
        {
            actor = entry.Actor;
            stale = entry.Stale;
            return true;
        }

        actor = default;
        stale = false;
        return false;
    }

    public ServerSnapshotMessage Apply(ServerSnapshotMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ServerSnapshotEnvelope snapshot = message.Snapshot;
        if (snapshot.ActorFrame is not ActorWireFrame frame)
        {
            // Not on the chain. The full list is shown as it is; the next frame the server sends
            // this connection is a keyframe.
            _entries.Clear();
            FrameSequence = 0;
            StaleActorCount = 0;
            return message;
        }

        if (frame.IsKeyframe)
        {
            // The decoder already materialized a keyframe's actors; the output list is then not
            // filled at all, so it never holds actors past this call.
            bool materialized = snapshot.Actors != null;
            ApplyKeyframe(frame, fillOutput: !materialized);
            return materialized
                ? message
                : message with { Snapshot = snapshot with { Actors = ToArray() } };
        }

        ApplyDelta(frame);
        return message with { Snapshot = snapshot with { Actors = ToArray() } };
    }

    private void ApplyKeyframe(ActorWireFrame frame, bool fillOutput)
    {
        KeyframesApplied++;
        _next.Clear();
        _output.Clear();
        ActorWireRecord[] records = frame.Records;
        for (int i = 0; i < records.Length; i++)
        {
            Entry entry = Entry.FromFull(records[i]);
            _next[records[i].ActorId] = entry;
            if (fillOutput)
            {
                _output.Add(entry.Actor);
            }
        }

        Swap(frame.Sequence, stale: 0);
    }

    private void ApplyDelta(ActorWireFrame frame)
    {
        DeltaFramesApplied++;
        bool chainBroken = frame.BaseSequence != FrameSequence;
        if (chainBroken)
        {
            // Once per broken frame (never per actor): the server only sends a delta whose base
            // this cache holds, so this is a lost frame or a bug worth a line in the log.
            ChainBreaks++;
            NetworkDiagnosticsLog.Write(
                "client.actor-chain-break",
                $"frame={frame.Sequence} base={frame.BaseSequence} held={FrameSequence} records={frame.Records.Length}");
        }

        _next.Clear();
        _output.Clear();
        int stale = 0;
        ActorWireRecord[] records = frame.Records;
        for (int i = 0; i < records.Length; i++)
        {
            ActorWireRecord record = records[i];
            Entry entry;
            if (record.IsFull)
            {
                entry = Entry.FromFull(record);
            }
            else if (_entries.TryGetValue(record.ActorId, out Entry cached))
            {
                entry = cached;
                if (chainBroken)
                {
                    // The record is relative to a state this cache never had.
                    entry.Stale = true;
                }
                else if (!entry.Stale)
                {
                    entry.Apply(record);
                }
            }
            else
            {
                // A delta for an actor never seen whole: nothing to apply it to.
                entry = Entry.Unknown(record.ActorId);
            }

            _next[record.ActorId] = entry;
            if (entry.Stale)
            {
                stale++;
            }
            else
            {
                _output.Add(entry.Actor);
            }
        }

        StaleActorsWithheld += stale;
        Swap(frame.Sequence, stale);
    }

    private void Swap(ulong sequence, int stale)
    {
        (_entries, _next) = (_next, _entries);
        _next.Clear();
        FrameSequence = sequence;
        StaleActorCount = stale;
    }

    // The snapshot's actor list is a wire payload, fresh per snapshot like the decoder's.
    private ActorSnapshot[] ToArray()
    {
        ActorSnapshot[] actors = _output.Count == 0 ? [] : _output.ToArray();
        _output.Clear();
        return actors;
    }

    private struct Entry
    {
        public ActorSnapshot Actor;
        public int PositionX;
        public int PositionY;
        public int PositionZ;
        public int FacingX;
        public int FacingY;
        public int FacingZ;
        public bool Stale;

        public static Entry FromFull(in ActorWireRecord record)
        {
            return new Entry
            {
                Actor = record.ToActorSnapshot(),
                PositionX = record.PositionX,
                PositionY = record.PositionY,
                PositionZ = record.PositionZ,
                FacingX = record.FacingX,
                FacingY = record.FacingY,
                FacingZ = record.FacingZ
            };
        }

        public static Entry Unknown(int actorId)
        {
            return new Entry
            {
                Actor = new ActorSnapshot(actorId, default, null, default, default, 0, 0, false),
                Stale = true
            };
        }

        public void Apply(in ActorWireRecord record)
        {
            ActorFieldGroups groups = record.Groups;
            if (groups == ActorFieldGroups.None)
            {
                return;
            }

            ActorSnapshot actor = Actor;
            Vector3 position = actor.Position;
            if ((groups & ActorFieldGroups.Position) != 0)
            {
                PositionX += record.PositionX;
                PositionY += record.PositionY;
                PositionZ += record.PositionZ;
                position = ActorQuantization.Dequantize(PositionX, PositionY, PositionZ);
            }

            Vector3 facing = actor.Facing;
            if ((groups & ActorFieldGroups.Facing) != 0)
            {
                FacingX += record.FacingX;
                FacingY += record.FacingY;
                FacingZ += record.FacingZ;
                facing = ActorQuantization.Dequantize(FacingX, FacingY, FacingZ);
            }

            bool health = (groups & ActorFieldGroups.Health) != 0;
            bool mode = (groups & ActorFieldGroups.Mode) != 0;
            Actor = new ActorSnapshot(
                actor.ActorId,
                actor.Kind,
                actor.SessionId,
                position,
                facing,
                health ? record.CurrentHealth : actor.CurrentHealth,
                health ? record.MaxHealth : actor.MaxHealth,
                health ? record.IsAlive : actor.IsAlive,
                mode ? record.InteractionMode : actor.InteractionMode,
                (groups & ActorFieldGroups.Combat) != 0 ? record.CombatStats : actor.CombatStats,
                actor.DisplayName,
                (groups & ActorFieldGroups.Progression) != 0 ? record.Progression : actor.Progression,
                mode ? record.EnemyBehaviorState : actor.EnemyBehaviorState,
                (groups & ActorFieldGroups.Starved) != 0 ? record.StarvedTicks : actor.StarvedTicks,
                (groups & ActorFieldGroups.Equipment) != 0 ? record.HeldLight : actor.HeldLight);
        }
    }
}
