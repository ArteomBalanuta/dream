using OpenTK.Mathematics;
using Dream.Simulation.Content;

namespace Dream.Simulation.World;

// A skill cast, replicated so other clients can draw it (protocol v22, trailing "castEvents").
// Presentation only: nothing in the simulation reads it, it is not sequenced and no checksum covers
// it. Origin is the server's cast origin and Direction its resolved, unnormalised cast direction;
// Range is the skill definition's range.
public readonly record struct CastEventDelta(
    int ActorId,
    SkillKind Kind,
    Vector3 Origin,
    Vector3 Direction,
    float Range);
