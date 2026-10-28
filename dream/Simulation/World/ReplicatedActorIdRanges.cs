namespace Dream.Simulation.World;

// The replicated actor id space that world objects (cubes) and enemies share on the wire.
//
// World objects take ids from [FirstWorldObjectId, FirstEnemyId) and, once that block runs out,
// from [FirstOverflowWorldObjectId, int.MaxValue]. Enemies take ids from
// [FirstEnemyId, FirstOverflowWorldObjectId). The two kinds therefore never share an id, however
// many cubes are placed or enemies respawn. The low blocks are the ranges every world used before
// the partition (cubes from 1000, enemies from 2000), so a fresh world allocates the same ids it
// always did and existing persisted ids keep their meaning.
//
// Worlds saved before the partition can hold placed-cube ids at or above FirstEnemyId (a cube
// counter that ran past 2000). Such a legacy id stays the cube's id; the world restore reserves it
// in the enemy counter so no later enemy takes it (see WorldSessionRuntime.RestorePersistedWorldState).
// Player actor ids are allocated separately and are not covered here.
public static class ReplicatedActorIdRanges
{
    public const int FirstWorldObjectId = 1000;

    public const int FirstEnemyId = 2000;

    // 2^30: about a billion enemy ids below it and about a billion more world-object ids above it.
    public const int FirstOverflowWorldObjectId = 1 << 30;

    public static bool IsWorldObjectId(int actorId)
    {
        return actorId is >= FirstWorldObjectId and < FirstEnemyId ||
            actorId >= FirstOverflowWorldObjectId;
    }

    public static bool IsEnemyId(int actorId)
    {
        return actorId is >= FirstEnemyId and < FirstOverflowWorldObjectId;
    }

    // The world-object id allocated after actorId: the next id, skipping the enemy range.
    public static int NextWorldObjectId(int actorId)
    {
        if (actorId == int.MaxValue)
        {
            throw new InvalidOperationException("The replicated world-object id space is exhausted.");
        }

        int next = actorId + 1;
        return next is >= FirstEnemyId and < FirstOverflowWorldObjectId
            ? FirstOverflowWorldObjectId
            : Math.Max(next, FirstWorldObjectId);
    }

    public static int NextEnemyId(int actorId)
    {
        int next = Math.Max(actorId + 1, FirstEnemyId);
        if (next >= FirstOverflowWorldObjectId)
        {
            throw new InvalidOperationException("The replicated enemy id space is exhausted.");
        }

        return next;
    }
}
