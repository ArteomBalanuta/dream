using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Repositories;
using Dream.Simulation.Characters;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server;
using Dream.Tests.Server.Persistence;
using Xunit;

namespace Dream.Tests.Support;

// The production join path with accounts wired: a deferred DedicatedServerHost over a real SQLite database,
// the router the dedicated server uses and an InMemoryAccountRepository (counting its lookups). Time is
// manual, ticks are driven by the test, and every reply is collected per connection.
internal sealed class AccountHostHarness : IDisposable
{
    private readonly bool _ownsDatabase;
    private readonly Dictionary<string, List<IServerToClientMessage>> _replies = new(StringComparer.Ordinal);

    // The real thing: the production host over the bundle's own SqliteAccountRepository, in the given world
    // (null: the default world). wrap decorates the repository, to fail a write on purpose. Accounts (the
    // in-memory stand-in) is not wired here; read ownership through Sqlite or the database. Disposing the
    // harness releases the world, so a database can be hosted again world after world.
    public static AccountHostHarness OverSqlite(
        PersistenceTestDatabase? database = null,
        string? world = null,
        Func<IAccountRepository, IAccountRepository>? wrap = null) =>
        new(database, world, wrap);

    private AccountHostHarness(PersistenceTestDatabase? database, string? world, Func<IAccountRepository, IAccountRepository>? wrap)
    {
        _ownsDatabase = database == null;
        Database = database ?? new PersistenceTestDatabase();
        Accounts = new CountingAccountRepository();
        Clock = new ServerConnectionTestFixtures.ManualTimeProvider();
        Sqlite = new SqliteAccountRepository(Database.Schema);
        ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(
            Database.DatabasePath,
            Database.SchemaPath,
            start: new ServerStartOptions(WorldSelector: world));
        Assert.IsType<SqliteAccountRepository>(bundle.Accounts);
        Host = ServerBootstrap.CreateAuthoritativeHost(wrap == null ? bundle : WithAccounts(bundle, wrap(bundle.Accounts!)));
        Host.TimeProvider = Clock;
        RuntimeHost = null!;
    }

