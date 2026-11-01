namespace Dream.Client.Navigation;

public sealed class GridPathfinder
{
    public const int MaxExpansions = 20000;

    private const int StraightCost = 10;
    private const int DiagonalCost = 14;
    private const int JumpCost = 10;

    private static readonly (int Dx, int Dz)[] Orthogonal = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private static readonly (int Dx, int Dz)[] Diagonal = [(1, 1), (1, -1), (-1, 1), (-1, -1)];

    private readonly PriorityQueue<NavCell, int> _open = new();
    private readonly Dictionary<NavCell, int> _cost = [];
    private readonly Dictionary<NavCell, NavCell> _parent = [];
    private readonly HashSet<NavCell> _closed = [];

    public static bool IsStandable(INavWorld world, int x, int level, int z)
        => x >= 0 && z >= 0 && x < world.Width && z < world.Depth
            && level >= 1 && level < world.Height
            && !world.IsBlocked(x, level, z)
            && world.IsSupport(x, level - 1, z);

    // path is filled start..end inclusive. False only if no standable start exists.
    // When the goal cannot be reached the path ends at the expanded cell nearest to it:
    // smallest horizontal distance first, then smallest level difference.
    public bool TryFindPath(INavWorld world, NavCell start, NavCell goal, List<NavCell> path, out bool reachedGoal)
    {
        path.Clear();
        reachedGoal = false;
        if (!TryResolveStart(world, start, out start))
        {
            return false;
        }

        _open.Clear();
        _cost.Clear();
        _parent.Clear();
        _closed.Clear();
        _cost[start] = 0;
        _open.Enqueue(start, 0);
        NavCell best = start;
        (int, int) bestScore = Score(start, goal);

        for (int expansions = 0; expansions < MaxExpansions && _open.TryDequeue(out NavCell node, out _); expansions++)
        {
            if (!_closed.Add(node))
            {
                continue;
            }

            if (node == goal)
            {
                reachedGoal = true;
                best = node;
                break;
            }

            (int, int) score = Score(node, goal);
            if (score.CompareTo(bestScore) < 0)
            {
                best = node;
                bestScore = score;
            }

            int cost = _cost[node];
            foreach ((int dx, int dz) in Orthogonal)
            {
                if (TryOrthogonal(world, node, dx, dz, out NavCell next, out int stepCost))
                {
                    Relax(node, next, cost + stepCost, goal);
                }
            }

            foreach ((int dx, int dz) in Diagonal)
            {
                if (IsStandable(world, node.X + dx, node.Level, node.Z)
                    && IsStandable(world, node.X, node.Level, node.Z + dz)
                    && IsStandable(world, node.X + dx, node.Level, node.Z + dz))
                {
                    Relax(node, new NavCell(node.X + dx, node.Level, node.Z + dz), cost + DiagonalCost, goal);
                }
            }
        }

        for (NavCell cell = best; ; cell = _parent[cell])
        {
            path.Add(cell);
            if (cell == start)
            {
                break;
            }
        }

        path.Reverse();
        return true;
    }

    // Same level, one step up (needs the cell above the origin free) or a drop of any height.
    private static bool TryOrthogonal(INavWorld world, NavCell from, int dx, int dz, out NavCell next, out int cost)
    {
        int x = from.X + dx;
        int z = from.Z + dz;
        next = default;
        cost = StraightCost;
        if (x < 0 || z < 0 || x >= world.Width || z >= world.Depth)
        {
            return false;
        }

        if (world.IsBlocked(x, from.Level, z))
        {
            cost = StraightCost + JumpCost;
            next = new NavCell(x, from.Level + 1, z);
            return !world.IsBlocked(from.X, from.Level + 1, from.Z)
                && IsStandable(world, x, from.Level + 1, z);
        }

        for (int level = from.Level; level >= 1 && !world.IsBlocked(x, level, z); level--)
        {
            if (world.IsSupport(x, level - 1, z))
            {
                next = new NavCell(x, level, z);
                return true;
            }
        }

        return false;
    }

    private void Relax(NavCell from, NavCell to, int cost, NavCell goal)
    {
        if (_closed.Contains(to) || (_cost.TryGetValue(to, out int known) && known <= cost))
        {
            return;
        }

        _cost[to] = cost;
        _parent[to] = from;
        int dx = Math.Abs(to.X - goal.X);
        int dz = Math.Abs(to.Z - goal.Z);
        int heuristic = StraightCost * (dx + dz) - (2 * StraightCost - DiagonalCost) * Math.Min(dx, dz);
        _open.Enqueue(to, cost + heuristic);
    }

    private static (int Horizontal, int Level) Score(NavCell cell, NavCell goal)
    {
        int dx = cell.X - goal.X;
        int dz = cell.Z - goal.Z;
        return (dx * dx + dz * dz, Math.Abs(cell.Level - goal.Level));
    }

    // The body may be a little above its support (mid-fall), so look down for a floor.
    private static bool TryResolveStart(INavWorld world, NavCell start, out NavCell resolved)
    {
        for (int level = Math.Min(start.Level, world.Height - 1); level >= 1; level--)
        {
            if (IsStandable(world, start.X, level, start.Z))
            {
                resolved = new NavCell(start.X, level, start.Z);
                return true;
            }
        }

        resolved = start;
        return false;
    }
}
