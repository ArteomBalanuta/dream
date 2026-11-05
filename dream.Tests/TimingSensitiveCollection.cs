using Xunit;

namespace Dream.Tests;

// Tests that must not share the process with any other test, so they run alone after the parallel
// phase (DisableParallelization). This attribute means "this test asserts on wall-clock time or
// reads or writes process-wide state". It does NOT mean "this test is slow": a slow loopback or
// persistence test belongs in the parallel pool (and, if it is long, in TestScheduling.SplitClasses).
// Putting slow tests here once made a strictly serial phase 74% of the suite's wall clock.
//
// What remains, and why:
// - Measure the clock: TickPacerTests (pacing overshoot), StaticWorldChunkMeshBuilderPerformanceTests
//   (a build budget), the opt-in load and *Measurement classes (DREAM_LOAD_TEST; tick percentiles
//   that other tests would disturb), StoredWorldSoak*, Wave12ChecksumWalkJitTests.
// - Compare steady play or tick medians: Wave8SnapshotRateCorrectionLoopbackTests,
//   Wave11SessionChurnTickLoopbackTests, and read process-wide physics, thread and handle counts:
//   Wave9SessionLeakLoopbackTests.
// - Set a process-wide environment variable: PvpFlagTests, ServerBootstrapDevCommandsTests,
//   WorldClockPersistenceTests (see also ProcessEnvironmentCollection).
// - Allocation gates: ClassicHudGemAllocationTests and ClassicHudAdversarialTests assert a warm HUD
//   frame allocates 0 B. The counter is per thread, but the runtime's tiered-JIT state (call counts,
//   the instrumented PGO tier, boxes of shared generics) is per process, and another test running new
//   code resets its delay and charges bytes to the frame being measured.
// - Process-wide physics leak tracker and forced full collections: PhysicsIntegratorTeardownTests.
// - Checkpoint timing and file state: CheckpointPersistenceTests, IncrementalCheckpointTests,
//   Wave9FrozenCubeCheckpointTests.
//
// A test that is only heavy on CPU or IO, uses its own temp database and an ephemeral loopback
// port, and waits on progress with a safety deadline (not on elapsed time) runs in the parallel
// pool. Per-test database pools are released with SqlitePools.Release, never ClearAllPools, which
// is process-wide. See TestAssemblyInitializer for the thread-pool minimum and TestScheduling for
// how the parallel phase is scheduled.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingSensitiveCollection
{
    public const string Name = "TimingSensitive";
}
