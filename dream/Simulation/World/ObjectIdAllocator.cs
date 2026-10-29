namespace Dream.Simulation.World;

// Hands out ObjectIds from one monotonically increasing counter. Tick thread only: it is not
// thread-safe, and allocation order must be deterministic (lockstep databases are compared row by row).
public sealed class ObjectIdAllocator
{
    private long _next;

    public ObjectIdAllocator(long next = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(next, 1L);
        _next = next;
    }

    // The value Take() returns next.
    public long Next => _next;

    public ObjectId Take()
    {
        if (_next == long.MaxValue)
        {
            throw new InvalidOperationException("The object id counter is exhausted.");
        }

        return new ObjectId(_next++);
    }

    // Raises the counter to the stored one; never lowers it.
    public void Restore(long storedNext)
    {
        _next = Math.Max(_next, storedNext);
    }

    // Marks an id taken elsewhere (a loaded row) so it is never issued again. None is ignored.
    public void Reserve(ObjectId taken)
    {
        if (taken.IsNone)
        {
            return;
        }

        if (taken.Value == long.MaxValue)
        {
            throw new InvalidOperationException("The object id counter is exhausted.");
        }

        _next = Math.Max(_next, taken.Value + 1);
    }
}
