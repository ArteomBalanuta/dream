using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Combat;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;


public sealed class WorldObjectRegistry
{
    private readonly Dictionary<object, WorldObjectRecord> _records = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Cube, DetachedCubeRecord> _detachedCubeRecords = new(ReferenceEqualityComparer.Instance);
    private int _nextActorId = 1000;
    // Persistence checkpoint change tracking (off until EnableCheckpointTracking; clients never
    // turn it on): cubes registered (placed, restored, rebound, re-identified) or damaged since the
    // last ClearCheckpointChanges, and the actor ids of cube records removed since then and not
    // registered again. Cube moves are tracked by the map (Map.CheckpointMovedCubes).
    private HashSet<Cube>? _checkpointDirtyCubes;
    private HashSet<int>? _checkpointRemovedCubeIds;
    private Action<WorldObjectRecord>? _recordDamaged;

    public bool IsCheckpointTrackingEnabled => _checkpointDirtyCubes != null;

    public void EnableCheckpointTracking()
    {
        _checkpointDirtyCubes ??= new HashSet<Cube>(ReferenceEqualityComparer.Instance);
        _checkpointRemovedCubeIds ??= [];
        if (_recordDamaged == null)
        {
            _recordDamaged = MarkCheckpointDirty;
            foreach (WorldObjectRecord record in _records.Values)
            {
                record.Damaged = _recordDamaged;
            }
        }
    }

    // Concrete sets (read-only by contract), so the capture enumerates them without boxing.
    internal HashSet<Cube> CheckpointDirtyCubes => _checkpointDirtyCubes ?? EmptyCubeSet;

    internal HashSet<int> CheckpointRemovedCubeIds => _checkpointRemovedCubeIds ?? EmptyIdSet;

    public void ClearCheckpointChanges()
    {
        _checkpointDirtyCubes?.Clear();
        _checkpointRemovedCubeIds?.Clear();
    }

    // Visits every cube record without allocating (GetCubeRecords copies them into an array).
    public Dictionary<object, WorldObjectRecord>.ValueCollection Records => _records.Values;

    private static readonly HashSet<Cube> EmptyCubeSet = [];
    private static readonly HashSet<int> EmptyIdSet = [];

    private void MarkCheckpointDirty(WorldObjectRecord record)
    {
        if (record.SourceObject is Cube cube && _records.TryGetValue(cube, out WorldObjectRecord? current) &&
            ReferenceEquals(current, record))
        {
            _checkpointDirtyCubes?.Add(cube);
        }
    }

    private void TrackRegistered(WorldObjectRecord record)
    {
        record.Damaged = _recordDamaged;
        if (_checkpointDirtyCubes != null && record.SourceObject is Cube cube)
        {
            _checkpointDirtyCubes.Add(cube);
            _checkpointRemovedCubeIds!.Remove(record.Actor.Id);
        }
    }

    private void TrackRemoved(WorldObjectRecord record)
    {
        record.Damaged = null;
        if (_checkpointRemovedCubeIds != null && record.SourceObject is Cube)
        {
            _checkpointRemovedCubeIds.Add(record.Actor.Id);
        }
    }

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

        if (sourceObject is Cube cube)
        {
            _detachedCubeRecords.Remove(cube);
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
        TrackRegistered(record);
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
        WorldObjectRecord record = Register(
            cube,
            () => cube.Position + new Vector3(0f, cube.Side * 0.75f, 0f),
            () => map.RemoveCube(cube),
            CreatureKind.Object,
            maxHealth,
            currentHealth,
            actorId,
            ownerPlayerName);

        // Every registration path (placement, persistence restore, rebinds, identity repair,
        // snapshot restore after a map rebuild) keys the map's replicated id to the actor id the
        // server replicates for this cube, so the authoritative map resolves and checksums cubes
        // by the same identity a client map receives. Map.RemoveCube drops the id again.
        map.AssignReplicatedCubeId(cube, record.Actor.Id);
        return record;
    }

    public WorldObjectRecord? TryGet(object sourceObject)
    {
        _records.TryGetValue(sourceObject, out WorldObjectRecord? record);
        return record;
    }

    public WorldObjectRecord? TryResolveCubeRecord(Map map, Cube cube)
    {
        if (_records.TryGetValue(cube, out WorldObjectRecord? exact))
        {
            return exact;
        }

        if (_detachedCubeRecords.TryGetValue(cube, out DetachedCubeRecord detached))
        {
            _detachedCubeRecords.Remove(cube);
            return RegisterCube(
                cube,
                map,
                detached.MaxHealth,
                detached.CurrentHealth,
                detached.ActorId,
                detached.OwnerPlayerName);
        }

        if (!TryGetAuthoritativeCubeCell(map, cube, out GeometryCellKey cell) || cell.Y == 0)
        {
            return null;
        }

        WorldObjectRecord[] matchingCellRecords = _records.Values
            .Where(record => record.SourceObject is Cube candidate && IsCubeAtCell(candidate, cell))
            .ToArray();

        if (matchingCellRecords.Length == 1)
        {
            return RebindCubeRecord(matchingCellRecords[0], cube, map);
        }

        return RegisterCube(cube, map);
    }