    public AccountHostHarness(PersistenceTestDatabase? database = null, CountingAccountRepository? accounts = null)
    {
        _ownsDatabase = database == null;
        Database = database ?? new PersistenceTestDatabase();
        Accounts = accounts ?? new CountingAccountRepository();
        Clock = new ServerConnectionTestFixtures.ManualTimeProvider();
        ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(Database.DatabasePath, Database.SchemaPath);
        RuntimePersistencePorts ports = ServerBootstrap.CreateRuntimePersistencePorts(bundle)!;
        ports = ports with { Accounts = Accounts };
        RuntimeHost = RuntimeAuthoritativeSessionHost.CreateDefault(ports);
        RuntimeAuthoritativeSessionHost runtimeHost = RuntimeHost;
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler(),
                new WorldResyncRequestHandler()
            ],
            Array.Empty<IClientMessageHandler>());
        Host = new DedicatedServerHost(runtimeHost, router, deferJoinsToTick: true) { TimeProvider = Clock };
    }

    // The host the dedicated and integrated servers run (ServerBootstrap.CreateAuthoritativeHost), over a
    // bundle that carries the given account repository (null: a host without accounts).
    public static DedicatedServerHost CreateProductionHost(PersistenceTestDatabase database, IAccountRepository? accounts)
    {
        ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, database.SchemaPath);
        return ServerBootstrap.CreateAuthoritativeHost(WithAccounts(bundle, accounts));
    }

    private static ServerPersistenceBundle WithAccounts(ServerPersistenceBundle bundle, IAccountRepository? accounts) =>
        new(
            bundle.Schema,
            bundle.PlayerProfiles,
            bundle.World,
            bundle.CombatFormulas,
            bundle.FormulaService,
            bundle.SimulationSeed,
            bundle.WorldTimeTicks,
            bundle.WorldId,
            bundle.Descriptor,
            bundle.TerrainChunks,
            bundle.StoredTerrain,
            bundle.TerrainPayload,
            bundle.HostLease,
            accounts);

    public PersistenceTestDatabase Database { get; }

    public CountingAccountRepository Accounts { get; }

    // OverSqlite only: the database's accounts, for the test's own reads.
    public SqliteAccountRepository? Sqlite { get; }

    public RuntimeAuthoritativeSessionHost RuntimeHost { get; }

    public DedicatedServerHost Host { get; }

    public ServerConnectionTestFixtures.ManualTimeProvider Clock { get; }

    public WorldSessionRuntime Runtime => Host.Runtime!;

    public IReadOnlyList<IServerToClientMessage> RepliesOf(string connectionId) =>
        _replies.TryGetValue(connectionId, out List<IServerToClientMessage>? list) ? list : [];

    public void ClearReplies() => _replies.Clear();

    // Sends a join (token from TestAccountTokens when none is given) without ticking; replies that came back
    // at once (a refusal before the queue) are recorded.
    public void SendJoin(string connectionId, string sessionId, string? name, string? token)
    {
        IReadOnlyList<IServerToClientMessage> immediate = Host.HandleClientMessage(
            connectionId,
            new ClientJoinRequestMessage(sessionId, name!, token));
        Record(connectionId, immediate);
    }

    // Flushes the persistence queue, runs one tick and records the replies that came out of it.
    public void Tick()
    {
        Assert.True(Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        int executed = Host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        if (executed == 0)
        {
            executed = Host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 0.01);
        }

        Assert.Equal(1, executed);
        List<PendingConnectionReply> replies = [];
        Host.DrainPendingJoinRepliesInto(replies);
        foreach (PendingConnectionReply reply in replies)
        {
            Record(reply.ConnectionId, [reply.Message]);
        }

        Host.DrainPendingActionResults();
        Assert.True(Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
    }

    // Sends a join and ticks until the connection has a reply (at most maxTicks); returns it.
    public IServerToClientMessage Join(string connectionId, string sessionId, string? name, string? token, int maxTicks = 8)
    {
        int before = RepliesOf(connectionId).Count;
        SendJoin(connectionId, sessionId, name, token);
        for (int i = 0; i < maxTicks && RepliesOf(connectionId).Count == before; i++)
        {
            Tick();
        }

        IReadOnlyList<IServerToClientMessage> replies = RepliesOf(connectionId);
        Assert.True(replies.Count > before, $"{connectionId} never got a join reply.");
        return replies[^1];
    }

    public string JoinRejectionOf(string connectionId, string sessionId, string? name, string? token) =>
        Assert.IsType<JoinRejectedMessage>(Join(connectionId, sessionId, name, token)).Reason;

    public void JoinAccepted(string connectionId, string sessionId, string name, string? token = null) =>
        Assert.IsType<JoinAcceptedMessage>(Join(connectionId, sessionId, name, token ?? TestAccountTokens.For(name)));

    public void Leave(string connectionId)
    {
        Host.ReleaseConnection(connectionId);
        Tick();
    }

    private void Record(string connectionId, IEnumerable<IServerToClientMessage> messages)
    {
        if (!_replies.TryGetValue(connectionId, out List<IServerToClientMessage>? list))
        {
            list = [];
            _replies[connectionId] = list;
        }

        list.AddRange(messages);
    }

    public void Dispose()
    {
        Host.Dispose();
        if (_ownsDatabase)
        {
            Database.Dispose();
        }
    }
}

// InMemoryAccountRepository that counts the lookups it served (a refused join must start none).
internal sealed class CountingAccountRepository : IAccountRepository
{
    private readonly InMemoryAccountRepository _inner = new();
    private int _lookups;

    public int LookupCalls => Volatile.Read(ref _lookups);

    public int AccountCount => _inner.AccountCount;

    public int RecordJoinCalls => _inner.RecordJoinCalls;

    private readonly HashSet<string> _known = new(StringComparer.Ordinal);

    public void AddCharacter(string key, string displayName, AccountKey? owner = null)
    {
        lock (_known)
        {
            _known.Add(key);
        }

        _inner.AddCharacter(key, displayName, owner);
    }

    public AccountKey? OwnerOf(string key) => _inner.OwnerOf(key);

    public CharacterLookup Lookup(string trimmedName, AccountKey? account)
    {
        Interlocked.Increment(ref _lookups);
        return _inner.Lookup(trimmedName, account);
    }

    // The SQLite repository claims the profile row the join's save created; here that row is added when a
    // claim names a character nobody listed (a new character).
    public bool RecordJoin(AccountKey account, CharacterKey character, bool claim, DateTime utcNow)
    {
        bool created;
        lock (_known)
        {
            created = claim && _known.Add(character.Value);
        }

        if (created)
        {
            _inner.AddCharacter(character.Value, character.Value, null);
        }

        return _inner.RecordJoin(account, character, claim, utcNow);
    }
}
