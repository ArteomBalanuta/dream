using System.Reflection;
using Xunit;
using Dream.Client.Audio;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;

namespace Dream.Tests.BugHunt;

/// <summary>
/// Regression tests for the BUGHUNTER-RACE packet. Races cannot be proved by spinning threads and hoping,
/// so each test pins the guard that makes the race safe, single-threaded and deterministic: a session that
/// arrives after the player cancelled is closed and never published; the reason an ended session carries is
/// written atomically, so a lost connection cannot overwrite a protocol fault; and the mixer's volumes are
/// published as one value the audio thread can read whole.
/// </summary>
public sealed class RaceConcurrencyTests : IDisposable
{
    private readonly IReadOnlyList<TimeSpan> _delays = NetworkSessionCoordinator.ReconnectDelays;

    public RaceConcurrencyTests()
    {
        NetworkSessionCoordinator.ReconnectDelays =
            [.. Enumerable.Repeat(TimeSpan.FromMilliseconds(1), NetworkSessionCoordinator.MaxReconnectAttempts)];
    }

    public void Dispose()
    {
        NetworkSessionCoordinator.ReconnectDelays = _delays;
    }

    private static readonly ClientConnectionOptions Endpoint = new("203.0.113.9", "5057", "Wanderer");

    // BH-RACE-1: Escape during an automatic reconnect cancelled the retries, but an attempt already in
    // flight still published its session, and the game entered the world a frame later on a connection the
    // player had abandoned. The gate holds the connect open while StopReconnect runs, so the ordering is
    // fixed rather than raced.
    [Fact]
    public async Task BH_RACE_1_AReconnectCancelledWhileConnecting_PublishesNoSession()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GatedConnector connector = new() { Gate = gate.Task };
        NetworkSessionCoordinator coordinator = new(new FakeLocalServerHandle(), connector);

        coordinator.BeginReconnect(Endpoint);
        await connector.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(coordinator.IsBusy);

        // The player presses Escape while the socket is still being opened.
        coordinator.StopReconnect();
        Assert.False(coordinator.IsReconnecting);

        // The server answers after the cancel.
        gate.SetResult();
        await coordinator.WaitForPendingConnectionAsync();

        Assert.False(coordinator.TryTakeConnectedSession(out RemoteClientSession? session));
        Assert.Null(session);
        Assert.NotEqual(ConnectionFlowState.Connected, coordinator.State);
        // The screen is usable again instead of stuck on "Reconnecting (1/5)...".
        Assert.Equal(ConnectionFlowState.ReadyToConnect, coordinator.State);
        Assert.Equal("Reconnect cancelled.", coordinator.StatusMessage);

        // The session that arrived too late is closed rather than left holding its seat on the server.
        await connector.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // BH-RACE-1: the same cancel through CancelConnect (the loading panel's Escape, which bumps the connect
    // generation) must also leave the late session unpublished.
    [Fact]
    public async Task BH_RACE_1_AReconnectCancelledByCancelConnect_PublishesNoSession()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GatedConnector connector = new() { Gate = gate.Task };
        NetworkSessionCoordinator coordinator = new(new FakeLocalServerHandle(), connector);

        coordinator.BeginReconnect(Endpoint);
        await connector.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(coordinator.CancelConnect());

        gate.SetResult();
        await coordinator.WaitForPendingConnectionAsync();

        Assert.False(coordinator.TryTakeConnectedSession(out RemoteClientSession? session));
        Assert.Null(session);
        Assert.Equal(ConnectionFlowState.ReadyToConnect, coordinator.State);
        // CancelConnect's own message stands: the stale attempt must not write over it.
        Assert.Equal("Connect when ready.", coordinator.StatusMessage);
        await connector.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // BH-RACE-1: an uncancelled reconnect still publishes its session (the fix must not swallow the normal
    // path).
    [Fact]
    public async Task BH_RACE_1_AnUncancelledReconnect_StillPublishesItsSession()
    {
        GatedConnector connector = new();
        NetworkSessionCoordinator coordinator = new(new FakeLocalServerHandle(), connector);

        coordinator.BeginReconnect(Endpoint);
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(ConnectionFlowState.Connected, coordinator.State);
        Assert.True(coordinator.TryTakeConnectedSession(out RemoteClientSession? session));
        await session!.DisposeAsync();
    }

