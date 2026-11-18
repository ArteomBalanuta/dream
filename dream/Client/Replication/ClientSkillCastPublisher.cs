using Dream.Rendering.Effects;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Content;
using Dream.Simulation.Runtime;
using OpenTK.Mathematics;

namespace Dream.Client.Replication;

// Turns an accepted AbilityUsedResult for the local player into one SkillCastEvent for the FX bridge.
// Presentation only: it reads the shipped catalogue and publishes; nothing here feeds the simulation.
// The caller guarantees the result was accepted (a refused cast carries no AbilityUsedResult). Mapped
// on SkillKind, not on the key, so a further skill of an existing kind gets its effect unchanged.
public static class ClientSkillCastPublisher
{
    // Returns true when an event was published. An unknown key, an unmapped kind or a non-finite
    // origin/direction publishes nothing. A zero direction is legal: it tells the effect to burst radially.
    public static bool Publish(SceneEventHub events, AbilityUsedResult result, Vector3 origin, Vector3 forward)
    {
        if (events == null || result == null || !IsFinite(origin) || !IsFinite(forward))
        {
            return false;
        }

        if (!ContentCatalog.Shipped.TryGetSkill(result.Key, out SkillDefinition skill))
        {
            return false;
        }

        return Publish(events, skill.Kind, origin, forward, skill.Range);
    }

    // The shared tail, and the entry for a peer's cast, whose kind, origin and range arrive on the wire.
    // An unmapped kind, a non-finite vector or a negative/non-finite range publishes nothing. A range of
    // zero is legal only for Mend, which has none; an Arc or Bolt of no reach is nonsense and is dropped.
    public static bool Publish(SceneEventHub events, SkillKind skillKind, Vector3 origin, Vector3 forward, float range)
    {
        if (events == null || !IsFinite(origin) || !IsFinite(forward) || !float.IsFinite(range) || range < 0f)
        {
            return false;
        }

        SkillFxKind kind;
        switch (skillKind)
        {
            case SkillKind.MeleeArc:
                kind = SkillFxKind.Arc;
                break;
            case SkillKind.RangedBolt:
                kind = SkillFxKind.Bolt;
                break;
            case SkillKind.SelfHeal:
                kind = SkillFxKind.Mend;
                range = 0f;
                break;
            default:
                return false;
        }

        if (range <= 0f && kind != SkillFxKind.Mend)
        {
            return false;
        }

        // The contract wants a horizontal unit vector; a vertical or zero aim becomes "no direction".
        Vector3 direction = new(forward.X, 0f, forward.Z);
        float lengthSquared = direction.LengthSquared;
        direction = lengthSquared > 1e-6f ? direction / MathF.Sqrt(lengthSquared) : Vector3.Zero;

        events.Publish(new SkillCastEvent(kind, origin, direction, range));
        return true;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
