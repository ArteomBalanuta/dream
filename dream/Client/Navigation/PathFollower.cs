using OpenTK.Mathematics;

namespace Dream.Client.Navigation;

public readonly record struct NavStep(Vector3 Direction, bool Jump, bool Arrived, bool NeedsReplan);

public sealed class PathFollower
{
    public bool HasPath => throw new NotImplementedException();

    public void SetPath(IReadOnlyList<NavCell> path, bool viaStartCentre) => throw new NotImplementedException();

    public void Clear() => throw new NotImplementedException();

    // Once per 64 Hz tick.
    public NavStep Step(Vector3 bodyCentre, INavWorld world) => throw new NotImplementedException();
}
