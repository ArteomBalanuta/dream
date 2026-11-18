using Dream.Server;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-LIFE-3: the dedicated server used to handle Ctrl+C only, so SIGTERM (docker stop, systemctl
// stop, kill) and a closed console window skipped every `using` in RunServerModeAsync and lost the
// whole write queue and the final checkpoint. OrderlyShutdownSignals routes all of them into one
// cancellation and one bounded drain wait.
//
// These tests drive Request/RequestAndWaitForDrain directly rather than raising real signals: the
// logic under test is the once-only teardown and the bound on the wait, and nothing here spins a
// thread and hopes.
public sealed class LifeShutdownSignalsTests
{
    private static readonly TimeSpan ShortGrace = TimeSpan.FromMilliseconds(150);

    // The crux of the fix: whichever signal arrives first starts the shutdown, and every later one
    // is a no-op, so the teardown runs exactly once -- not zero times and not twice.
    [Fact]
    public void TheFirstSignal_StartsTheShutdown_AndLaterSignalsDoNotStartASecond()
    {
        List<string> lines = [];
        using OrderlyShutdownSignals signals = new(lines.Add, ShortGrace);

        int teardowns = 0;
        using CancellationTokenRegistration registration = signals.Token.Register(() => teardowns++);

        Assert.False(signals.ShutdownRequested);
        Assert.Null(signals.FirstSignal);
        Assert.Equal(0, teardowns);

        signals.Request("SIGTERM");
        signals.Request("Ctrl+C");
        signals.Request("ProcessExit");

        Assert.True(signals.ShutdownRequested);
        Assert.True(signals.Token.IsCancellationRequested);
        // Exactly once: three signals, one teardown.
        Assert.Equal(1, teardowns);
        Assert.Equal("SIGTERM", signals.FirstSignal);
        Assert.Equal(3, signals.SignalCount);
        Assert.Contains(lines, line => line.Contains("SIGTERM", StringComparison.Ordinal));
        Assert.Equal(2, lines.Count(line => line.Contains("already shutting down", StringComparison.Ordinal)));
    }

    // A signal arriving while the drain is already running waits for that drain rather than
    // starting a second one.
    [Fact]
    public void ASignalDuringTheDrain_WaitsForIt_AndStartsNothingNew()
    {
        using OrderlyShutdownSignals signals = new(_ => { }, TimeSpan.FromSeconds(5));
        int teardowns = 0;
        using CancellationTokenRegistration registration = signals.Token.Register(() => teardowns++);

        signals.Request("SIGTERM");
        Task drain = Task.Run(() =>
        {
            Thread.Sleep(50);
            signals.NotifyDrained();
        });

        Assert.True(signals.RequestAndWaitForDrain("ProcessExit"));
        Assert.Equal(1, teardowns);
        Assert.Equal("SIGTERM", signals.FirstSignal);
        drain.Wait(TimeSpan.FromSeconds(10));
    }

    // The bound: a drain that never lands must not hold the signal handler open. The OS kills the
    // process after its own grace period, so a handler that waited forever would simply lose the
    // save it was waiting for and hang the stop until the kill.
    [Fact]
    public void AWaitForADrainThatNeverLands_GivesUpAtTheGrace()
    {
        List<string> lines = [];
        using OrderlyShutdownSignals signals = new(lines.Add, ShortGrace);

        long start = Environment.TickCount64;
        bool drained = signals.RequestAndWaitForDrain("SIGTERM");
        TimeSpan waited = TimeSpan.FromMilliseconds(Environment.TickCount64 - start);

        Assert.False(drained);
        // Generous upper bound: the assertion under test is that it returns at all, well before
        // the shortest grace the OS gives (about 5 s for a closed console window).
        Assert.True(waited < TimeSpan.FromSeconds(3), $"the wait took {waited.TotalSeconds:0.##} s");
        Assert.Contains(lines, line => line.Contains("still saving", StringComparison.Ordinal));
    }

    // The normal stop: the body returned, the host/service/transport disposals ran, NotifyDrained
    // followed, so the ProcessExit hook that fires afterwards does not wait at all.
    [Fact]
    public void AfterTheDrain_AFurtherSignalDoesNotWait()
    {
        using OrderlyShutdownSignals signals = new(_ => { }, TimeSpan.FromSeconds(30));
        signals.Request("Ctrl+C");
        signals.NotifyDrained();

        long start = Environment.TickCount64;
        Assert.True(signals.RequestAndWaitForDrain("ProcessExit"));
        Assert.True(Environment.TickCount64 - start < 2000, "a drained shutdown must not wait");
    }

    // NotifyDrained is idempotent, and so is Dispose: the real path calls NotifyDrained in a
    // finally and Dispose straight after it.
    [Fact]
    public void NotifyDrained_AndDispose_AreIdempotent()
    {
        OrderlyShutdownSignals signals = new(_ => { }, ShortGrace);
        signals.NotifyDrained();
        signals.NotifyDrained();
        signals.Dispose();
        signals.Dispose();

        // A signal that lands after disposal is swallowed, not thrown out of an OS handler.
        signals.Request("SIGTERM");
        Assert.True(signals.WaitForDrain());
    }

    // Install must work on this platform: PosixSignalRegistration.Create for the signals the OS
    // does have, and no throw for the ones it does not.
    [Fact]
    public void Install_RegistersTheRealHooks_AndDisposeReleasesThem()
    {
        OrderlyShutdownSignals signals = new(_ => { }, ShortGrace);
        signals.Install();
        signals.NotifyDrained();
        signals.Dispose();
        // Reaching here means every registration was created and released without throwing.
        Assert.False(signals.ShutdownRequested);
    }
}
