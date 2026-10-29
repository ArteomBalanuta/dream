namespace Dream.Simulation.World;

// The runtime mapping between persistent ObjectIds and the int actor ids used on the wire. Cubes and
// enemies share one map: their actor id ranges are disjoint. One-to-one: binding drops any earlier
// binding of either side. Tick thread only.
public sealed class ObjectIdentityMap
{
    private readonly Dictionary<int, ObjectId> _byActor = [];
    private readonly Dictionary<ObjectId, int> _byObject = [];

    public int Count => _byActor.Count;

    public void Bind(ObjectId objectId, int actorId)
    {
        if (objectId.IsNone)
        {
            throw new ArgumentException("Cannot bind ObjectId.None.", nameof(objectId));
        }

        if (_byActor.Remove(actorId, out ObjectId oldObject))
        {
            _byObject.Remove(oldObject);
        }

        if (_byObject.Remove(objectId, out int oldActor))
        {
            _byActor.Remove(oldActor);
        }

        _byActor[actorId] = objectId;
        _byObject[objectId] = actorId;
    }

    public bool TryGetObjectId(int actorId, out ObjectId objectId) => _byActor.TryGetValue(actorId, out objectId);

    public bool TryGetActorId(ObjectId objectId, out int actorId) => _byObject.TryGetValue(objectId, out actorId);

    public bool RemoveByActorId(int actorId, out ObjectId objectId)
    {
        if (!_byActor.Remove(actorId, out objectId))
        {
            return false;
        }

        _byObject.Remove(objectId);
        return true;
    }

    public bool RemoveByObjectId(ObjectId objectId)
    {
        if (!_byObject.Remove(objectId, out int actorId))
        {
            return false;
        }

        _byActor.Remove(actorId);
        return true;
    }

    public void Clear()
    {
        _byActor.Clear();
        _byObject.Clear();
    }
}
