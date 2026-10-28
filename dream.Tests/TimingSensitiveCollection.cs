using Xunit;

namespace Dream.Tests;

// Tests whose assertions are about wall-clock time itself, so they run alone:
// DisableParallelization stops this collection from executing concurrently with any other
// collection (xUnit runs it after the parallel ones).
//
// Keep it to tests that truly measure the clock: TickPacerTests (pacing overshoot),
// StaticWorldChunkMeshBuilderPerformanceTests (a 250 ms build budget), CheckpointPersistenceTests
// and ServerLoadTests (opt-in via DREAM_LOAD_TEST; they measure tick percentiles, so they must not
// share the machine with other tests). PhysicsIntegratorTeardownTests is here for another reason:
// it switches on the process-wide physics leak tracker and forces full collections.
// Wave9SessionLeakLoopbackTests reads process-wide physics, thread and handle counts;
// Wave11SessionChurnTickLoopbackTests compares tick medians across a churn; and
// Wave8SnapshotRateCorrectionLoopbackTests needs steady play never starved of a core. Every other
// loopback and stress test, MultiClientLoopbackSmokeTests included, runs in the parallel pool and
// waits on progress (a received frame, a drained queue, a signalled decode) with a generous safety
// deadline instead of on elapsed time; see TestAssemblyInitializer for the thread-pool minimum
// and TestScheduling for how the parallel phase is scheduled.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingSensitiveCollection
{
    public const string Name = "TimingSensitive";
}
