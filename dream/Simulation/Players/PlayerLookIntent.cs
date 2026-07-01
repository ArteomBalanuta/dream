using OpenTK.Mathematics;

namespace Dream.Simulation.Players;


public readonly record struct PlayerLookIntent(
    float YawDelta,
    float PitchDelta,
    Vector3? Facing = null)
{
    public static PlayerLookIntent None => new(0f, 0f, null);
}
