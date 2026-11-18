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
public readonly record struct SkillCastEvent(SkillFxKind Kind, Vector3 Origin, Vector3 Direction, float Range);
