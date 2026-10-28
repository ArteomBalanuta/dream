using Xunit;
using Dream.Application;
using Dream.Client.Scenes;
using Dream.Client.Startup;
using Dream.Server;

namespace Dream.Tests.Application;

// NETR-2: the decision and teardown Game runs when the play session ends. PlayScene and Game need
// a GL context, so they are stood in for by fakes here; SceneTeardownTests checks Game's wiring.
public class PlaySessionEndTransitionTests
{
    [Fact]
    public void PlayScene_IsASessionEndSource()
    {
        Assert.True(typeof(ISessionEndSource).IsAssignableFrom(typeof(PlayScene)));
    }

    [Fact]
    public void HasEnded_IsFalse_WhileTheSessionIsLive()
    {
        FakePlayScene scene = new(reason: null);

        Assert.False(PlaySessionEndTransition.HasEnded(scene, out string? reason));
        Assert.Null(reason);
    }

    [Fact]
    public void HasEnded_ReturnsTheReason_OnceTheSessionEnded()
    {
        FakePlayScene scene = new(reason: null);
        Assert.False(PlaySessionEndTransition.HasEnded(scene, out _));

        scene.Reason = "Connection lost: no data from the server for 8 s";

        Assert.True(PlaySessionEndTransition.HasEnded(scene, out string? reason));
        Assert.Equal("Connection lost: no data from the server for 8 s", reason);
    }

    [Fact]
    public void TearDown_DisposesTheSceneThenTheLocalServer_EachOnce()
    {
        List<string> order = [];
        FakePlayScene scene = new("ended", order);
        FakeLocalServer server = new(order);

        IReadOnlyList<Exception> failures = PlaySessionEndTransition.TearDown(scene, server);

        Assert.Empty(failures);
        Assert.Equal(["scene", "server"], order);
        Assert.Equal(1, scene.DisposeCount);
        Assert.Equal(1, server.DisposeCount);
    }

    [Fact]
    public void TearDown_StillStopsTheLocalServer_WhenTheSceneThrows()
    {
        List<string> order = [];
        FakePlayScene scene = new("ended", order) { ThrowOnDispose = new InvalidOperationException("gl") };
        FakeLocalServer server = new(order);

        IReadOnlyList<Exception> failures = PlaySessionEndTransition.TearDown(scene, server);

        Assert.Same(scene.ThrowOnDispose, Assert.Single(failures));
        Assert.Equal(["scene", "server"], order);
        Assert.Equal(1, server.DisposeCount);
    }

    [Fact]
    public void TearDown_ReturnsBothFailures_WhenBothStepsThrow()
    {
        FakePlayScene scene = new("ended") { ThrowOnDispose = new InvalidOperationException("scene") };
        FakeLocalServer server = new() { ThrowOnDispose = new InvalidOperationException("server") };

        IReadOnlyList<Exception> failures = PlaySessionEndTransition.TearDown(scene, server);

        Assert.Equal(2, failures.Count);
        Assert.Same(scene.ThrowOnDispose, failures[0]);
        Assert.Same(server.ThrowOnDispose, failures[1]);
    }

    // Local mode: Game's handle is an OnDemandLocalServerHandle; the integrated server it started
    // is stopped exactly once by the teardown.
    [Fact]
    public void TearDown_StopsTheStartedIntegratedServer_Once()
    {
        FakeLocalServer integrated = new();
        OnDemandLocalServerHandle handle = new(() => integrated);
        handle.EnsureStarted();

        PlaySessionEndTransition.TearDown(new FakePlayScene("ended"), handle);

        Assert.Equal(1, integrated.DisposeCount);
    }

    // Remote mode: the local server never started, so there is nothing to stop.
    [Fact]
    public void TearDown_StartsNothing_WhenTheLocalServerNeverStarted()
    {
        int starts = 0;
        OnDemandLocalServerHandle handle = new(() =>
        {
            starts++;
            return new FakeLocalServer();
        });

        IReadOnlyList<Exception> failures = PlaySessionEndTransition.TearDown(new FakePlayScene("ended"), handle);

        Assert.Empty(failures);
        Assert.Equal(0, starts);
        Assert.False(handle.IsStarted);
    }

    private sealed class FakePlayScene(string? reason, List<string>? order = null) : ISessionEndSource, IDisposable
    {
        public string? Reason { get; set; } = reason;

        public string? SessionEndReason => Reason;

        public Exception? ThrowOnDispose { get; init; }

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            order?.Add("scene");
            if (ThrowOnDispose != null)
            {
                throw ThrowOnDispose;
            }
        }
    }

    private sealed class FakeLocalServer(List<string>? order = null) : ILocalServerHandle
    {
        public bool IsReady => true;

        public int BoundPort => 6123;

        public string? ErrorMessage => null;

        public DedicatedServerPerformanceSnapshot LatestPerformance => DedicatedServerPerformanceSnapshot.Empty;

        public Exception? ThrowOnDispose { get; init; }

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            order?.Add("server");
            return ThrowOnDispose != null ? ValueTask.FromException(ThrowOnDispose) : ValueTask.CompletedTask;
        }
    }
}