    // BH-RACE-2: DisconnectReason is written by the send loop, the receive loop and the owner thread. With
    // `??=` on a plain field two of them could interleave and a late "Connection lost: ..." could land on
    // top of a protocol mismatch, making a non-retryable end look retryable. The property is now computed
    // from a field written under Interlocked, so it has no setter at all.
    [Fact]
    public void BH_RACE_2_DisconnectReason_IsWrittenThroughOneAtomicField()
    {
        PropertyInfo property = typeof(RemoteClientSession).GetProperty(nameof(RemoteClientSession.DisconnectReason))!;
        Assert.Null(property.SetMethod);

        FieldInfo field = typeof(RemoteClientSession)
            .GetField("_disconnectReason", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(typeof(string), field.FieldType);

        // No path may assign the property directly any more: every writer goes through the two helpers.
        string source = File.ReadAllText(Path.Combine(
            Dream.Tests.Integration.RepoRoot.Find(), "dream", "Client", "Networking", "RemoteClientSession.cs"));
        Assert.DoesNotContain("DisconnectReason ??=", source, StringComparison.Ordinal);
        Assert.Contains("Volatile.Write(ref _disconnectReason", source, StringComparison.Ordinal);
        Assert.Contains("Interlocked.CompareExchange(ref _disconnectReason", source, StringComparison.Ordinal);
    }

    // BH-RACE-2: the reason recorded first is the one the player is shown and the one retryability is judged
    // from, so a later lost connection cannot downgrade it into an auto-reconnect loop.
    [Fact]
    public void BH_RACE_2_ALostConnection_DoesNotOverwriteAProtocolFault()
    {
        RemoteClientSession session = NewSession();
        const string mismatch = "Server uses protocol v21; this client uses v22.";

        session.EndSessionWithReason(mismatch, "test.protocol");
        session.EndSessionWithReason("Connection lost: IOException: broken pipe", "test.lost");

        Assert.Equal(mismatch, session.DisconnectReason);
        Assert.False(RemoteClientSession.IsRetryableEnd(session.DisconnectReason));
    }

    // BH-RACE-3: the audio callback thread copied a multi-float struct while the main thread replaced it, so
    // one buffer could mix an old master against new bus gains. The volumes are published behind a box now:
    // one atomic reference, so a read is a coherent snapshot and the audio thread never blocks.
    [Fact]
    public void BH_RACE_3_Volumes_ArePublishedAsOneAtomicReference()
    {
        FieldInfo field = typeof(AudioMixer).GetField("_volumes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.False(field.FieldType.IsValueType);
        Assert.Equal(typeof(AudioVolumes), field.FieldType.GetGenericArguments().Single());
    }

    // BH-RACE-3: a snapshot taken before a settings change keeps the gains it was taken with; the replacement
    // is whole, never a field-by-field update a reader could catch half done.
    [Fact]
    public void BH_RACE_3_ASnapshot_IsNotMutatedByALaterSetVolumes()
    {
        AudioMixer mixer = new(new AudioVolumes(true, 0.8f, 0.8f, 0.5f, 0.7f));
        AudioVolumes before = mixer.Volumes;

        mixer.SetVolumes(new AudioVolumes(true, 0.2f, 0.1f, 0.1f, 0.1f));

        Assert.Equal(0.8f, before.Master);
        Assert.Equal(0.5f, before.Music);

        AudioVolumes after = mixer.Volumes;
        Assert.Equal(0.2f, after.Master);
        Assert.Equal(0.1f, after.Music);
        // One coherent value: the bus gains are all derived from the same master.
        Assert.Equal(0.2f * 0.1f, after.BusGain(AudioBus.Music), 5);
        Assert.Equal(0.2f * 0.1f, after.BusGain(AudioBus.Sfx), 5);
    }

    private static RemoteClientSession NewSession() =>
        new(new IdleTransportConnection(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("203.0.113.9", 5057, "race-session", "Wanderer", "Wanderer"));

    // Holds every connect open on a gate so a cancel can be ordered against it without spinning threads.
    private sealed class GatedConnector : IRemoteSessionConnector
    {
        public Task? Gate { get; init; }

        public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<RemoteClientSession> ConnectAsync(
            RemoteClientOptions options,
            CancellationToken cancellationToken = default)
        {
            FirstCall.TrySetResult();
            if (Gate != null)
            {
                await Gate;
            }

            return new RemoteClientSession(
                new IdleTransportConnection(Closed),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer(),
                options);
        }
    }

    private sealed class IdleTransportConnection(TaskCompletionSource? closed = null) : ITransportConnection
    {
        public string ConnectionId => "race-test";

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            closed?.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLocalServerHandle : ILocalServerHandle
    {
        public bool IsReady => true;

        public int BoundPort => 6123;

        public string? ErrorMessage => null;

        public DedicatedServerPerformanceSnapshot LatestPerformance => DedicatedServerPerformanceSnapshot.Empty;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
