using OpenTK.Mathematics;

namespace Dream.Rendering.Effects;

// Which cast effect to play. Mapped from the skill's kind by whoever publishes the event.
public enum SkillFxKind : byte
{
    Arc,
    Bolt,
    Mend
}

// A skill the local player just cast. Origin is the caster's world position as
// Player.GetWorldPosition() returns it (capsule centre); the effects apply their own lifts and
// drops. Direction is a horizontal unit vector, and a zero or non-finite one means "no direction":
// the effect falls back to a radial burst. Range is the skill's range in metres, 0 for a self-cast.
// CasterActorId is the caster's actor id as the scene knows it (0 = unknown): a timed effect uses it to follow the
// caster through its wind-up, since the server sweeps from where the caster stands, not where it stood.
public readonly record struct SkillCastEvent(
    SkillFxKind Kind,
    Vector3 Origin,
    Vector3 Direction,
    float Range,
    int CasterActorId = 0);
