using System.Diagnostics;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Server.Persistence;

// SCALE-3: a join no longer flushes the write queue or reads the database on the tick. The receive
// thread prefetches the joining player's profile and world state on the persistence worker (ordered
// behind the queued writes), and the tick applies the cached result.
public sealed class JoinPersistencePrefetchTests
{
    private const string PlayerName = "Player A";

    [Fact]
    public void DeferredJoin_DoesNoDatabaseWorkOnTheTickThread()
    {
        using PersistenceTestDatabase database = new();
        ThreadRecordingProfiles profiles = new(new SqlitePlayerProfileRepository(database.Schema));
        ThreadRecordingWorld world = new(new SqliteWorldPersistenceRepository(database.Schema), profiles);
        using DedicatedServerHost host = CreateHost(profiles, world);
        profiles.Reset();

        RunOnReceiveThread(() => Assert.Empty(host.HandleClientMessage("connection-a", new ClientJoinRequestMessage("session-a", PlayerName))));
        RunOnReceiveThread(() => Assert.Empty(host.HandleClientMessage("connection-b", new ClientJoinRequestMessage("session-b", "Player B"))));

        // A long write is in progress on the worker; the old join path flushed the queue here.
        using ManualResetEventSlim gate = new(initialState: false);
        host.Runtime!.PersistenceWrites!.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));
        Stopwatch tick = Stopwatch.StartNew();
        profiles.TickThreadId = Environment.CurrentManagedThreadId;
        try
        {
            Assert.Equal(1, AdvanceOneTick(host));
        }
        finally
        {
            profiles.TickThreadId = -1;
            gate.Set();
        }

        tick.Stop();
        Assert.Empty(profiles.TickThreadCalls);
        Assert.True(tick.Elapsed < TimeSpan.FromSeconds(2), $"The join tick took {tick.Elapsed.TotalMilliseconds:0} ms.");
        List<PendingConnectionReply> replies = [];
        host.DrainPendingJoinRepliesInto(replies);
        Assert.Equal(2, replies.Count);
        Assert.All(replies, reply => Assert.IsType<JoinAcceptedMessage>(reply.Message));
        Assert.True(profiles.Calls > 0, "The prefetch never read the profiles.");
    }

    [Fact]
    public void Join_WhileAProfileWriteIsStillQueued_RestoresTheQueuedState()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository repository = new(database.Schema);
        repository.Save(PersistenceGroupCommitTests.Profile(PlayerName, experience: 3, level: 1));
        using DedicatedServerHost host = CreateHost(repository, new SqliteWorldPersistenceRepository(database.Schema));
        PersistenceWriteQueue writes = host.Runtime!.PersistenceWrites!;
        using ManualResetEventSlim gate = new(initialState: false);

        // The newer profile is still waiting behind a slow write when the join request arrives.
        writes.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));
        writes.Enqueue(() => repository.Save(PersistenceGroupCommitTests.Profile(PlayerName, experience: 40, level: 6, PlayerInteractionMode.Combat)));
        ReceiveThread receive = StartReceiveThread(() => host.HandleClientMessage("connection-a", new ClientJoinRequestMessage("session-a", PlayerName)));
        Assert.False(receive.Join(TimeSpan.FromMilliseconds(100)), "The join did not wait for the queued profile write.");
        gate.Set();
        Assert.True(receive.Join(TimeSpan.FromSeconds(30)));

        AdvanceOneTick(host);

        Assert.Equal("session-a", host.Runtime.PrimaryRemoteSessionId?.Value);
        Assert.Equal(6, host.Runtime.Player.Combatant.Level);
        Assert.Equal(40, host.Runtime.Player.Combatant.Experience);
        Assert.Equal(PlayerInteractionMode.Combat, host.Runtime.InteractionMode);
    }

    [Fact]
    public void Rejoin_RequestedBeforeTheOldConnectionIsReleased_RestoresTheStateSavedAtRelease()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository repository = new(database.Schema);
        using DedicatedServerHost host = CreateHost(repository, new SqliteWorldPersistenceRepository(database.Schema));
        WorldSessionRuntime runtime = host.Runtime!;
        RunOnReceiveThread(() => host.HandleClientMessage("connection-1", new ClientJoinRequestMessage("session-a", PlayerName)));
        AdvanceOneTick(host);
        Assert.Equal("session-a", runtime.PrimaryRemoteSessionId?.Value);
        runtime.Player.Combatant.ApplyProgression(4, 25);

        // The reconnect's request arrives (and its prefetch reads the database) while the old
        // connection still holds the session; the old one is released, saving level 4, before the tick.
        RunOnReceiveThread(() => host.HandleClientMessage("connection-2", new ClientJoinRequestMessage("session-a", PlayerName)));
        host.ReleaseConnection("connection-1");
        AdvanceOneTick(host);

        List<PendingConnectionReply> replies = [];
        host.DrainPendingJoinRepliesInto(replies);
        Assert.Contains(replies, reply => reply.ConnectionId == "connection-2" && reply.Message is JoinAcceptedMessage);
        Assert.Equal("session-a", runtime.PrimaryRemoteSessionId?.Value);
        Assert.Equal(4, runtime.Player.Combatant.Level);
        Assert.Equal(25, runtime.Player.Combatant.Experience);
    }

    [Fact]
    public void Join_WhosePrefetchIsNotFinished_WaitsForALaterTick_WithoutBlockingTheTick()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository repository = new(database.Schema);
        repository.Save(PersistenceGroupCommitTests.Profile(PlayerName, experience: 9, level: 3));
        using DedicatedServerHost host = CreateHost(repository, new SqliteWorldPersistenceRepository(database.Schema));
        WorldSessionRuntime runtime = host.Runtime!;
        RunOnReceiveThread(() => host.HandleClientMessage("connection-a", new ClientJoinRequestMessage("session-a", PlayerName)));
        // Lose the receive thread's load (as when another connection's join consumed it) and stall
        // the worker, so the tick has to start a new load that cannot finish yet.
        runtime.DiscardPersistedPlayerPrefetch(PlayerName);
        using ManualResetEventSlim gate = new(initialState: false);
        runtime.PersistenceWrites!.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));

        List<PendingConnectionReply> replies = [];
        try
        {
            for (int i = 0; i < 3; i++)
            {
                Stopwatch tick = Stopwatch.StartNew();
                AdvanceOneTick(host);
                Assert.True(tick.Elapsed < TimeSpan.FromSeconds(2), $"Tick {i} waited {tick.Elapsed.TotalMilliseconds:0} ms for the database.");
                host.DrainPendingJoinRepliesInto(replies);
                Assert.Empty(replies);
                Assert.Null(runtime.PrimaryRemoteSessionId);
            }
        }
        finally
        {
            gate.Set();
        }

        Assert.True(runtime.PersistenceWrites.Flush(TimeSpan.FromSeconds(30)));
        AdvanceOneTick(host);
        host.DrainPendingJoinRepliesInto(replies);
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(replies).Message);
        Assert.Equal(3, runtime.Player.Combatant.Level);
    }

    [Fact]
    public void PrefetchedJoin_LeavesNoLoadBehind()
    {
        using PersistenceTestDatabase database = new();
        using DedicatedServerHost host = CreateHost(
            new SqlitePlayerProfileRepository(database.Schema),
            new SqliteWorldPersistenceRepository(database.Schema));
        WorldSessionRuntime runtime = host.Runtime!;

        RunOnReceiveThread(() => host.HandleClientMessage("connection-a", new ClientJoinRequestMessage("session-a", $"  {PlayerName} ")));
        Assert.True(runtime.IsPersistedPlayerReady(PlayerName, startIfMissing: false));
        AdvanceOneTick(host);

        Assert.Equal("session-a", runtime.PrimaryRemoteSessionId?.Value);
        Assert.False(runtime.IsPersistedPlayerReady(PlayerName, startIfMissing: false));
    }

    private static DedicatedServerHost CreateHost(IPlayerProfileRepository profiles, IWorldPersistenceRepository world)
    {
        WorldSessionRuntime runtime = new(
            12,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: profiles,
            worldPersistence: world,
            persistenceWrites: PersistenceWriteQueue.Background());
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler(),
                new WorldResyncRequestHandler()
            ],
            Array.Empty<IClientMessageHandler>());
        return new DedicatedServerHost(new RuntimeAuthoritativeSessionHost(runtime), router, deferJoinsToTick: true);
    }

    private static int AdvanceOneTick(DedicatedServerHost host)
    {
        int executed = host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        return executed != 0 ? executed : host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 0.01);
    }

    private static void RunOnReceiveThread(Action action)
    {
        ReceiveThread thread = StartReceiveThread(action);
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The receive thread did not finish.");
    }

    private static ReceiveThread StartReceiveThread(Action action) => ReceiveThread.Start(action);

    // A dedicated thread standing in for a connection's receive loop; Join rethrows its failure.
    private sealed class ReceiveThread
    {
        private readonly Thread _thread;
        private Exception? _failure;

        private ReceiveThread(Action action)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _failure = ex;
                }
            })
            {
                IsBackground = true,
                Name = "test-receive"
            };
        }

        public static ReceiveThread Start(Action action)
        {
            ReceiveThread thread = new(action);
            thread._thread.Start();
            return thread;
        }

        public bool Join(TimeSpan timeout)
        {
            if (!_thread.Join(timeout))
            {
                return false;
            }

            if (_failure != null)
            {
                throw new InvalidOperationException("The receive thread failed.", _failure);
            }

            return true;
        }
    }

    // Records every repository call made on the tick thread while a tick runs.
    private sealed class ThreadRecordingProfiles(IPlayerProfileRepository inner) : IPlayerProfileRepository
    {
        private int _calls;

        public volatile int TickThreadId = -1;

        public List<string> TickThreadCalls { get; } = [];

        public int Calls => Volatile.Read(ref _calls);

        public void Reset()
        {
            lock (TickThreadCalls)
            {
                TickThreadCalls.Clear();
            }

            Volatile.Write(ref _calls, 0);
        }

        public void Record(string call)
        {
            Interlocked.Increment(ref _calls);
            if (Environment.CurrentManagedThreadId == TickThreadId)
            {
                lock (TickThreadCalls)
                {
                    TickThreadCalls.Add(call);
                }
            }
        }

        public PersistedPlayerProfile GetOrCreate(string playerName)
        {
            Record($"GetOrCreate({playerName})");
            return inner.GetOrCreate(playerName);
        }

        public PersistedPlayerProfile? TryGet(string playerName)
        {
            Record($"TryGet({playerName})");
            return inner.TryGet(playerName);
        }

        public void Save(PersistedPlayerProfile profile)
        {
            Record($"Save({profile.PlayerName})");
            inner.Save(profile);
        }
    }

    private sealed class ThreadRecordingWorld(IWorldPersistenceRepository inner, ThreadRecordingProfiles recorder) : IWorldPersistenceRepository
    {
        public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName) => Call(nameof(TryGetPlayerWorldState), () => inner.TryGetPlayerWorldState(playerName));

        public void SavePlayerWorldState(PersistedPlayerWorldState state) => Call(nameof(SavePlayerWorldState), () => inner.SavePlayerWorldState(state));

        public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes() => Call(nameof(LoadPlacedCubes), inner.LoadPlacedCubes);

        public void SavePlacedCube(PersistedPlacedCube cube) => Call(nameof(SavePlacedCube), () => inner.SavePlacedCube(cube));

        public void DeletePlacedCube(int cubeId) => Call(nameof(DeletePlacedCube), () => inner.DeletePlacedCube(cubeId));

        public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces() => Call(nameof(LoadCubeFracturePieces), inner.LoadCubeFracturePieces);

        public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece) => Call(nameof(SaveCubeFracturePiece), () => inner.SaveCubeFracturePiece(piece));

        public void DeleteCubeFracturePieces(int sourceCubeId) => Call(nameof(DeleteCubeFracturePieces), () => inner.DeleteCubeFracturePieces(sourceCubeId));

        public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies() => Call(nameof(LoadActiveEnemies), inner.LoadActiveEnemies);

        public void SaveEnemyInstance(PersistedEnemyInstance enemy) => Call(nameof(SaveEnemyInstance), () => inner.SaveEnemyInstance(enemy));

        public void DeleteEnemyInstance(int enemyId) => Call(nameof(DeleteEnemyInstance), () => inner.DeleteEnemyInstance(enemyId));

        public IReadOnlyList<PersistedItemInstance> LoadItemInstances() => Call(nameof(LoadItemInstances), inner.LoadItemInstances);

        public void SaveItemInstance(PersistedItemInstance item) => Call(nameof(SaveItemInstance), () => inner.SaveItemInstance(item));

        public void DeleteItemInstance(long itemInstanceId) => Call(nameof(DeleteItemInstance), () => inner.DeleteItemInstance(itemInstanceId));

        public IReadOnlyList<PersistedInventorySlot> LoadInventorySlots() => Call(nameof(LoadInventorySlots), inner.LoadInventorySlots);

        public void SaveInventorySlot(PersistedInventorySlot slot) => Call(nameof(SaveInventorySlot), () => inner.SaveInventorySlot(slot));

        public void DeleteInventorySlots(string playerName) => Call(nameof(DeleteInventorySlots), () => inner.DeleteInventorySlots(playerName));

        private T Call<T>(string name, Func<T> call)
        {
            recorder.Record(name);
            return call();
        }

        private void Call(string name, Action call)
        {
            recorder.Record(name);
            call();
        }
    }
}
