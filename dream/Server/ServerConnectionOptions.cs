namespace Dream.Server;

// Liveness and backlog limits of one server-side connection. A TimeSpan of zero or less turns that
// check off. Defaults are used by every constructor that takes no options.
public sealed record ServerConnectionOptions
{
    public static ServerConnectionOptions Default { get; } = new();

    // Closes a connection that has delivered no complete inbound frame for this long. Clients send
    // a command frame every client tick while the game loop runs, menus open or not, so a live
    // client is never near it; it catches peers that vanished without a TCP close.
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(10);

    // Closes a connection whose current socket write has not completed for this long plus the
    // frame's transfer allowance (see SendStallMinimumBytesPerSecond). The transport reports no
    // partial progress, so one frame must finish inside that limit.
    public TimeSpan SendStallTimeout { get; init; } = TimeSpan.FromSeconds(5);

    // The slowest link a frame write is still allowed to run at: each write's stall limit is
    // SendStallTimeout plus frameBytes / this rate, so a slow but live client receiving a large
    // frame (a full world resync) is not mistaken for a stalled one. Zero or less gives every
    // frame the flat SendStallTimeout. Worlds go out in WorldChunkMessage.DefaultChunkBytes (64 KB)
    // chunks, sized so one chunk fits inside this allowance on a link of about 12.5 KB/s
    // (64 KB / (5 s + 0.25 s)); the largest other frame is a coalesced snapshot.
    public int SendStallMinimumBytesPerSecond { get; init; } = 256 * 1024;

    // A lagging client's unsent snapshots merge into one. When the merged world mutations or
    // damage events pass these caps, the pending deltas are dropped and the server serves the
    // connection a full world resync instead, so the backlog stays bounded.
    public int MaxCoalescedWorldMutations { get; init; } = 4096;

    public int MaxCoalescedDamageEvents { get; init; } = 4096;

    public const int DefaultMaxConnections = 64;

    public const string MaxConnectionsEnvironmentVariable = "DREAM_MAX_CONNECTIONS";

    // Service-wide: a connection accepted while this many are open is closed at once. Zero or less
    // turns the cap off. Overridden by DREAM_MAX_CONNECTIONS.
    public int MaxConnections { get; init; } = DefaultMaxConnections;

    // Clock for the liveness checks; tests pass a fake one.
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public const int DefaultSnapshotIntervalTicks = 2;

    // The largest interval DREAM_SNAPSHOT_INTERVAL_TICKS may set (250 ms at 64 Hz). Far longer
    // intervals starve the client of acks and snapshots: past about 500 ticks its 8 s watchdog
    // would drop a healthy connection, and well before that the ack-lag stall trigger
    // (ClientCommandTickStream) reads every interval as a stall. Larger values are clamped to it.
    public const int MaxSnapshotIntervalOverrideTicks = 16;

    // Overrides SnapshotIntervalTicks (a positive integer, at most MaxSnapshotIntervalOverrideTicks)
    // for one server run.
    public const string SnapshotIntervalEnvironmentVariable = "DREAM_SNAPSHOT_INTERVAL_TICKS";

    // Service-wide (DedicatedServerService reads it from the options it was built with): the server
    // simulates every tick but encodes and broadcasts a snapshot only once this many ticks have run
    // since the last one; 2 at the 64 Hz tick sends 32 snapshots a second. World mutations and
    // damage events of the skipped ticks stay pending in the runtime and go out merged into the
    // next snapshot, and action results follow that snapshot, so nothing is lost; acks simply
    // arrive less often. 1 sends a snapshot after every advance that ran a tick (the behaviour
    // before NETR-7). Values below 1 are treated as 1.
    public int SnapshotIntervalTicks { get; init; } = DefaultSnapshotIntervalTicks;

    // Applies DREAM_SNAPSHOT_INTERVAL_TICKS when it is set. An invalid value (not a positive
    // integer) keeps the current interval with a log line; one above
    // MaxSnapshotIntervalOverrideTicks is clamped to it with a log line.
    public ServerConnectionOptions WithEnvironmentOverrides(Action<string>? log = null)
    {
        return WithSnapshotIntervalOverride(Environment.GetEnvironmentVariable(SnapshotIntervalEnvironmentVariable), log)
            .WithMaxConnectionsOverride(Environment.GetEnvironmentVariable(MaxConnectionsEnvironmentVariable), log);
    }

    public ServerConnectionOptions WithMaxConnectionsOverride(string? value, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return this;
        }

        if (int.TryParse(value.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int max) &&
            max > 0)
        {
            return this with { MaxConnections = max };
        }

        log?.Invoke($"{MaxConnectionsEnvironmentVariable} is not a positive integer; keeping {MaxConnections}.");
        return this;
    }

    public ServerConnectionOptions WithSnapshotIntervalOverride(string? value, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return this;
        }

        if (int.TryParse(value.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int ticks) &&
            ticks > 0)
        {
            if (ticks > MaxSnapshotIntervalOverrideTicks)
            {
                log?.Invoke($"{SnapshotIntervalEnvironmentVariable} {ticks} is above {MaxSnapshotIntervalOverrideTicks}; using {MaxSnapshotIntervalOverrideTicks} ticks.");
                ticks = MaxSnapshotIntervalOverrideTicks;
            }

            return this with { SnapshotIntervalTicks = ticks };
        }

        log?.Invoke($"{SnapshotIntervalEnvironmentVariable} is not a positive integer; keeping {SnapshotIntervalTicks} ticks.");
        return this;
    }
}

// Why a connection was closed by the server itself rather than by its peer.
public enum ServerConnectionCloseReason
{
    None,
    // A writer or encode error.
    Error,
    // More than ServerConnection.MaxPendingOutboundMessages unmerged messages queued.
    BacklogOverflow,
    IdleTimeout,
    SendStall
}
