using Xunit;

namespace Dream.Tests;

// Tests whose assertions are about wall-clock time itself, so they run alone:
// DisableParallelization stops this collection from executing concurrently with any other
// collection (xUnit runs it after the parallel ones).
//
// Keep it to tests that truly measure the clock: TickPacerTests (pacing overshoot),
// MultiClientLoopbackSmokeTests (a real-time server tick loop and two clients),
// StaticWorldChunkMeshBuilderPerformanceTests (a 250 ms build budget), CheckpointPersistenceTests
// and ServerLoadTests (opt-in via DREAM_LOAD_TEST; they measure tick percentiles, so they must not
// share the machine with other tests). Every other loopback and stress test runs in the parallel
// pool and waits on progress (a received frame, a drained queue, a signalled decode) with a
// generous safety deadline instead of on elapsed time; see TestAssemblyInitializer for the
// thread-pool minimum they rely on.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingSensitiveCollection
{
    public const string Name = "TimingSensitive";
}
