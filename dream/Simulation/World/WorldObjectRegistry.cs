using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Combat;

namespace Dream.Simulation.World;


public sealed class WorldObjectRegistry
{
    private readonly Dictionary<object, WorldObjectRecord> _records = new(ReferenceEqualityComparer.Instance);
    private int _nextActorId = 1000;

    public WorldObjectRecord Register(
        object sourceObject,
        Func<Vector3> anchorProvider,
        Func<bool> removeFromWorld,
        CreatureKind kind = CreatureKind.Object,
        int maxHealth = 100,
        int? currentHealth = null,
        int? actorId = null,
        string? ownerPlayerName = null)
    {
        if (_records.TryGetValue(sourceObject, out WorldObjectRecord? existing))
        {
            return existing;
        }

        int resolvedActorId = actorId ?? _nextActorId++;
        _nextActorId = Math.Max(_nextActorId, resolvedActorId + 1);

        WorldObjectRecord record = new(
            sourceObject,
            anchorProvider,
            removeFromWorld,
            ownerPlayerName,
            kind,
            resolvedActorId,
            maxHealth,
            currentHealth ?? maxHealth);

        _records[sourceObject] = record;
        return record;
    }

    public WorldObjectRecord RegisterCube(
        Cube cube,
        Map map,
        int maxHealth = 100,
        int? currentHealth = null,
        int? actorId = null,
        string? ownerPlayerName = null)
    {
        return Register(
            cube,
            () => cube.Position + new Vector3(0f, cube.Side * 0.75f, 0f),
            () => map.RemoveCube(cube),
            CreatureKind.Object,
            maxHealth,
            currentHealth,
            actorId,
            ownerPlayerName);
    }

    public WorldObjectRecord? TryGet(object sourceObject)
    {
        _records.TryGetValue(sourceObject, out WorldObjectRecord? record);
        return record;
    }

    public bool Unregister(object sourceObject)
    {
        return _records.Remove(sourceObject);
    }

    public bool TryRemove(WorldObjectRecord record)
    {
        bool removed = record.TryRemoveFromWorld();
        if (removed)
        {
            _records.Remove(record.SourceObject);
        }

        return removed;
    }

    public bool TryGetByActorId(int actorId, out WorldObjectRecord record)
    {
        record = _records.Values.FirstOrDefault(candidate => candidate.Actor.Id == actorId)!;
        return record != null;
    }

    public WorldObjectCubeSnapshot CreateCubeSnapshot()
    {
        List<WorldObjectCubeSnapshotEntry> entries = new(_records.Count);
        foreach (WorldObjectRecord record in _records.Values)
        {
            if (record.SourceObject is not Cube cube)
            {
                continue;
            }

            Vector3 cell = GetCell(cube.Position);
            entries.Add(new WorldObjectCubeSnapshotEntry(
                (int)cell.X,
                (int)cell.Y,
                (int)cell.Z,
                record.Health.CurrentHealth,
                record.Health.MaxHealth,
                record.Actor.Id,
                record.Actor.Kind,
                record.OwnerPlayerName));
        }

        return new WorldObjectCubeSnapshot(entries);
    }

    public void RestoreCubeSnapshot(Map map, WorldObjectCubeSnapshot snapshot)
    {
        _records.Clear();

        foreach (WorldObjectCubeSnapshotEntry entry in snapshot.Entries)
        {
            Cube? cube = map.TryGetCubeAtCell(entry.X, entry.Y, entry.Z);
            if (cube == null || entry.Y == 0)
            {
                continue;
            }

            RegisterCube(cube, map, entry.MaxHealth, entry.CurrentHealth, entry.ActorId, entry.OwnerPlayerName);
        }
    }

    private static Vector3 GetCell(Vector3 position)
    {
        return new Vector3(
            (int)MathF.Floor(position.X + 1e-6f),
            (int)MathF.Floor(position.Y + 1e-6f),
            (int)MathF.Floor(position.Z + 1e-6f));
    }
}

public sealed class WorldObjectRecord : IDamageable, ICombatAnchorProvider
{
    private readonly Func<Vector3> _anchorProvider;
    private readonly Func<bool> _removeFromWorld;

    public WorldObjectRecord(
        object sourceObject,
        Func<Vector3> anchorProvider,
        Func<bool> removeFromWorld,
        string? ownerPlayerName,
        CreatureKind kind,
        int actorId,
        int maxHealth,
        int currentHealth)
    {
        SourceObject = sourceObject;
        _anchorProvider = anchorProvider;
        _removeFromWorld = removeFromWorld;
        OwnerPlayerName = ownerPlayerName;
        Health = new HealthState(maxHealth);
        if (currentHealth < maxHealth)
        {
            Health.ApplyDamage(maxHealth - currentHealth);
        }

        Actor = new CombatActorHandle(actorId, kind, this, this);
    }

    public object SourceObject { get; }

    public string? OwnerPlayerName { get; }

    public HealthState Health { get; }

    public CombatActorHandle Actor { get; }

    public DamageReceipt ApplyDamage(int amount)
    {
        return Health.ApplyDamage(amount);
    }

    public Vector3 GetCombatAnchorWorldPosition()
    {
        return _anchorProvider();
    }

    public bool TryRemoveFromWorld()
    {
        return _removeFromWorld();
    }
}

public readonly record struct WorldObjectCubeSnapshot(IReadOnlyList<WorldObjectCubeSnapshotEntry> Entries);

public readonly record struct WorldObjectCubeSnapshotEntry(
    int X,
    int Y,
    int Z,
    int CurrentHealth,
    int MaxHealth,
    int ActorId,
    CreatureKind Kind,
    string? OwnerPlayerName);
