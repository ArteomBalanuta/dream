using System.Reflection;
using Xunit;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;

namespace Dream.Tests.BugHunt;

// REV3-2: CancelConnect could lose the race it was written to win.
//
// The cancelled-Join fix stamps every connect with a generation and bumps it in CancelConnect, so a
// session that arrives after the cancel is disposed instead of published. But the check was not
// authoritative: TryConnectOnceAsync tested IsCurrentConnect(generation) and THEN wrote
// `_connectedSession = session; State = Connected;`. There is no lock and no synchronization context
// (OpenTK's loop installs none), so that continuation runs on a thread-pool thread while
// CancelConnect runs on the render thread. Interleave CancelConnect's
// Interlocked.Exchange(ref _connectedSession, null) + State = ReadyToConnect between the check and
// the two writes and the session is published anyway: the player is dropped into the join they
// pressed Escape on, and nothing is left to dispose it.
//
// The cure is to CLAIM the slot and re-test the generation after. CancelConnect bumps the generation
// before it clears the slot, so whichever side of the claim a cancel lands on, one of the two sees
// it and the session is disposed exactly once.
//
// The interleavings are driven through the publish itself rather than by spinning threads and hoping,
// so every case here is deterministic.
public sealed class Rev3ConnectPublishRaceTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static NetworkSessionCoordinator NewCoordinator() =>
        new(new FakeLocalServer(), new UnusedConnector());

    private static RemoteClientSession NewSession(TaskCompletionSource? closed = null) =>
        new(
            new IdleTransportConnection(closed),
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("203.0.113.9", 5057, "rev3", "Wanderer", "Wanderer"));

    private static bool TryPublish(NetworkSessionCoordinator coordinator, RemoteClientSession session, int generation) =>
        (bool)typeof(NetworkSessionCoordinator)
            .GetMethod("TryPublish", Instance)!
            .Invoke(coordinator, [session, generation])!;

    private static int Generation(NetworkSessionCoordinator coordinator) =>
        (int)typeof(NetworkSessionCoordinator).GetField("_connectGeneration", Instance)!.GetValue(coordinator)!;

    private static RemoteClientSession? Published(NetworkSessionCoordinator coordinator) =>
        (RemoteClientSession?)typeof(NetworkSessionCoordinator)
            .GetField("_connectedSession", Instance)!.GetValue(coordinator);

    private static void Bump(NetworkSessionCoordinator coordinator) =>
        typeof(NetworkSessionCoordinator).GetField("_connectGeneration", Instance)!
            .SetValue(coordinator, Generation(coordinator) + 1);

    // Interleaving A: the cancel lands BEFORE the claim. The generation is already stale when the
    // publish re-tests it, so the session is taken back out and disposed - this is the case the
    // original check caught too.
    [Fact]
    public async Task ACancelBeforeTheClaim_PublishesNothing_AndDisposesTheSession()
    {
        NetworkSessionCoordinator coordinator = NewCoordinator();
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteClientSession session = NewSession(closed);
        int generation = Generation(coordinator);

        Bump(coordinator);

        Assert.False(TryPublish(coordinator, session, generation));
        Assert.Null(Published(coordinator));
        Assert.False(coordinator.TryTakeConnectedSession(out RemoteClientSession? taken));
        Assert.Null(taken);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // Interleaving B, the one the finding is about: the cancel lands AFTER the publish has claimed
    // the slot. CancelConnect's Interlocked.Exchange takes the session out and disposes it, the
    // publish's re-test then sees the stale generation, its take-back fails (the slot is no longer
    // holding this session) and it disposes nothing - so the session is disposed exactly once and
    // the player is never handed it.
    [Fact]
    public void ACancelAfterTheClaim_LeavesNothingPublished()
    {
        NetworkSessionCoordinator coordinator = NewCoordinator();
        RemoteClientSession session = NewSession();
        int generation = Generation(coordinator);

        // The claim, as the publish makes it.
        typeof(NetworkSessionCoordinator).GetField("_connectedSession", Instance)!.SetValue(coordinator, session);
        // CancelConnect, in its own order: the generation first, then the slot.
        Bump(coordinator);
        typeof(NetworkSessionCoordinator).GetField("_connectedSession", Instance)!.SetValue(coordinator, null);

        // What the publish does from here: re-test, and take back only what is still ours.
        Assert.False(TryPublish(coordinator, session, generation));
        Assert.Null(Published(coordinator));
        Assert.False(coordinator.TryTakeConnectedSession(out _));
    }

    // The claim never displaces a session another connect already published.
    [Fact]
    public async Task APublishOverAnAlreadyPublishedSession_DisposesTheLateOne_AndLeavesTheFirst()
    {
        NetworkSessionCoordinator coordinator = NewCoordinator();
        RemoteClientSession first = NewSession();
        TaskCompletionSource lateClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteClientSession late = NewSession(lateClosed);
        int generation = Generation(coordinator);
        typeof(NetworkSessionCoordinator).GetField("_connectedSession", Instance)!.SetValue(coordinator, first);

        Assert.False(TryPublish(coordinator, late, generation));

        Assert.Same(first, Published(coordinator));
        await lateClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await first.DisposeAsync();
    }

    // Control: an uncancelled connect still publishes, exactly once, and the game can take it.
    [Fact]
    public void Control_AnUncancelledConnect_PublishesExactlyOnce()
    {
        NetworkSessionCoordinator coordinator = NewCoordinator();
        RemoteClientSession session = NewSession();

        Assert.True(TryPublish(coordinator, session, Generation(coordinator)));

        Assert.Same(session, Published(coordinator));
        Assert.True(coordinator.TryTakeConnectedSession(out RemoteClientSession? taken));
        Assert.Same(session, taken);
        // Taken once: the slot is empty afterwards, so nothing can be handed out twice.
        Assert.False(coordinator.TryTakeConnectedSession(out _));
    }

    // The ordering the fix rests on, pinned as source: the two publish sites hand the session to
    // TryPublish rather than assigning the field, and TryPublish claims the slot with a
    // CompareExchange BEFORE it re-tests the generation. A check that precedes the write is exactly
    // the defect.
    [Fact]
    public void ThePublish_ClaimsTheSlotBeforeItRetestsTheGeneration()
    {
        string source = Read("dream/Client/Startup/NetworkSessionCoordinator.cs");

        string publish = Method(source, "private bool TryPublish(");
        int claim = publish.IndexOf("Interlocked.CompareExchange(ref _connectedSession, session, null)", StringComparison.Ordinal);
        int recheck = publish.IndexOf("IsCurrentConnect(generation)", StringComparison.Ordinal);
        Assert.True(claim >= 0, "The publish must claim _connectedSession with a compare-exchange.");
        Assert.True(recheck > claim, "The generation must be re-tested AFTER the slot is claimed.");
        Assert.Contains("Interlocked.CompareExchange(ref _connectedSession, null, session)", publish, StringComparison.Ordinal);

        // No path may publish by plain assignment any more; CancelConnect's own clear stays atomic.
        Assert.DoesNotContain("_connectedSession = session;", source, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _connectedSession, null)", source, StringComparison.Ordinal);
        // CancelConnect bumps the generation before it clears the slot; the fix depends on that order.
        string cancel = Method(source, "public bool CancelConnect()");
        int bump = cancel.IndexOf("Interlocked.Increment(ref _connectGeneration)", StringComparison.Ordinal);
        int clear = cancel.IndexOf("Interlocked.Exchange(ref _connectedSession, null)", StringComparison.Ordinal);
        Assert.True(bump >= 0 && clear > bump, "CancelConnect must bump the generation before it clears the slot.");
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[start..(i + 1)];
            }
        }

        throw new InvalidOperationException("Unbalanced braces.");
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class UnusedConnector : IRemoteSessionConnector
    {
        public ValueTask<RemoteClientSession> ConnectAsync(
            RemoteClientOptions options, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No connect is started by these tests.");
    }

    private sealed class IdleTransportConnection(TaskCompletionSource? closed = null) : ITransportConnection
    {
        public string ConnectionId => "rev3-publish";

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

    private sealed class FakeLocalServer : ILocalServerHandle
    {
        public bool IsReady => true;

        public int BoundPort => 6123;

        public string? ErrorMessage => null;

        public DedicatedServerPerformanceSnapshot LatestPerformance => DedicatedServerPerformanceSnapshot.Empty;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
