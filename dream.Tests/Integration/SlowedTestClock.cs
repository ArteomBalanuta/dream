namespace Dream.Tests.Integration;

// The clock the loopback tests give their real RemoteClientSession: the system clock with elapsed
// time reported Factor times slower, so every deadline the session measures against it lasts Factor
// times as long in real seconds.
//
// Why these tests need it. RemoteClientSession.ServerSilenceTimeout ends the session when no byte
// has arrived for 8 s, because a joined client talking to a real server is sent a snapshot every
// tick or two and silence that long means the server has hung or vanished. In production the server
// ticks itself at 32-64 Hz, so that holds. In these tests the TEST is the server's tick source:
// nothing is sent between the test's own TickAsync calls, so "silence" measures how fast the test
// thread is running, not whether the server is alive. Under a loaded parallel suite one iteration of
// a wait loop can take seconds, and the watchdog then kills a perfectly healthy session - measured:
// "m5e2e-a lost its session: Connection lost: no data from the server for 8 s" in 19 of 69 tests
// with the collection gate opened to 64 on 16 cores.
//
// What it still catches. A server that has really hung or vanished sends nothing ever, so it still
// trips the watchdog, after 8 s of this clock instead of 8 s of the wall clock - and either way the
// test's own LoopbackDeadline fails it first. A socket that CLOSES is not silence at all: the
// session learns it from the read, with no clock involved, which is what M8's dropped-client test
// asserts. The watchdog's own behaviour - the 8 s value, the reason string, the deadline moving on
// every read - is covered by RemoteClientSessionWatchdogTests on a manual clock, so nothing here is
// the only thing proving it.
//
// Scaling TimestampFrequency is what slows elapsed time down: TimeProvider.GetElapsedTime divides
// the raw timestamp delta by it. CreateTimer scales the other way, multiplying a requested wait back
// into real time, so a session that sleeps "until 8 s after the last read" wakes when this clock
// says so.
internal sealed class SlowedTestClock : TimeProvider
{
    // Eight: the silence budget becomes 64 real seconds, comfortably past the worst contention this
    // suite has produced, and still well inside the 180 s hard cap of the test's own deadline, so a
    // genuinely silent server is reported by the test rather than left to run on.
    public const int Factor = 8;

    public static readonly SlowedTestClock Instance = new();

    private SlowedTestClock()
    {
    }

    public override long GetTimestamp() => System.GetTimestamp();

    public override long TimestampFrequency => System.TimestampFrequency * Factor;

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow();

    public override TimeZoneInfo LocalTimeZone => System.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        System.CreateTimer(callback, state, Stretch(dueTime), Stretch(period));

    private static TimeSpan Stretch(TimeSpan wait)
    {
        if (wait == Timeout.InfiniteTimeSpan || wait <= TimeSpan.Zero)
        {
            return wait;
        }

        double ticks = wait.Ticks * (double)Factor;
        return ticks >= TimeSpan.MaxValue.Ticks ? Timeout.InfiniteTimeSpan : TimeSpan.FromTicks((long)ticks);
    }
}
