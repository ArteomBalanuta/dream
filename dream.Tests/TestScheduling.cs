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
//
// ARCH-1c (wave 12): waves 10-11 grew the suite back to about 110 s. The parallel phase ended on
// unsplit classes that started late (Wave9StarveInputChangeTests alone ran 27-43 s serially; it
// now simulates each distinct run once), and
// the serial TimingSensitive phase had grown to about 50 s. Those classes are split and ranked
// below; the serial phase keeps only what reads process-wide state or measures the clock. The
// gate also widens to one slot per logical CPU once every real-time collection has finished: the
// quarter of the machine it held back was there for them, and after them the parallel phase is
// CPU-bound collections only, so those cores sat idle for the rest of the phase.
public static class TestScheduling
{
    // Classes whose test cases each run as their own collection; see the header. Measured alone
    // (Debug, 16 logical CPUs) each was 8-43 s run serially, almost all of it in a few tests.
    public static readonly IReadOnlySet<string> SplitClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "Dream.Tests.Integration.Wave8CommandStallCorrectionLoopbackTests",
        "Dream.Tests.Integration.Wave8IdleTimeoutClientStallLoopbackTests",
        "Dream.Tests.Server.Persistence.PersistenceWriteQueueLockRetryTests",
        "Dream.Tests.Server.Persistence.ShutdownDrainDeadlineTests",
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
        // ARCH-1c: long classes from waves 9-11, each test independent (no shared mutable static
        // state, no fixture; the facing probes they share are deterministic and cached).
        // Wave9StarveInputChangeTests is deliberately not here: its theory cases now share their
        // simulated runs, so split they would mostly hold slots waiting on each other; unsplit it
        // is one collection of a few seconds, ranked early in LongestFirst.
        "Dream.Tests.Simulation.Runtime.Wave9StarvedTickAckTests",
        "Dream.Tests.Integration.WorldTransferLoopbackTests",
        "Dream.Tests.Server.StaleSessionTakeoverTests",
        "Dream.Tests.Simulation.Physics.CubeRestFreezerTests",
        "Dream.Tests.Integration.SnapshotIntervalLoopbackTests",
        "Dream.Tests.Integration.Wave10TransferInteractionTests",
        "Dream.Tests.Integration.Wave9SessionLifecycleLoopbackTests",
        "Dream.Tests.Integration.Wave11SessionChurnLoopbackTests",
        "Dream.Tests.Multiplayer.Protocol.WorldTransferProtocolTests",
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
        "Dream.Tests.Server.Persistence.ShutdownDrainDeadlineTests",
        "Dream.Tests.Integration.ClientCommandStallLoopbackTests",
        // ARCH-1c: 8 s of a slow link played in real time (it was in the serial phase).
        "Dream.Tests.Integration.Wave10TransferInteractionTests",
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
        "Dream.Tests.Simulation.Runtime.Wave9StarveInputChangeTests",
        "Dream.Tests.Multiplayer.Transport.MultiClientLoopbackSmokeTests",
        "Dream.Tests.Integration.Wave8IdleTimeoutClientStallLoopbackTests",
        "Dream.Tests.Simulation.Runtime.BufferedInboxStallReplayTests",
        "Dream.Tests.Integration.WorldTransferLoopbackTests",
        "Dream.Tests.Integration.Wave10TransferInteractionTests",
        "Dream.Tests.Integration.AnimatedModelPoseSharpGltfKeyframeParityShard2IntegrationTests",
        "Dream.Tests.Integration.AnimatedModelPoseSharpGltfPlaybackParityIntegrationTests",
        "Dream.Tests.Integration.Wave7LateJoinFractureResyncLoopbackTests",
        "Dream.Tests.Simulation.Runtime.Wave7StarvationReconciliationTests",
        "Dream.Tests.Server.Persistence.PersistenceWriteQueueLockRetryTests",
        "Dream.Tests.Server.Persistence.ShutdownDrainDeadlineTests",
        "Dream.Tests.Client.Replication.ClientJoinAcceptedSubcellApplierTests",
        "Dream.Tests.Client.Networking.RemoteClientSessionJoinTests",
        "Dream.Tests.Simulation.Runtime.Wave9StarvedTickAckTests",
        "Dream.Tests.Integration.SnapshotIntervalLoopbackTests",
        "Dream.Tests.Multiplayer.Protocol.WorldTransferProtocolTests",
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

