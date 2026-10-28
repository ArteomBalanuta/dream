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

    // Closes a connection whose current socket write has not completed for this long. The
    // transport reports no partial progress, so one frame must finish inside it.
    public TimeSpan SendStallTimeout { get; init; } = TimeSpan.FromSeconds(5);

    // A lagging client's unsent snapshots merge into one. When the merged world mutations or
    // damage events pass these caps, the pending deltas are dropped and the server serves the
    // connection a full world resync instead, so the backlog stays bounded.
    public int MaxCoalescedWorldMutations { get; init; } = 4096;

    public int MaxCoalescedDamageEvents { get; init; } = 4096;

    // Clock for the liveness checks; tests pass a fake one.
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
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