    public bool Unregister(object sourceObject, bool preserveDetachedRecord = true)
    {
        if (!_records.Remove(sourceObject, out WorldObjectRecord? record))
        {
            return false;
        }

        TrackRemoved(record);

        if (sourceObject is Cube cube && preserveDetachedRecord)
        {
            _detachedCubeRecords[cube] = new DetachedCubeRecord(
                record.Actor.Id,
                record.Health.MaxHealth,
                record.Health.CurrentHealth,
                record.OwnerPlayerName);
            return true;
        }

        if (sourceObject is Cube removedCube)
        {
            _detachedCubeRecords.Remove(removedCube);
        }

        return true;
    }

    public bool TryRemove(WorldObjectRecord record)
    {
        bool removed = record.TryRemoveFromWorld();
        if (removed)
        {
            if (_records.Remove(record.SourceObject, out WorldObjectRecord? removedRecord))
            {
                TrackRemoved(removedRecord);
            }

            if (record.SourceObject is Cube cube)
            {
                _detachedCubeRecords.Remove(cube);
            }
        }

        return removed;
    }

    public WorldObjectRecord ReplaceCubeRecordIdentity(
        Cube cube,
        Map map,
        int actorId,
        int maxHealth,
        int currentHealth,
        string? ownerPlayerName)
    {
        if (_records.Remove(cube, out WorldObjectRecord? replaced))
        {
            TrackRemoved(replaced);
        }

        _detachedCubeRecords.Remove(cube);

        if (TryGetAuthoritativeCubeCell(map, cube, out GeometryCellKey cell))
        {
            object[] staleEntries = _records.Values
                .Where(record => record.SourceObject is Cube candidate && IsCubeAtCell(candidate, cell))
                .Select(record => record.SourceObject)
                .ToArray();
            foreach (object staleEntry in staleEntries)
            {
                if (_records.Remove(staleEntry, out WorldObjectRecord? stale))
                {
                    TrackRemoved(stale);
                }
            }
        }

        return RegisterCube(
            cube,
            map,
            maxHealth,
            currentHealth,
            actorId,
            ownerPlayerName);
    }

    public bool TryGetByActorId(int actorId, out WorldObjectRecord record)
    {
        record = _records.Values.FirstOrDefault(candidate => candidate.Actor.Id == actorId)!;
        return record != null;
    }

    public IReadOnlyList<WorldObjectRecord> GetCubeRecords()
    {
        return _records.Values
            .Where(record => record.SourceObject is Cube)
            .ToArray();
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
        foreach (WorldObjectRecord existing in _records.Values)
        {
            TrackRemoved(existing);
        }

        _records.Clear();
        _detachedCubeRecords.Clear();

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

    private WorldObjectRecord RebindCubeRecord(WorldObjectRecord record, Cube cube, Map map)
    {
        if (_records.Remove(record.SourceObject))
        {
            TrackRemoved(record);
        }

        return RegisterCube(
            cube,
            map,
            record.Health.MaxHealth,
            record.Health.CurrentHealth,
            record.Actor.Id,
            record.OwnerPlayerName);
    }

    private static bool TryGetAuthoritativeCubeCell(Map map, Cube cube, out GeometryCellKey cell)
    {
        if (map.TryGetCellForCube(cube, out cell))
        {
            return true;
        }

        cell = ToCellKey(cube.Position);
        Cube? occupant = map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z);
        return ReferenceEquals(occupant, cube);
    }

    private static bool IsCubeAtCell(Cube cube, GeometryCellKey cell)
    {
        return ToCellKey(cube.Position) == cell;
    }

    private static GeometryCellKey ToCellKey(Vector3 position)
    {
        return new GeometryCellKey(
            (int)MathF.Floor(position.X + 1e-6f),
            (int)MathF.Floor(position.Y + 1e-6f),
            (int)MathF.Floor(position.Z + 1e-6f));
    }

    private readonly record struct DetachedCubeRecord(
        int ActorId,
        int MaxHealth,
        int CurrentHealth,
        string? OwnerPlayerName);
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

    // Set by WorldObjectRegistry while the record is registered and checkpoint tracking is on.
    internal Action<WorldObjectRecord>? Damaged { get; set; }

    public DamageReceipt ApplyDamage(int amount)
    {
        DamageReceipt receipt = Health.ApplyDamage(amount);
        if (receipt.AppliedAmount > 0)
        {
            Damaged?.Invoke(this);
        }

        return receipt;
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
