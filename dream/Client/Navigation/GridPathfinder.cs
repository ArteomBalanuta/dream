namespace Dream.Client.Navigation;

public sealed class GridPathfinder
{
    public const int MaxExpansions = 20000;

    // path is filled start..end inclusive. False only if no standable start exists.
    public bool TryFindPath(INavWorld world, NavCell start, NavCell goal, List<NavCell> path, out bool reachedGoal)
        => throw new NotImplementedException();
}
