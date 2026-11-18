using System.Reflection;
using System.Runtime.CompilerServices;
using OpenTK.Mathematics;
using Xunit;
using Dream.Application;
using Dream.Client.Input;
using Dream.Client.Networking;
using Dream.Client.Presentation;
using Dream.Client.Scenes;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui.Panels;
using Dream.Rendering.ViewModels;
using Dream.Server;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Support;
using Dream.Tests.Utilities;

namespace Dream.Tests.Integration;

// A real PlayScene with no GL: only Render, the sky and the frame loop are missing. It is fed by a real
// RemoteClientSession over TCP, so everything ApplyReplicatedMessage does (join, snapshot, action
// result, push) runs in production order, with the world-transfer hold of the real session in front of
// it. Update does not run, so nothing is predicted: commands are sent by the test.
internal sealed class HeadlessPlayScene : IAsyncDisposable
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly WireLoopbackServer _server;
    private readonly List<IServerToClientMessage> _applied = [];
    private ulong _commandSequence;
    private ulong _actionSequence;

    private HeadlessPlayScene(WireLoopbackServer server, RemoteClientSession session, ServerConnection connection, PlayScene scene)
    {
        _server = server;
        Session = session;
        Connection = connection;
        Scene = scene;
    }

    public RemoteClientSession Session { get; }

    public ServerConnection Connection { get; }

    public PlayScene Scene { get; }

    public string SessionId => Session.SessionId;

    // Every message the scene applied, in order.
    public IReadOnlyList<IServerToClientMessage> Applied => _applied;

    public ClientWorldSession World => Get<ClientWorldSession>("_runtime");

    public ClientDeathTracker Death => Get<ClientDeathTracker>("_deathTracker");

    public LootFeedbackSystem Loot => Get<LootFeedbackSystem>("_lootFeedback");

    public Dream.Client.Replication.ClientGroundItemCache GroundItems => Get<Dream.Client.Replication.ClientGroundItemCache>("_groundItems");

    public Dream.Client.Replication.ClientNpcCache Npcs => Get<Dream.Client.Replication.ClientNpcCache>("_npcs");

    public VendorHudState Vendor => Get<VendorHudState>("_vendorHud");

    public QuickbarHudState Quickbar => Get<QuickbarHudState>("_quickbarHud");

    public PlayerInventorySnapshot? Inventory =>
        (PlayerInventorySnapshot?)typeof(PlayScene).GetField("_inventory", Fields)!.GetValue(Scene);

    private T Get<T>(string field) => (T)typeof(PlayScene).GetField(field, Fields)!.GetValue(Scene)!;

    // Connects a real session and applies messages until the join was applied. pumpJoin false leaves
    // the join in the session's queue, to run what happens around it first.
    public static async Task<HeadlessPlayScene> JoinAsync(WireLoopbackServer server, string sessionId, bool pumpJoin = true)
    {
        Task<ServerConnection> accept = server.Service.AcceptOnThreadPoolAsync();
        string name = $"Name {sessionId}";
        RemoteClientSession session = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", server.Port, sessionId, name, "headless-scene", TestAccountTokens.For(name)));
        ServerConnection connection = await accept.WaitAsync(WireLoopbackServer.Timeout);
        HeadlessPlayScene client = new(server, session, connection, CreateScene(session));

        DateTime deadline = DateTime.UtcNow + WireLoopbackServer.Timeout;
        while (!server.Host.IsConnectionJoined(connection.ConnectionId))
        {
            Assert.True(DateTime.UtcNow < deadline, $"{sessionId} never joined");
            await Task.Delay(2);
            await server.TickAsync();
            Assert.True(await client.FlushAsync());
        }

        if (pumpJoin)
        {
            await client.PumpUntilAsync(c => c.Applied.Any(message => message is JoinAcceptedMessage), "the join");
        }

        return client;
    }

    // The constructor's objects, minus what needs GL. Fields with a plain initializer get a new instance,
    // so a field added to PlayScene later does not need this list.
    private static PlayScene CreateScene(RemoteClientSession session)
    {
        PlayScene scene = (PlayScene)RuntimeHelpers.GetUninitializedObject(typeof(PlayScene));
        SceneEventHub events = new();
        GameSettings settings = new(new Vector2i(1440, 1024));
        Set(scene, "_settings", settings);
        Set(scene, "_events", events);
        Set(scene, "_remoteClient", session);
        Set(scene, "_viewModelController", new PlayerViewModelController(ViewModelCatalog.CreateDefault(), settings));
        Set(scene, "_clientProtocol", ClientProtocolSession.CreateDefault());
        Set(scene, "_runtime", new ClientWorldSession(settings.FloorSize, new Vector3(5f, 1.5f, 5f), events));
        Set(scene, "_scenePresenter", new ClientScenePresenter(session.SessionId));
        Set(scene, "_combatFeedback", new CombatFeedbackSystem(events, new FloatingCombatTextSystem()));
        Set(scene, "_actionFeedback", new AuthoritativeActionFeedbackSystem(events));
        // An uninitialized object skips BindNewChat, and the fill below needs a parameterless constructor.
        Set(scene, "_chatInput", new ChatInput(new ChatHudState()));
        Set(scene, "_lastWorldResyncRequestSeconds", double.NegativeInfinity);
        Set(scene, "_snapshotLogKey", string.Empty);
        Set(scene, "_commandLogKey", string.Empty);
        Set(scene, "_presenterStateLogKey", string.Empty);
        foreach (FieldInfo field in typeof(PlayScene).GetFields(Fields))
        {
            if (field.GetValue(scene) == null
                && field.FieldType.IsClass
                && field.FieldType.Namespace?.StartsWith("Dream.", StringComparison.Ordinal) == true
                && (field.FieldType.Namespace == "Dream.Rendering.Hud" || field.FieldType.Namespace?.StartsWith("Dream.Rendering", StringComparison.Ordinal) != true)
                && Array.Find(field.FieldType.GetConstructors(), ctor => ctor.GetParameters().All(p => p.IsOptional)) is { } constructor)
            {
                field.SetValue(scene, constructor.Invoke([.. constructor.GetParameters().Select(p => p.DefaultValue)]));
            }
        }

        // M3: these live in a partial and have field initialisers, which an uninitialized object skips.
        // The fill loop above builds them by their parameterless constructors; a type that loses one
        // would be null here and fail every economy test far from the cause.
        foreach (string economyField in new[] { "_groundItems", "_npcs", "_vendorHud", "_quickbarHud" })
        {
            Assert.NotNull(typeof(PlayScene).GetField(economyField, Fields)!.GetValue(scene));
        }

        return scene;
    }

    private static void Set(object target, string name, object value) =>
        typeof(PlayScene).GetField(name, Fields)!.SetValue(target, value);

    public Task<bool> FlushAsync() => OutboundDrain.FlushAsync(Connection, WireLoopbackServer.Timeout).AsTask();

    // What PlayScene.DrainRemoteTransportMessages does once per frame.
    public void Pump()
    {
        while (Session.IsConnected && Session.TryDequeueInbound(out IServerToClientMessage? message) && message != null)
        {
            Scene.ApplyReplicatedMessage(message);
            _applied.Add(message);
        }
    }

    // Bounded by LoopbackDeadline rather than by a flat 30 s of wall clock, with the number of
    // applied server messages as the progress counter: the failure worth catching here is a session
    // that stops being told anything, which is exactly "nothing applied for the no-progress
    // window". A session that is merely slow because the suite is loaded keeps applying messages
    // and keeps this wait alive, up to LoopbackDeadline.HardTimeout. The connection check is
    // unchanged and still fails first when the session actually drops.
    public async Task PumpUntilAsync(Func<HeadlessPlayScene, bool> done, string what)
    {
        LoopbackDeadline deadline = new(Applied.Count);
        while (true)
        {
            Pump();
            if (done(this))
            {
                return;
            }

            Assert.True(Session.IsConnected, $"{SessionId} lost its session: {Session.DisconnectReason}");
            deadline.AssertAlive(Applied.Count, $"{what} on {SessionId}");
            await Task.Delay(1);
        }
    }

    public Task PumpToSnapshotAsync(ulong tick) => PumpUntilAsync(
        client => client.Applied.OfType<ServerSnapshotMessage>().Any(snapshot => snapshot.Snapshot.ServerTick >= tick),
        $"the snapshot of tick {tick}");

    // One command frame, read by the server before the caller ticks.
    public async Task SendCommandAsync(PlayerMoveIntent move, Vector3 facing, bool attack, PlayerInteractionMode mode = PlayerInteractionMode.Combat)
    {
        _commandSequence++;
        await Session.SendCommandFrameAsync(new PlayerCommandFrame(
            new PlayerSessionId(SessionId),
            _commandSequence,
            new PlayerModeSwitchIntent(mode),
            move,
            new PlayerLookIntent(0f, 0f, facing),
            PrimaryUsePressed: attack));
        ulong sequence = _commandSequence;
        await WaitReadAsync(context => context.LastCommandMessage?.CommandFrame.Sequence >= sequence);
    }

    public async Task<ulong> SendActionAsync(IPlayerActionPayload payload)
    {
        // Past the commands, as ClientActionSequenceTracker does: a use a command triggered spends its sequence.
        _actionSequence = Math.Max(_actionSequence, _commandSequence) + 1;
        await Session.SendPlayerActionAsync(new PlayerActionMessage(new PlayerSessionId(SessionId), _actionSequence, payload));
        ulong sequence = _actionSequence;
        await WaitReadAsync(context => context.LastActionMessage?.ActionSequence >= sequence);
        return sequence;
    }

    private async Task WaitReadAsync(Func<ServerRoutingContext, bool> read)
    {
        DateTime deadline = DateTime.UtcNow + WireLoopbackServer.Timeout;
        while (!_server.Host.TryGetConnectionContext(Connection.ConnectionId, out ServerRoutingContext? context)
            || context == null
            || !read(context))
        {
            Assert.True(DateTime.UtcNow < deadline, $"The server never read {SessionId}'s last message.");
            await Task.Delay(1);
        }
    }

    // Closes the session and waits until the server has released the seat.
    public async Task LeaveAsync()
    {
        await Session.DisposeAsync();
        DateTime deadline = DateTime.UtcNow + WireLoopbackServer.Timeout;
        while (_server.Host.IsConnectionJoined(Connection.ConnectionId))
        {
            Assert.True(DateTime.UtcNow < deadline, $"{SessionId} was never released");
            await Task.Delay(5);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        World.Dispose();
    }
}
