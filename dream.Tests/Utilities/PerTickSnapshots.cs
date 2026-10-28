using Dream.Server;

namespace Dream.Tests.Utilities;

// Server options for tests that drive the service one tick at a time and expect one snapshot per
// tick: SnapshotIntervalTicks = 1, the behaviour before NETR-7 made 2 (32 Hz) the default. These
// tests also pin that interval 1 still produces exactly the old output.
internal static class PerTickSnapshots
{
    public static ServerConnectionOptions Options { get; } = ServerConnectionOptions.Default with { SnapshotIntervalTicks = 1 };

    public static ServerConnectionOptions With(ServerConnectionOptions options) => options with { SnapshotIntervalTicks = 1 };
}
