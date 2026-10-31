using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.Persistence;

// ARCH-3 step B: RuntimePersistenceBridge against fake ports and a fake runtime host, without a
// WorldSessionRuntime. The runtime-level persistence suites cover the same paths end to end.
public sealed class RuntimePersistenceBridgeTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RestoreWorld_WithoutWorldPersistence_OnlySpawnsTheInitialWolf()
    {
        Rig rig = new(world: null);

        rig.Bridge.RestoreWorld();

        Assert.Equal(1, rig.Host.InitialWolfSpawns);
        Assert.Null(rig.Bridge.CapturePersistenceCheckpoint(full: true));
    }

    [Fact]
    public void RestoreWorld_RegistersCubes_GivesFreshActorIds_AndBindsEachToItsObjectId()
    {
        FakeWorld world = new();
        world.Cubes.Add(new PersistedPlacedCube(
            new ObjectId(7), "builder", new Vector3(3, 1, 3), GeometryShapeKind.SolidCube, GeometryMaterialId.Grass, 60, 100, Now, Now));
        // Listed out of ObjectId order on purpose.
        world.Enemies.Add(Enemy(2001));
        world.Enemies.Add(Enemy(9));
        Rig rig = new(world);
        rig.Host.NextEnemyId = 2500;

        rig.Bridge.RestoreWorld();

        Assert.Equal(0, rig.Host.InitialWolfSpawns);
        WorldObjectRecord record = Assert.Single(rig.WorldObjects.Records);
        Assert.Equal(ReplicatedActorIdRanges.FirstWorldObjectId, record.Actor.Id);
        Assert.Equal("builder", record.OwnerPlayerName);
        Assert.Equal(60, record.Health.CurrentHealth);
        Assert.True(rig.Identities.TryGetObjectId(record.Actor.Id, out ObjectId cubeObject));
        Assert.Equal(new ObjectId(7), cubeObject);
        // Enemies take actor ids in ObjectId order, whatever order the repository returned them in.
        Assert.True(rig.Identities.TryGetActorId(new ObjectId(9), out int firstActor));
        Assert.True(rig.Identities.TryGetActorId(new ObjectId(2001), out int secondActor));
        Assert.Equal((2500, 2501), (firstActor, secondActor));
        Assert.Equal([2500, 2501], rig.Enemies.ActiveActors.Select(enemy => enemy.Combatant.Actor.Id));
        // The allocator is past every loaded id.
        Assert.Equal(2002, rig.ObjectIds.Next);
    }

    [Fact]
    public void RestoreWorld_CountsCubesSkippedForAnOccupiedOrOutOfBoundsCell_InOneSummaryLine()
    {
        FakeWorld world = new();
        world.Cubes.Add(new PersistedPlacedCube(
            new ObjectId(7), "builder", new Vector3(3, 1, 3), GeometryShapeKind.SolidCube, GeometryMaterialId.Grass, 60, 100, Now, Now));
        world.Cubes.Add(new PersistedPlacedCube(
            new ObjectId(8), "builder", new Vector3(3, 1, 3), GeometryShapeKind.SolidCube, GeometryMaterialId.Grass, 60, 100, Now, Now));
        world.Cubes.Add(new PersistedPlacedCube(
            new ObjectId(9), "builder", new Vector3(100000, 1, 3), GeometryShapeKind.SolidCube, GeometryMaterialId.Grass, 60, 100, Now, Now));
        Rig rig = new(world);
        List<(string Category, string Message)> lines = [];

        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            rig.Bridge.RestoreWorld();
        }

        Assert.Single(rig.WorldObjects.Records);
        (string category, string message) = Assert.Single(
            lines.Where(line => line.Category == "server.persistence.restore-skipped-cubes"));
        Assert.Contains("count=2", message);
    }

    [Fact]
    public void Prefetch_LoadsThroughTheQueue_AndRestoreOfANewPlayerCreatesItsProfileOnce()
    {
        FakeProfiles profiles = new();
        Rig rig = new(new FakeWorld(), profiles);

        PersistedPlayerPrefetch? prefetch = rig.Bridge.BeginPersistedPlayerPrefetch("newcomer");

        Assert.NotNull(prefetch);
        Assert.Equal(PersistedPlayerLoadState.Ready, rig.Bridge.GetPersistedPlayerLoadState("newcomer"));
        Assert.True(rig.Bridge.TryLoadPlayerForRestore(
            new PlayerSessionId("s1"), "newcomer", out PersistedPlayerProfile profile, out _, out _));
        Assert.Equal("newcomer", profile.PlayerName);
        // The read-only prefetch found no row; the applied join saves the default profile.
        Assert.Equal(["newcomer"], profiles.Saved.Select(saved => saved.PlayerName));
        Assert.Equal(0, profiles.GetOrCreateCalls);
        Assert.Equal(PersistedPlayerLoadState.Missing, rig.Bridge.GetPersistedPlayerLoadState("newcomer"));
        Assert.Equal(1, rig.Bridge.KnownProfileNameCount);
    }

    [Fact]
    public void Prefetch_IsRefusedOnceTheHostIsDisposing()
    {
        Rig rig = new(new FakeWorld(), new FakeProfiles());
        rig.Host.IsDisposed = true;

        Assert.Null(rig.Bridge.BeginPersistedPlayerPrefetch("late"));
        Assert.Equal(PersistedPlayerLoadState.Missing, rig.Bridge.GetPersistedPlayerLoadState("late"));
    }

    [Fact]
    public void SynchronousRestore_SkipsTheInventoryReadWhenTheHostAlreadyHasIt()
    {
        FakeWorld world = new();
        Rig rig = new(world, new FakeProfiles());
        rig.Host.LoadedInventories.Add("veteran");

        Assert.True(rig.Bridge.TryLoadPlayerForRestore(
            new PlayerSessionId("s1"), "veteran", out _, out _, out PersistedPlayerInventory? inventory));

        Assert.Null(inventory);
        Assert.Equal(0, world.InventoryLoads);
    }

    [Fact]
    public void DepartedProfiles_AreEvictedOldestFirst_WithTheirInventory()
    {
        Rig rig = new(new FakeWorld(), new FakeProfiles());
        rig.Bridge.MaxDepartedProfiles = 1;
        foreach (string name in new[] { "a", "b" })
        {
            rig.Bridge.TryLoadPlayerForRestore(new PlayerSessionId(name), name, out _, out _, out _);
        }

        rig.Bridge.MarkProfileDeparted("a");
        rig.Bridge.MarkProfileDeparted("b");

        Assert.Equal(1, rig.Bridge.DepartedProfileCount);
        Assert.Equal(1, rig.Bridge.ProfileCacheCount);
        Assert.Equal(["a"], rig.Host.EvictedInventories);
    }

    [Fact]
    public void PersistPlayerProfile_SkipsAnUnrestoredSession()
    {
        FakeProfiles profiles = new();
        Rig rig = new(new FakeWorld(), profiles);
        PlayerSessionId session = new("s1");
        rig.Host.Sessions.Register(session, actorId: 5, isLocal: false, displayName: "half-joined");
        rig.Bridge.MarkSessionUnrestored(session);
        PlayerCombatant combatant = new(() => Vector3.Zero, actorId: 5);

        rig.Bridge.PersistPlayerProfile(session, combatant);
        Assert.Empty(profiles.Saved);

        rig.Bridge.ForgetUnrestoredSession(session);
        rig.Bridge.PersistPlayerProfile(session, combatant);
        Assert.Equal(["half-joined"], profiles.Saved.Select(saved => saved.PlayerName));
    }

    [Fact]
    public void PersistPlayerWorldState_SavesTheLeavingPlayersState_AndSkipsAnUnrestoredSession()
    {
        FakeWorld world = new();
        Rig rig = new(world, new FakeProfiles());
        PlayerSessionId session = new("s1");
        rig.Host.Sessions.Register(session, actorId: 5, isLocal: false, displayName: "bob");
        rig.Bridge.TryLoadPlayerForRestore(session, "bob", out _, out _, out _);
        rig.Host.PlayerStates[session] = (new Vector3(3f, 1.5f, 4f), Vector3.UnitX, 42, 100);

        rig.Bridge.MarkSessionUnrestored(session);
        rig.Bridge.PersistPlayerWorldState(session);
        Assert.Empty(world.SavedWorldStates);

        rig.Bridge.ForgetUnrestoredSession(session);
        Assert.Equal(0, rig.Bridge.UnrestoredPlayerSessionCount);
        rig.Bridge.PersistPlayerWorldState(session);
        PersistedPlayerWorldState saved = Assert.Single(world.SavedWorldStates);
        Assert.Equal(("bob", new Vector3(3f, 1.5f, 4f), Vector3.UnitX, 42), (saved.PlayerName, saved.Position, saved.Facing, saved.CurrentHealth));
    }

    // A join whose read predates the leave's queued write (a reconnect) still gets the state the
    // player left with; once used, the row is authoritative again.
    [Fact]
    public void TryLoadPlayerForRestore_PrefersTheWorldStateSavedOnLeave_OverAnOlderRow()
    {
        FakeWorld world = new();
        Rig rig = new(world, new FakeProfiles());
        PlayerSessionId session = new("s1");
        rig.Host.Sessions.Register(session, actorId: 5, isLocal: false, displayName: "bob");
        rig.Bridge.TryLoadPlayerForRestore(session, "bob", out _, out _, out _);
        rig.Host.PlayerStates[session] = (new Vector3(3f, 1.5f, 4f), Vector3.UnitX, 42, 100);
        rig.Bridge.PersistPlayerWorldState(session);
        world.StoredWorldState = new PersistedPlayerWorldState("bob", Vector3.Zero, Vector3.UnitZ, 100, 0, DateTime.UtcNow.AddMinutes(-1));

        rig.Bridge.TryLoadPlayerForRestore(new PlayerSessionId("s2"), "bob", out _, out PersistedPlayerWorldState? rejoined, out _);
        Assert.Equal(42, rejoined?.CurrentHealth);
        Assert.Equal(new Vector3(3f, 1.5f, 4f), rejoined?.Position);

        rig.Bridge.TryLoadPlayerForRestore(new PlayerSessionId("s3"), "bob", out _, out PersistedPlayerWorldState? again, out _);
        Assert.Equal(100, again?.CurrentHealth);
    }

    [Fact]
    public void TheWorldStateSavedOnLeave_IsEvictedWithTheDepartedProfile()
    {
        FakeWorld world = new();
        Rig rig = new(world, new FakeProfiles());
        rig.Bridge.MaxDepartedProfiles = 1;
        foreach (string name in new[] { "a", "b" })
        {
            PlayerSessionId session = new(name);
            rig.Host.Sessions.Register(session, actorId: name == "a" ? 5 : 6, isLocal: false, displayName: name);
            rig.Bridge.TryLoadPlayerForRestore(session, name, out _, out _, out _);
            rig.Host.PlayerStates[session] = (new Vector3(3f, 1.5f, 4f), Vector3.UnitX, 42, 100);
            rig.Bridge.PersistPlayerWorldState(session);
            Assert.True(rig.Host.Sessions.TryGet(session, out ConnectedPlayer? player));
            player!.MarkDespawned();
        }

        rig.Bridge.MarkProfileDeparted("a");
        rig.Bridge.MarkProfileDeparted("b");

        // "a" was evicted: its join reads the row (here the older one) again.
        world.StoredWorldState = new PersistedPlayerWorldState("a", Vector3.Zero, Vector3.UnitZ, 100, 0, DateTime.UtcNow);
        rig.Bridge.TryLoadPlayerForRestore(new PlayerSessionId("a2"), "a", out _, out PersistedPlayerWorldState? evicted, out _);
        Assert.Equal(100, evicted?.CurrentHealth);
        rig.Bridge.TryLoadPlayerForRestore(new PlayerSessionId("b2"), "b", out _, out PersistedPlayerWorldState? kept, out _);
        Assert.Equal(42, kept?.CurrentHealth);
    }

    [Fact]
    public void Checkpoints_FirstCaptureIsFull_AndAnIdleIncrementalCaptureIsNull()
    {
        Rig rig = new(new FakeWorld(), new FakeProfiles());
        rig.Bridge.RestoreWorld();
        rig.Host.InitialWolfSpawns = 0;

        PersistenceCheckpoint? first = rig.Bridge.CapturePersistenceCheckpoint(full: false);
        PersistenceCheckpoint? idle = rig.Bridge.CapturePersistenceCheckpoint(full: false);

        Assert.NotNull(first);
        Assert.False(first!.IsIncremental);
        Assert.Null(idle);
        Assert.Equal(1, rig.Bridge.FullCheckpointCaptures);
        Assert.Equal(1, rig.Bridge.IncrementalCheckpointCaptures);
    }

    [Fact]
    public void Checkpoints_CaptureConnectedPlayersThroughTheHost()
    {
        Rig rig = new(new FakeWorld(), new FakeProfiles());
        PlayerSessionId session = new("s1");
        rig.Host.Sessions.Register(session, actorId: 5, isLocal: false, displayName: "runner");
        rig.Host.PlayerStates[session] = (new Vector3(4, 2, 4), Vector3.UnitX, 42, 100);

        PersistenceCheckpoint checkpoint = rig.Bridge.CapturePersistenceCheckpoint(full: true)!;

        PersistedPlayerWorldState state = Assert.Single(checkpoint.PlayerWorldStates);
        Assert.Equal(("runner", new Vector3(4, 2, 4), 42), (state.PlayerName, state.Position, state.CurrentHealth));
        PlayerCheckpointVitals vitals = Assert.Single(checkpoint.PlayerVitals);
        Assert.Equal((42, 100), (vitals.CurrentHealth, vitals.MaxHealth));
    }

    [Fact]
    public void FinalCheckpoint_InlineQueueWritesItSynchronously_AndIsSkippedWhenDisabled()
    {
        FakeCheckpointWriter writer = new();
        Rig disabled = new(new FakeWorld(), new FakeProfiles(), writer);
        disabled.Bridge.WriteCheckpointOnDispose = false;
        Assert.Null(disabled.Bridge.WriteFinalCheckpoint());
        Assert.Empty(writer.Written);

        Rig rig = new(new FakeWorld(), new FakeProfiles(), writer);
        Assert.Null(rig.Bridge.WriteFinalCheckpoint());
        PersistenceCheckpoint written = Assert.Single(writer.Written);
        Assert.False(written.IsIncremental);
    }

    [Fact]
    public void FinalCheckpoint_BackgroundQueueQueuesItBehindTheWrites_AndWritesItOnce()
    {
        FakeCheckpointWriter writer = new();
        PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        Rig rig = new(new FakeWorld(), new FakeProfiles(), writer, writes);

        PendingFinalCheckpoint? final = rig.Bridge.WriteFinalCheckpoint();
        writes.Dispose();
        final?.WriteIfDropped(writes);

        Assert.NotNull(final);
        Assert.Single(writer.Written);
    }

    // PersistFracturePieces reads only the fractured cell (Map.CaptureFracturePiecesInto(cell, list))
    // instead of filtering a whole-map scan; it must save exactly the rows the filtered scan yields.
    [Fact]
    public void PersistFracturePieces_SavesTheSameRowsAsAFilteredWholeMapScan()
    {
        FakeWorld world = new();
        Rig rig = new(world);
        Map map = rig.Host.Map;
        GeometryCellKey fractured = new(4, 2, 4);
        foreach (GeometryCellKey other in new[] { new GeometryCellKey(2, 2, 2), new GeometryCellKey(6, 3, 6), new GeometryCellKey(8, 2, 3) })
        {
            Assert.True(map.RestoreFracturePieces(
                other,
                GeometryMaterialId.Grass,
                [new GeometryFracturePiece(new Vector3(other.X + 0.5f, other.Y + 0.5f, other.Z + 0.5f), 0.4f)],
                withPhysics: false));
        }

        Assert.True(map.RestoreFracturePieces(
            fractured,
            GeometryMaterialId.Grass,
            [
                new GeometryFracturePiece(new Vector3(4.25f, 2.25f, 4.25f), 0.5f),
                new GeometryFracturePiece(new Vector3(4.75f, 2.25f, 4.25f), 0.45f),
                new GeometryFracturePiece(new Vector3(4.5f, 2.75f, 4.75f), 0.3f)
            ],
            withPhysics: false));
        Cube cube = map.TryAddCube(new Vector3(1, 1, 1))!;
        WorldObjectRecord record = rig.WorldObjects.RegisterCube(cube, map, maxHealth: 100, currentHealth: 0, actorId: 42, ownerPlayerName: "builder");

        rig.Bridge.PersistFracturePieces(record, fractured);

        var expected = map.CaptureFracturePieces()
            .Where(piece => piece.Cell == fractured)
            .Select(piece => (piece.PieceIndex, "builder", new Vector3(piece.Cell.X, piece.Cell.Y, piece.Cell.Z), piece.Piece.Center, piece.Piece.SideLength, piece.MaterialId))
            .ToArray();
        Assert.Equal(3, expected.Length);
        Assert.Equal(
            expected,
            world.Pieces.Select(piece => (piece.PieceIndex, piece.OwnerPlayerName, piece.ParentCell, piece.Center, piece.SideLength, piece.MaterialId)));
        // The origin took the first id, and every piece its own after it.
        Assert.All(world.Pieces, piece => Assert.Equal(new ObjectId(1), piece.OriginObjectId));
        Assert.Equal([new ObjectId(2), new ObjectId(3), new ObjectId(4)], world.Pieces.Select(piece => piece.ObjectId));
    }

    private static PersistedEnemyInstance Enemy(int id)
    {
        return new PersistedEnemyInstance(
            new ObjectId(id),
            "wolf",
            "Wolf",
            new Vector3(5, 1, 5),
            Vector3.UnitZ,
            new PersistedCreatureStats(40, 40, 8, 10, 10, 0, 3f, 1f, 1, 0),
            Now,
            Now);
    }

    private sealed class Rig
    {
        public Rig(
            FakeWorld? world,
            FakeProfiles? profiles = null,
            FakeCheckpointWriter? writer = null,
            PersistenceWriteQueue? writes = null)
        {
            Host = new FakeHost(new Map(generateStartupTerrain: true, width: 12, depth: 12, headless: true));
            ObjectIds = new ObjectIdAllocator();
            Identities = new ObjectIdentityMap();
            Bridge = new RuntimePersistenceBridge(
                Host,
                profiles,
                world,
                writer ?? (world != null ? new FakeCheckpointWriter() : null),
                writes ?? PersistenceWriteQueue.Inline(),
                WorldObjects,
                Enemies,
                new SceneEventHub(),
                ObjectIds,
                Identities);
        }

        public ObjectIdAllocator ObjectIds { get; }

        public ObjectIdentityMap Identities { get; }

        public FakeHost Host { get; }

        public WorldObjectRegistry WorldObjects { get; } = new();

        public EnemyRegistry Enemies { get; } = new();

        public RuntimePersistenceBridge Bridge { get; }
    }

    private sealed class FakeHost(Map map) : IRuntimePersistenceHost
    {
        public Map Map { get; } = map;

        public PlayerSessionRegistry Sessions { get; } = new();

        public bool IsDisposed { get; set; }

        public int NextEnemyId { get; set; } = 3000;

        public int InitialWolfSpawns { get; set; }


        public HashSet<string> LoadedInventories { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> EvictedInventories { get; } = [];

        public Dictionary<PlayerSessionId, (Vector3 Position, Vector3 Facing, int Health, int MaxHealth)> PlayerStates { get; } = [];

        public PlayerInteractionMode GetSessionInteractionMode(PlayerSessionId sessionId) => PlayerInteractionMode.Building;

        public bool TryGetPlayerCheckpointState(
            PlayerSessionId sessionId,
            out Vector3 position,
            out Vector3 facing,
            out int currentHealth,
            out int maxHealth)
        {
            bool found = PlayerStates.TryGetValue(sessionId, out var state);
            (position, facing, currentHealth, maxHealth) = state;
            return found;
        }

        public bool TryAllocateEnemyActorId(out int actorId)
        {
            actorId = NextEnemyId++;
            return true;
        }

        public void SpawnInitialWolf() => InitialWolfSpawns++;

        public bool IsPlayerInventoryLoaded(string playerName) => LoadedInventories.Contains(playerName);

        public void EvictPlayerInventory(string playerName) => EvictedInventories.Add(playerName);
    }

    private sealed class FakeProfiles : IPlayerProfileRepository
    {
        private readonly Dictionary<string, PersistedPlayerProfile> _rows = new(StringComparer.OrdinalIgnoreCase);

        public List<PersistedPlayerProfile> Saved { get; } = [];

        public int GetOrCreateCalls { get; private set; }

        public PersistedPlayerProfile GetOrCreate(string playerName)
        {
            GetOrCreateCalls++;
            if (!_rows.TryGetValue(playerName, out PersistedPlayerProfile profile))
            {
                profile = PersistedPlayerProfile.CreateDefault(playerName, Now);
                _rows[playerName] = profile;
            }

            return profile;
        }

        public PersistedPlayerProfile? TryGet(string playerName) =>
            _rows.TryGetValue(playerName, out PersistedPlayerProfile profile) ? profile : null;

        public void Save(PersistedPlayerProfile profile)
        {
            Saved.Add(profile);
            _rows[profile.PlayerName] = profile;
        }
    }

    private sealed class FakeCheckpointWriter : IPersistenceCheckpointWriter
    {
        public List<PersistenceCheckpoint> Written { get; } = [];

        public void Write(PersistenceCheckpoint checkpoint)
        {
            lock (Written)
            {
                Written.Add(checkpoint);
            }
        }
    }

    private sealed class FakeWorld : IWorldPersistenceRepository
    {
        public List<PersistedPlacedCube> Cubes { get; } = [];

        public List<PersistedEnemyInstance> Enemies { get; } = [];


        public int InventoryLoads { get; private set; }

        public List<PersistedPlayerWorldState> SavedWorldStates { get; } = [];

        // What TryGetPlayerWorldState returns (a row a read made before the latest save).
        public PersistedPlayerWorldState? StoredWorldState { get; set; }

        public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName) => StoredWorldState;

        public void SavePlayerWorldState(PersistedPlayerWorldState state) => SavedWorldStates.Add(state);

        public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes() => Cubes;

        public void SavePlacedCube(PersistedPlacedCube cube) => Cubes.Add(cube);

        public void DeletePlacedCube(ObjectId objectId) => Cubes.RemoveAll(cube => (int)cube.ObjectId.Value == (int)objectId.Value);

        public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces() => [];

        public List<PersistedCubeFracturePiece> Pieces { get; } = [];

        public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece) => Pieces.Add(piece);

        public void DeleteCubeFracturePieces(ObjectId originObjectId)
        {
        }

        public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies() => Enemies;

        public void SaveEnemyInstance(PersistedEnemyInstance enemy) => Enemies.Add(enemy);

        public void DeleteEnemyInstance(ObjectId objectId) => Enemies.RemoveAll(enemy => (int)enemy.ObjectId.Value == (int)objectId.Value);

        public long LoadNextObjectId() => 1;

        // Terrain overrides are not exercised by this fake.
        public IReadOnlyList<PersistedTerrainCellOverride> LoadTerrainCellOverrides() => [];

        public void SaveTerrainCellOverride(PersistedTerrainCellOverride cellOverride)
        {
        }


        public IReadOnlyList<PersistedItemInstance> LoadItemInstances() => [];

        public void SaveItemInstance(PersistedItemInstance item)
        {
        }

        public void DeleteItemInstance(long itemInstanceId)
        {
        }

        public IReadOnlyList<PersistedInventorySlot> LoadInventorySlots() => [];

        public PersistedPlayerInventory LoadPlayerInventory(string playerName)
        {
            InventoryLoads++;
            return PersistedPlayerInventory.Empty;
        }

        public void SaveInventorySlot(PersistedInventorySlot slot)
        {
        }

        public void DeleteInventorySlots(string playerName)
        {
        }

        public void DeleteInventorySlot(string playerName, int slotIndex, long expectedItemInstanceId)
        {
        }

        public void DestroyInventoryItem(string playerName, long itemInstanceId)
        {
        }

        public void SaveStarterKitGrant(string playerName, int kitVersion)
        {
        }
    }
}
