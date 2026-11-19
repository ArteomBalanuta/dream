using Dream.Multiplayer.Protocol;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Players;
using OpenTK.Mathematics;

namespace Dream.Simulation.Runtime;

// Timed MeleeArc swings. An accepted cast spends its mana and cooldown at once, but the arc deals its
// damage only inside the active window of MeleeArcSwing: ticks [cast + WindupTicks, cast + WindupTicks +
// ActiveTicks). Each tick of the window sweeps the whole cone from where the caster stands then, in the
// direction fixed at the cast, and a per-cast hit set gives every target at most one hit. Pending swings
// are runtime state only: never persisted, and lost with the runtime.
public sealed partial class WorldSessionRuntime
{
    private sealed class PendingMeleeSwing
    {
        public PlayerActionMessage Message = null!;
        public SkillDefinition Skill = null!;
        public Vector3 Direction;
        public ulong CastTick;
        public PlayerCombatant Caster = null!;
        public HashSet<long> HitSet = [];
    }

    // FIFO in cast order. The pool keeps finished swings and their hit sets, so steady state allocates nothing.
    private readonly List<PendingMeleeSwing> _pendingMeleeSwings = [];
    private readonly Stack<PendingMeleeSwing> _meleeSwingPool = new();

    private void QueueMeleeSwing(
        PlayerActionMessage message,
        SkillDefinition skill,
        Vector3 direction,
        ulong castTick,
        PlayerCombatant caster)
    {
        PendingMeleeSwing swing = _meleeSwingPool.Count > 0 ? _meleeSwingPool.Pop() : new PendingMeleeSwing();
        swing.Message = message;
        swing.Skill = skill;
        swing.Direction = new Vector3(direction.X, 0f, direction.Z);
        swing.CastTick = castTick;
        swing.Caster = caster;
        swing.HitSet.Clear();
        _pendingMeleeSwings.Add(swing);
    }

    private void AdvancePendingMeleeSwings(ulong tick)
    {
        // A sweep publishes events but never queues another swing, so the list is stable while walking it.
        int write = 0;
        for (int read = 0; read < _pendingMeleeSwings.Count; read++)
        {
            PendingMeleeSwing swing = _pendingMeleeSwings[read];
            ulong windowStart = swing.CastTick + MeleeArcSwing.WindupTicks;
            ulong windowEnd = windowStart + MeleeArcSwing.ActiveTicks;
            bool keep = tick < windowEnd;
            if (keep && tick >= windowStart && !SweepMeleeSwing(swing, tick))
            {
                keep = false;
            }

            if (keep)
            {
                _pendingMeleeSwings[write++] = swing;
                continue;
            }

            swing.Message = null!;
            swing.Skill = null!;
            swing.Caster = null!;
            swing.HitSet.Clear();
            _meleeSwingPool.Push(swing);
        }

        _pendingMeleeSwings.RemoveRange(write, _pendingMeleeSwings.Count - write);
    }

    // The authorisation of the cast is re-checked at the moment of effect: the same combatant behind the
    // session (a reconnect or respawn can swap it), spawned, alive and still in Combat mode. Any failure
    // cancels the swing; the mana and cooldown are not refunded. False cancels.
    private bool SweepMeleeSwing(PendingMeleeSwing swing, ulong tick)
    {
        if (!TryResolveAuthoritativePlayerTarget(swing.Message.SessionId, out _, out IAuthoritativePlayerTarget? caster)
            || caster == null
            || !ReferenceEquals(caster.Combatant, swing.Caster)
            || !caster.IsSpawned
            || !caster.IsAlive
            || !swing.Caster.Health.IsAlive
            || caster.InteractionMode != PlayerInteractionMode.Combat)
        {
            return false;
        }

        _combatResolver.ResolveMeleeArcSweep(
            swing.Message,
            swing.Skill,
            caster.EyePosition,
            swing.Direction,
            swing.Caster,
            tick,
            swing.HitSet,
            out _);
        return true;
    }
}
