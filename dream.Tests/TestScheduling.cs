using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("Dream.Tests.TestScheduling+Framework", "dream.Tests")]
[assembly: TestCollectionOrderer("Dream.Tests.TestScheduling+LongestFirstOrderer", "dream.Tests")]

namespace Dream.Tests;

// How the parallel phase is scheduled (ARCH-1b).
//
// xUnit makes each test class one collection and runs a collection's tests one after another; the
// parallel phase therefore lasts at least as long as the slowest class. A few classes run several
// real-time scenarios each (real TCP stalls of 1-11 s, a SQLite lock held past busy_timeout) whose
// waits cannot be faked, so each test in them is seconds of wall clock that no CPU can shorten.
// Run serially they were the suite's critical path (71 s and 55 s of a 78 s run).
//
// Scheduling only (no test, assertion or budget changes):
//
// - SplitClasses: every test case of these classes gets a collection of its own, so their
//   scenarios run in parallel with each other. Only classes whose tests share no mutable static
//   state, no collection fixture and no ordering assumption belong here (each test case still gets
//   its own class instance, and a class fixture is created per test case). Listing a class here
//   does not touch its file, so it works for files other work streams own.
//
// - The slot gate (AssemblyRunner): at most MaxRunningCollections collections run at once, so sync
//   tests no longer fill the thread pool and starve the Task.Run work that running tests wait on.
//   RealTimeClasses skip the gate.
//
// - LongestFirst: slots go to known long collections first (longest processing time first), so
//   none of them starts late and ends the phase late; the rest keep xUnit's default order.
//
// None of this touches the non-parallel collections (TimingSensitive, RenderThreadAffinity): xUnit
// still runs those alone after the parallel phase.
//
// Measured on 16 logical CPUs (Debug): 78.6 s of test time before, about 37 s after, with the
// parallel phase CPU-bound (testhost CPU ~360-410 s). Server GC no longer makes a difference.
public static class TestScheduling
{
    // Classes whose test cases each run as their own collection; see the header. Measured alone
    // (Debug, 16 logical CPUs) each was 8-43 s run serially, almost all of it in a few tests.
    public static readonly IReadOnlySet<string> SplitClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "Dream.Tests.Integration.Wave8CommandStallCorrectionLoopbackTests",
        "Dream.Tests.Integration.Wave8IdleTimeoutClientStallLoopbackTests",
        "Dream.Tests.Server.Persistence.PersistenceWriteQueueLockRetryTests",
        "Dream.Tests.Integration.ClientCommandStallLoopbackTests",
        "Dream.Tests.Simulation.Runtime.BufferedInboxStallReplayTests",
        "Dream.Tests.Simulation.Runtime.WorldSessionRuntimeTickAllocationTests",
        "Dream.Tests.Simulation.Runtime.Wave7StarvationReconciliationTests",
        "Dream.Tests.Simulation.World.PresentationExportCacheStaticEditTests",
        "Dream.Tests.Server.Wave8ShutdownReleaseRaceTests",
        "Dream.Tests.Client.Replication.ClientReplayReconciliationTests",
        "Dream.Tests.Rendering.World.StaticWorldChunkPlanEquivalenceTests",
        "Dream.Tests.Simulation.Physics.PartialCellSupportTests",
        "Dream.Tests.Simulation.Physics.PhysicsSyncActiveBodyTests",
        "Dream.Tests.Server.Persistence.WorldSimulationSeedTests",
        "Dream.Tests.Multiplayer.Protocol.ClientInputValidationTests",
        "Dream.Tests.Server.DedicatedServerTickAllocationTests",
        "Dream.Tests.Simulation.World.Geometry.MapGeometryFacadeTests",
        "Dream.Tests.Simulation.Runtime.RuntimeAuthoritativeSessionHostTests",
        "Dream.Tests.Rendering.World.Wave7StaticChunkIncrementalPlanTests",
        "Dream.Tests.Server.DedicatedServerHostReleaseRaceTests",
        "Dream.Tests.Rendering.World.StaticWorldChunkFrustumCullingTests",
        "Dream.Tests.Integration.AsyncResourceLoaderStressIntegrationTests",
        "Dream.Tests.Simulation.Players.PlayerCollisionAllocationTests",
        "Dream.Tests.Integration.Wave7FloorCubeChecksumLoopbackTests",
        "Dream.Tests.Client.Networking.RemoteClientSessionJoinTests",
        "Dream.Tests.Simulation.World.MapSyncAllocationTests",
        "Dream.Tests.Simulation.World.PresentationExportCacheTests",
        "Dream.Tests.Integration.EnemyInterpolationReplicationIntegrationTests",
        "Dream.Tests.Server.LiveServerDeferredJoinLoopbackTests",
        "Dream.Tests.Server.WorldResyncDeferralTests",
        "Dream.Tests.Rendering.World.StaticWorldChunkRendererUploadPipelineTests",
    };

    // Classes whose tests run a real-time loop and assert on how it went: a headless client that
    // predicts one tick per 15.6 ms and must not fall behind, or a stall measured against a server
    // timeout or SQLite's busy_timeout. They mostly wait, so they skip the slot gate and all start
    // at once; the gate keeps the CPU-bound collections from starving them of a core. They must
    // also be in SplitClasses (each test its own collection).
    public static readonly IReadOnlySet<string> RealTimeClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "Dream.Tests.Integration.Wave8CommandStallCorrectionLoopbackTests",
        "Dream.Tests.Integration.Wave8IdleTimeoutClientStallLoopbackTests",
        "Dream.Tests.Server.Persistence.PersistenceWriteQueueLockRetryTests",
        "Dream.Tests.Integration.ClientCommandStallLoopbackTests",
    };

    // The collections that took longest in a full parallel run (after splitting; a split class at
    // its slowest test), longest first: they start first, so none of them starts late and ends the
    // phase late. Unlisted collections follow in xUnit's default order. A name missing here only
    // costs scheduling quality, never correctness. Measured 2026-10-28, Debug, 16 logical CPUs.
    public static readonly IReadOnlyList<string> LongestFirst =
    [
        "Dream.Tests.Simulation.Physics.PhysicsSyncActiveBodyTests",
        "Dream.Tests.Integration.Wave8CommandStallCorrectionLoopbackTests",
        "Dream.Tests.Integration.AnimatedModelPoseSharpGltfKeyframeParityShard1IntegrationTests",
        "Dream.Tests.Integration.ClientCommandStallLoopbackTests",
        "Dream.Tests.Integration.AnimatedModelPoseSharpGltfKeyframeParityShard0IntegrationTests",
        "Dream.Tests.Client.Replication.ClientReplayReconciliationTests",
        "Dream.Tests.Multiplayer.Transport.MultiClientLoopbackSmokeTests",
        "Dream.Tests.Integration.Wave8IdleTimeoutClientStallLoopbackTests",
        "Dream.Tests.Simulation.Runtime.BufferedInboxStallReplayTests",
        "Dream.Tests.Integration.AnimatedModelPoseSharpGltfKeyframeParityShard2IntegrationTests",
        "Dream.Tests.Integration.AnimatedModelPoseSharpGltfPlaybackParityIntegrationTests",
        "Dream.Tests.Integration.Wave7LateJoinFractureResyncLoopbackTests",
        "Dream.Tests.Simulation.Runtime.Wave7StarvationReconciliationTests",
        "Dream.Tests.Server.Persistence.PersistenceWriteQueueLockRetryTests",
        "Dream.Tests.Client.Replication.ClientJoinAcceptedSubcellApplierTests",
        "Dream.Tests.Integration.CoalesceOverflowResyncLoopbackTests",
        "Dream.Tests.Integration.PresentationExportAliasingIntegrationTests",
        "Dream.Tests.Integration.Wave7FloorCubeChecksumLoopbackTests",
        "Dream.Tests.Server.Wave8ShutdownReleaseRaceTests",
        "Dream.Tests.Integration.ProtocolV2SlowClientLoopbackIntegrationTests",
        "Dream.Tests.Integration.InPlaceUserObjectPhysicsSyncIntegrationTests",
        "Dream.Tests.Simulation.World.PresentationExportCacheStaticEditTests",
        "Dream.Tests.Integration.HandWrittenConverterLoopbackIntegrationTests",
        "Dream.Tests.Server.Persistence.WorldSimulationSeedTests",
        "Dream.Tests.Integration.ClientWorldStateBoundaryIntegrationTests",
        "Dream.Tests.Integration.ProtocolV2SharedSnapshotLoopbackIntegrationTests",
        "Dream.Tests.Rendering.World.StaticWorldChunkPlanEquivalenceTests",
        "Dream.Tests.Integration.ServerSnapshotConverterConcurrencyIntegrationTests",
        "Dream.Tests.Integration.SnapshotConverterLoopbackSessionIntegrationTests",
        "Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests",
        "Dream.Tests.Integration.SnapshotConverterLateJoinerIntegrationTests",
        "Dream.Tests.Server.SharedSnapshotEncodingTests",
        "Dream.Tests.Integration.PersistentWorldSeedLoopbackIntegrationTests",
        "Dream.Tests.Server.SessionReleaseReconnectRaceTests",
        "Dream.Tests.Rendering.World.StaticWorldChunkMeshBuilderAllocationTests",
        "Dream.Tests.Multiplayer.Protocol.ServerSnapshotConverterTests",
        "Dream.Tests.Server.Persistence.UnrestoredPlayerSessionTests",
        "Dream.Tests.Integration.AnimatedModelPoseSharpGltfClipSwitchParityIntegrationTests",
        "Dream.Tests.Client.Replication.ClientJoinAcceptedStartupTerrainApplierTests",
        "Dream.Tests.Rendering.Models.AnimatedModelAllocationTests",
        "Dream.Tests.Simulation.World.PresentationExportCacheTests",
        "Dream.Tests.Server.DedicatedServerServiceShutdownTests",
        "Dream.Tests.Integration.PooledClientSendLoopbackIntegrationTests",
        "Dream.Tests.Integration.PresentationExportModelMappingIntegrationTests",
        "Dream.Tests.Integration.ProtocolV2VersionRejectionLoopbackIntegrationTests",
    ];

    internal static string? ClassOf(ITestCollection collection)
    {
        // Default collections are named "Test collection for <full class name>"; split ones carry
        // their class name the same way plus the test case.
        const string Prefix = "Test collection for ";
        string name = collection.DisplayName;
        if (!name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        int end = name.IndexOf(' ', Prefix.Length);
        return end < 0 ? name[Prefix.Length..] : name[Prefix.Length..end];
    }

    public sealed class Framework(IMessageSink messageSink) : XunitTestFramework(messageSink)
    {
        protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
        {
            return new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);
        }
    }

    private sealed class Executor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
        : XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
    {
        // As XunitTestFrameworkExecutor.RunTestCases, with the split applied and the gated runner.
        protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
        {
            List<IXunitTestCase> cases = testCases.ToList();
            Split(cases);
            using AssemblyRunner runner = new(TestAssembly, cases, DiagnosticMessageSink, executionMessageSink, executionOptions);
            await runner.RunAsync();
        }
    }

    // xUnit's aggressive algorithm with unlimited threads queues every collection on the thread
    // pool at once, and a sync test blocks its pool thread. While collections are still queued,
    // a pool thread that frees up takes the next queued collection before it steals the Task.Run
    // work that running tests wait on (a server's accept loop, a session's send loop, a
    // persistence worker): that work sits in the blocked test thread's local queue. Tests then
    // wait seconds on their own background work, and real-time scenarios miss their windows.
    //
    // The runner still queues every collection at once, but each one first awaits a slot (an
    // await, not a blocked thread), so the pool's queue drains at once and at most
    // MaxRunningCollections run. The slots go out in LongestFirst order. MaxRunningCollections
    // stays below TestAssemblyInitializer's pool minimum, so the pool keeps threads free for the
    // tests' background work without waiting for thread injection.
    private sealed class AssemblyRunner(
        ITestAssembly testAssembly,
        IEnumerable<IXunitTestCase> testCases,
        IMessageSink diagnosticMessageSink,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions)
        : XunitTestAssemblyRunner(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
    {
        private readonly OrderedGate _gate = new(MaxRunningCollections);
        private Dictionary<string, int> _order = [];

        protected override Task<RunSummary> RunTestCollectionsAsync(IMessageBus messageBus, CancellationTokenSource cancellationTokenSource)
        {
            _order = OrderTestCollections()
                .Select((pair, index) => (pair.Item1.UniqueID, index))
                .ToDictionary(entry => entry.UniqueID.ToString("N"), entry => entry.index);
            return base.RunTestCollectionsAsync(messageBus, cancellationTokenSource);
        }

        protected override async Task<RunSummary> RunTestCollectionAsync(IMessageBus messageBus, ITestCollection testCollection, IEnumerable<IXunitTestCase> testCases, CancellationTokenSource cancellationTokenSource)
        {
            string? className = ClassOf(testCollection);
            if (className != null && RealTimeClasses.Contains(className))
            {
                return await base.RunTestCollectionAsync(messageBus, testCollection, testCases, cancellationTokenSource);
            }

            int priority = _order.TryGetValue(testCollection.UniqueID.ToString("N"), out int index) ? index : int.MaxValue;
            await _gate.EnterAsync(priority);
            try
            {
                return await base.RunTestCollectionAsync(messageBus, testCollection, testCases, cancellationTokenSource);
            }
            finally
            {
                _gate.Exit();
            }
        }
    }

    // How many gated collections run at once. About one per core: the CPU-bound collections then
    // do not oversubscribe the machine, so the real-time ones always find a core. More slots do not
    // finish the CPU work sooner. DREAM_TEST_MAX_COLLECTIONS overrides it for experiments.
    internal static int MaxRunningCollections { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("DREAM_TEST_MAX_COLLECTIONS"), out int configured) && configured > 0
            ? configured
            : Math.Max(4, Environment.ProcessorCount * 3 / 4);

    // A counting gate whose waiters are admitted lowest priority value first.
    internal sealed class OrderedGate(int slots)
    {
        private readonly PriorityQueue<TaskCompletionSource, (int Priority, long Arrival)> _waiters = new();
        private int _free = slots;
        private long _arrivals;

        public Task EnterAsync(int priority)
        {
            lock (_waiters)
            {
                if (_free > 0 && _waiters.Count == 0)
                {
                    _free--;
                    return Task.CompletedTask;
                }

                TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue(waiter, (priority, _arrivals++));
                return waiter.Task;
            }
        }

        public void Exit()
        {
            TaskCompletionSource? next = null;
            lock (_waiters)
            {
                if (!_waiters.TryDequeue(out next, out _))
                {
                    _free++;
                }
            }

            next?.SetResult();
        }
    }

    // Moves every test case of a SplitClasses class into a collection of its own. Only the
    // collection changes: the class, method, arguments and unique ID (which does not include the
    // collection) stay, so runners map results back to the same tests.
    internal static void Split(IEnumerable<IXunitTestCase> testCases)
    {
        foreach (IXunitTestCase testCase in testCases)
        {
            ITestClass testClass = testCase.TestMethod.TestClass;
            if (!SplitClasses.Contains(testClass.Class.Name) ||
                testCase.TestMethod is not TestMethod testMethod ||
                testClass.TestCollection.CollectionDefinition != null)
            {
                continue;
            }

            ITestCollection original = testClass.TestCollection;
            string name = $"{original.DisplayName} {testCase.DisplayName}";
            TestCollection collection = new(original.TestAssembly, collectionDefinition: null, name);
            TestMethod own = new(new TestClass(collection, testClass.Class), testMethod.Method);
            SetTestMethod(testCase, own);
        }
    }

    private static void SetTestMethod(IXunitTestCase testCase, ITestMethod testMethod)
    {
        PropertyInfo property = typeof(TestMethodTestCase).GetProperty(nameof(TestMethodTestCase.TestMethod))!;
        property.SetValue(testCase, testMethod);
    }

    public sealed class LongestFirstOrderer : ITestCollectionOrderer
    {
        private readonly DefaultTestCollectionOrderer _default = new();

        public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
        {
            List<ITestCollection> ordered = _default.OrderTestCollections(testCollections).ToList();
            // OrderBy is stable: unranked collections keep the default order behind the ranked ones.
            return ordered.OrderBy(Rank).ToList();
        }

        private static int Rank(ITestCollection collection)
        {
            string? className = TestScheduling.ClassOf(collection);
            int rank = className == null ? -1 : IndexOf(LongestFirst, className);
            if (rank >= 0)
            {
                return rank;
            }

            return className != null && SplitClasses.Contains(className) ? LongestFirst.Count : int.MaxValue;
        }

        private static int IndexOf(IReadOnlyList<string> names, string name)
        {
            for (int i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