    // The class a collection runs, from its display name: xUnit 2 names a class's default collection
    // "Test collection for <full class name>", and Split names its collections the same way plus the
    // test case. Null for a named collection ([Collection]). This depends on a display string, so an
    // xUnit upgrade that renamed it would make every lookup null and silently drop RealTimeClasses
    // and LongestFirst; TestSchedulingTests discovers real test cases to catch that.
    internal static string? ClassOf(ITestCollection collection)
    {
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
            Split(cases, message => DiagnosticMessageSink.OnMessage(new DiagnosticMessage(message)));
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
        private int _realTimeRemaining;

        protected override Task<RunSummary> RunTestCollectionsAsync(IMessageBus messageBus, CancellationTokenSource cancellationTokenSource)
        {
            List<ITestCollection> ordered = OrderTestCollections().Select(pair => pair.Item1).ToList();
            _order = ordered
                .Select((collection, index) => (collection.UniqueID, index))
                .ToDictionary(entry => entry.UniqueID.ToString("N"), entry => entry.index);
            _realTimeRemaining = ordered.Count(IsRealTime);
            if (_realTimeRemaining == 0)
            {
                WidenGate();
            }

            return base.RunTestCollectionsAsync(messageBus, cancellationTokenSource);
        }

        protected override async Task<RunSummary> RunTestCollectionAsync(IMessageBus messageBus, ITestCollection testCollection, IEnumerable<IXunitTestCase> testCases, CancellationTokenSource cancellationTokenSource)
        {
            if (IsRealTime(testCollection))
            {
                try
                {
                    return await base.RunTestCollectionAsync(messageBus, testCollection, testCases, cancellationTokenSource);
                }
                finally
                {
                    if (Interlocked.Decrement(ref _realTimeRemaining) == 0)
                    {
                        WidenGate();
                    }
                }
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

        private static bool IsRealTime(ITestCollection collection)
        {
            string? className = ClassOf(collection);
            return className != null && RealTimeClasses.Contains(className);
        }

        private void WidenGate()
        {
            _gate.AddSlots(WidenedRunningCollections - MaxRunningCollections);
        }
    }

    // How many gated collections run at once. About one per core: the CPU-bound collections then
    // do not oversubscribe the machine, so the real-time ones always find a core. More slots do not
    // finish the CPU work sooner. DREAM_TEST_MAX_COLLECTIONS overrides it for experiments, clamped
    // to the thread pool minimum TestAssemblyInitializer sets (ProcessorCount * ThreadsPerCore):
    // more slots than pool threads would bring back the injection delay the gate exists to avoid.
    internal static int MaxRunningCollections { get; } =
        ResolveMaxRunningCollections(Environment.GetEnvironmentVariable("DREAM_TEST_MAX_COLLECTIONS"), Environment.ProcessorCount);

    // How many gated collections run at once after the last real-time collection finished (ARCH-1c):
    // one per logical CPU, still within the pool minimum, never fewer than MaxRunningCollections.
    // A DREAM_TEST_MAX_COLLECTIONS experiment keeps its fixed count for the whole run.
    internal static int WidenedRunningCollections { get; } =
        ResolveWidenedRunningCollections(Environment.GetEnvironmentVariable("DREAM_TEST_MAX_COLLECTIONS"), Environment.ProcessorCount);

    internal static int ResolveWidenedRunningCollections(string? configured, int processorCount)
    {
        int running = ResolveMaxRunningCollections(configured, processorCount);
        if (int.TryParse(configured, out int value) && value > 0)
        {
            return running;
        }

        int poolMinimum = Math.Max(1, processorCount * TestAssemblyInitializer.ThreadsPerCore);
        return Math.Max(running, Math.Min(poolMinimum, processorCount));
    }

    internal static int ResolveMaxRunningCollections(string? configured, int processorCount)
    {
        int poolMinimum = Math.Max(1, processorCount * TestAssemblyInitializer.ThreadsPerCore);
        int fallback = Math.Min(poolMinimum, Math.Max(4, processorCount * 3 / 4));
        if (!int.TryParse(configured, out int value) || value <= 0)
        {
            return fallback;
        }

        if (value > poolMinimum)
        {
            Console.Error.WriteLine(
                $"[TestScheduling] DREAM_TEST_MAX_COLLECTIONS={value} is above the thread pool minimum {poolMinimum} (ProcessorCount * {TestAssemblyInitializer.ThreadsPerCore}); using {poolMinimum}.");
            return poolMinimum;
        }

        return value;
    }

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

        // Adds slots, admitting waiters (lowest priority value first) for as many as are queued.
        public void AddSlots(int count)
        {
            List<TaskCompletionSource> admitted = [];
            lock (_waiters)
            {
                for (int i = 0; i < count; i++)
                {
                    if (_waiters.TryDequeue(out TaskCompletionSource? next, out _))
                    {
                        admitted.Add(next);
                    }
                    else
                    {
                        _free++;
                    }
                }
            }

            foreach (TaskCompletionSource waiter in admitted)
            {
                waiter.SetResult();
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
    //
    // The move sets TestMethodTestCase.TestMethod through reflection (its setter is not public).
    // If an xUnit upgrade removes or changes it, Split reports why through diagnostic and returns
    // false with no case moved (a failure partway through moves the cases already moved back to
    // their original collection): the suite then runs unsplit (slower, still correct) instead of
    // failing every run. Returns true when the split applied.
    internal static bool Split(IEnumerable<IXunitTestCase> testCases, Action<string>? diagnostic = null)
    {
        PropertyInfo? property = typeof(TestMethodTestCase).GetProperty(nameof(TestMethodTestCase.TestMethod));
        if (property?.SetMethod == null)
        {
            ReportSplitDisabled(diagnostic, "TestMethodTestCase.TestMethod has no setter");
            return false;
        }

        List<(IXunitTestCase TestCase, ITestMethod Own)> moves = [];
        // Cases already moved, with the test method each had before, for the rollback.
        List<(IXunitTestCase TestCase, ITestMethod Original)> moved = [];
        try
        {
            CollectSplits(testCases, moves);
            for (int i = 0; i < moves.Count; i++)
            {
                ITestMethod original = moves[i].TestCase.TestMethod;
                property.SetValue(moves[i].TestCase, moves[i].Own);
                moved.Add((moves[i].TestCase, original));
            }
        }
        catch (Exception ex)
        {
            string reason = $"{ex.GetType().Name}: {ex.Message}";
            string? rollbackFailure = RollBack(property, moved);
            ReportSplitDisabled(
                diagnostic,
                rollbackFailure == null
                    ? reason
                    : $"{reason}; moving the {moved.Count} cases already split back failed ({rollbackFailure}), so some may still run in their own collection");
            return false;
        }

        return true;
    }

    // Restores the cases moved before a failure, newest first. Null when every one was restored,
    // else why one was not (the rest are still attempted).
    private static string? RollBack(PropertyInfo property, List<(IXunitTestCase TestCase, ITestMethod Original)> moved)
    {
        string? failure = null;
        for (int i = moved.Count - 1; i >= 0; i--)
        {
            try
            {
                property.SetValue(moved[i].TestCase, moved[i].Original);
            }
            catch (Exception ex)
            {
                failure ??= $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        return failure;
    }

    private static void ReportSplitDisabled(Action<string>? diagnostic, string reason)
    {
        string message = $"[TestScheduling] Splitting SplitClasses is disabled, the suite runs unsplit (slower): {reason}. Check TestScheduling.Split against the xUnit version.";
        Console.Error.WriteLine(message);
        diagnostic?.Invoke(message);
    }

    private static void CollectSplits(IEnumerable<IXunitTestCase> testCases, List<(IXunitTestCase TestCase, ITestMethod Own)> moves)
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
            moves.Add((testCase, own));
        }
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
