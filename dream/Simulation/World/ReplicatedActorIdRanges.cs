namespace Dream.Simulation.World;

// The replicated actor id space that players, world objects (cubes) and enemies share on the wire.
//
// Players take ids from [FirstPlayerId, FirstWorldObjectId) and, only while that block is all in
// use at once, from [FirstOverflowPlayerId, int.MaxValue]. World objects take ids from
// [FirstWorldObjectId, FirstEnemyId) and, once that block runs out, from
// [FirstOverflowWorldObjectId, LastWorldObjectId]. Enemies take ids from
// [FirstEnemyId, FirstOverflowWorldObjectId). The three kinds therefore never share an id, however
// many players join, cubes are placed or enemies respawn. The low blocks are the ranges every world
// used before the partition (players from 1, cubes from 1000, enemies from 2000), so a fresh world
// allocates the same ids it always did and existing persisted ids keep their meaning.
//
// Player ids are not persisted. A remote player's id is released when its session detaches and the
// next join takes the lowest released id (see WorldSessionRuntime.AllocateRemoteActorId), so the
// player block is bounded by the players connected at once, not by how many ever joined.
//
// Actor ids are not persisted. A world restore gives cubes and enemies fresh actor ids (cubes from
// 1000, enemies from 2000, in ObjectId order) and binds them to their persisted ObjectId
// (ObjectIdentityMap), so a world saved before the partition restores without any id collision.
//
// Counters: an allocator keeps the id it hands out next. Reserving an id that is already taken moves
// the counter past it, and saturates at ExhaustedCounter when the taken id is the last of its
// range, so a reservation never fails. Only handing out
// an id from an exhausted counter fails (TakeWorldObjectId, TakeEnemyId).
public static class ReplicatedActorIdRanges
{
    public const int FirstPlayerId = 1;

    public const int FirstWorldObjectId = 1000;

    public const int FirstEnemyId = 2000;

    // 2^30: about a billion enemy ids below it and about a billion more world-object ids above it.
    public const int FirstOverflowWorldObjectId = 1 << 30;

    // The top 2^20 ids: players beyond the 999 low ids connected at once. Never reached in
    // practice; it only guarantees a join can never take a cube or enemy id.
    public const int FirstOverflowPlayerId = int.MaxValue - (1 << 20) + 1;

    public const int LastWorldObjectId = FirstOverflowPlayerId - 1;

    public const int LastEnemyId = FirstOverflowWorldObjectId - 1;

    // The counter of a range with no id left to hand out. Never a valid actor id.
    public const int ExhaustedCounter = int.MinValue;

    public static bool IsPlayerId(int actorId)
    {
        return actorId is >= FirstPlayerId and < FirstWorldObjectId ||
            actorId >= FirstOverflowPlayerId;
    }

    public static bool IsWorldObjectId(int actorId)
    {
        return actorId is >= FirstWorldObjectId and < FirstEnemyId ||
            actorId is >= FirstOverflowWorldObjectId and <= LastWorldObjectId;
    }

    public static bool IsEnemyId(int actorId)
    {
        return actorId is >= FirstEnemyId and < FirstOverflowWorldObjectId;
    }

    // The world-object id allocated after actorId: the next id, skipping the enemy range.
    public static int NextWorldObjectId(int actorId)
    {
        if (actorId >= LastWorldObjectId)
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

    // The player id allocated after actorId: the next id, skipping the cube and enemy ranges.
    public static int NextPlayerId(int actorId)
    {
        if (actorId == int.MaxValue)
        {
            throw new InvalidOperationException("The replicated player id space is exhausted.");
        }

        int next = Math.Max(actorId + 1, FirstPlayerId);
        return next is >= FirstWorldObjectId and < FirstOverflowPlayerId ? FirstOverflowPlayerId : next;
    }

    // The world-object counter once takenId is known to be in use: past it, or ExhaustedCounter
    // when it is the last world-object id. An id outside the world-object ranges, or a counter
    // already exhausted, leaves the counter as it is.
    public static int ReserveWorldObjectId(int counter, int takenId)
    {
        if (counter == ExhaustedCounter || !IsWorldObjectId(takenId))
        {
            return counter;
        }

        return takenId == LastWorldObjectId
            ? ExhaustedCounter
            : Math.Max(counter, NextWorldObjectId(takenId));
    }

    public static int ReserveEnemyId(int counter, int takenId)
    {
        if (counter == ExhaustedCounter || !IsEnemyId(takenId))
        {
            return counter;
        }

        return takenId == LastEnemyId
            ? ExhaustedCounter
            : Math.Max(counter, NextEnemyId(takenId));
    }

    // Hands out the counter's id and advances the counter. Throws only when the range is exhausted.
    public static int TakeWorldObjectId(ref int counter)
    {
        if (counter == ExhaustedCounter)
        {
            throw new InvalidOperationException("The replicated world-object id space is exhausted.");
        }

        int actorId = counter;
        counter = ReserveWorldObjectId(counter, actorId);
        return actorId;
    }

    public static int TakeEnemyId(ref int counter)
    {
        if (counter == ExhaustedCounter)
        {
            throw new InvalidOperationException("The replicated enemy id space is exhausted.");
        }

        int actorId = counter;
        counter = ReserveEnemyId(counter, actorId);
        return actorId;
    }
}
