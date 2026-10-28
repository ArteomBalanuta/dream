using BulletSharp;
using Dream.Simulation.Physics;
using Xunit;
using Xunit.Abstractions;
using BVector3 = BulletSharp.Math.Vector3;
using BMatrix = BulletSharp.Math.Matrix;

namespace Dream.Tests.Simulation.Physics;

// Regression for the wave-9 native shutdown crash (an AV in Bullet's removeRigidBody inside
// DedicatedServerHost.Dispose). The crash dump showed two unrelated worlds on two threads faulting
// at the same instruction: quickprof's Start_Profile reading gCurrentNodes[34] == null. libbulletc
// gives each thread a profiler index that wraps after 63 threads, so in a process where more than
// 63 threads ever touch Bullet, two live threads share one profile tree and corrupt it. See
// BulletProfilerGuard.
public class BulletProfilerThreadIndexTests
{
    private readonly ITestOutputHelper _output;

    public BulletProfilerThreadIndexTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ConstructingAnIntegrator_DisablesTheNativeProfilerThreadIndex()
    {
        using PhysicsIntegrator physics = new();

        _output.WriteLine($"guard: {BulletProfilerGuard.Status} ({BulletProfilerGuard.Detail})");
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            != System.Runtime.InteropServices.Architecture.X64)
        {
            return;
        }

        Assert.Equal(BulletProfilerGuardStatus.Patched, BulletProfilerGuard.Status);
        // Every thread now gets the out-of-range index, so quickprof's Start/Stop_Profile return
        // before touching the shared per-index profile trees.
        Assert.Equal(BulletProfilerGuard.DisabledThreadIndex, BulletProfilerGuard.ReadCurrentThreadIndexForTest());
        int fromOtherThread = Task.Run(BulletProfilerGuard.ReadCurrentThreadIndexForTest).GetAwaiter().GetResult();
        Assert.Equal(BulletProfilerGuard.DisabledThreadIndex, fromOtherThread);
    }

    // libbulletc hands out profiler thread indices 1..63 in order and then wraps, so a thread
    // started 63 profiler-touching threads after another gets the same index. The test starts 8 hot
    // threads, then 55 short-lived threads that each make one profiled call, then 8 more hot threads:
    // without the guard each of the last 8 shares its index with one of the first 8, and the 16
    // step and edit their own worlds in parallel. Before the guard this crashed the test host with
    // 0xC0000005 in Start_Profile (Stop_Profile on one thread moved the shared node to root->Parent,
    // null, while the other thread re-read it).
    [Fact]
    public void ThreadsSharingAProfilerIndex_StepTheirOwnWorldsInParallel_WithoutANativeCrash()
    {
        using (new PhysicsIntegrator())
        {
        }

        // A short run keeps the suite cheap; DREAM_BULLET_PROFILER_STRESS_SECONDS lengthens it (with
        // DREAM_BULLET_PROFILER_GUARD=0 it reproduces the native crash).
        double seconds = double.TryParse(
            Environment.GetEnvironmentVariable("DREAM_BULLET_PROFILER_STRESS_SECONDS"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double configured) ? configured : 0.3;
        const int PairCount = 8;
        const int ProfilerIndexCount = 63;
        long deadline = 0;
        using Barrier start = new((2 * PairCount) + 1);
        Exception? failure = null;
        List<Thread> hot = [];
        int[] indices = new int[2 * PairCount];

        void StartHot(int number)
        {
            using ManualResetEventSlim indexTaken = new();
            Thread thread = new(() =>
            {
                try
                {
                    TouchProfiler();
                    if (BulletProfilerGuard.Status == BulletProfilerGuardStatus.Disabled)
                    {
                        indices[number] = BulletProfilerGuard.ReadCurrentThreadIndexForTest();
                    }

                    indexTaken.Set();
                    start.SignalAndWait();
                    RunWorld(Volatile.Read(ref deadline));
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                    indexTaken.Set();
                }
            })
            {
                IsBackground = true,
                Name = $"bullet-profiler-hot-{number}"
            };
            thread.Start();
            // Index assignment order is the creation order only if each thread takes its index
            // before the next one starts.
            indexTaken.Wait();
            hot.Add(thread);
        }

        for (int i = 0; i < PairCount; i++)
        {
            StartHot(i);
        }

        for (int i = 0; i < ProfilerIndexCount - PairCount; i++)
        {
            Thread burner = new(TouchProfiler) { IsBackground = true, Name = $"bullet-profiler-burn-{i}" };
            burner.Start();
            burner.Join();
        }

        for (int i = 0; i < PairCount; i++)
        {
            StartHot(PairCount + i);
        }

        Volatile.Write(ref deadline, System.Diagnostics.Stopwatch.GetTimestamp()
            + (long)(seconds * System.Diagnostics.Stopwatch.Frequency));
        start.SignalAndWait();
        foreach (Thread thread in hot)
        {
            thread.Join();
        }

        _output.WriteLine($"guard: {BulletProfilerGuard.Status}; hot thread profiler indices: {string.Join(",", indices)}");
        Assert.Null(failure);
    }

    // One profiled Bullet call, which makes libbulletc assign this thread a profiler index.
    private static void TouchProfiler()
    {
        using DefaultCollisionConfiguration configuration = new();
        using CollisionDispatcher dispatcher = new(configuration);
        using DbvtBroadphase broadphase = new();
        using SequentialImpulseConstraintSolver solver = new();
        using DiscreteDynamicsWorld world = new(dispatcher, broadphase, solver, configuration);
        world.StepSimulation(1f / 64f, 1, 1f / 64f);
    }

    // Mostly native time inside profiled zones: a pile of boxes that never sleeps, stepped every
    // iteration, with one box removed and re-added every few steps.
    private static void RunWorld(long deadline)
    {
        using DefaultCollisionConfiguration configuration = new();
        using CollisionDispatcher dispatcher = new(configuration);
        using DbvtBroadphase broadphase = new();
        using SequentialImpulseConstraintSolver solver = new();
        using DiscreteDynamicsWorld world = new(dispatcher, broadphase, solver, configuration);
        world.Gravity = new BVector3(0, -9.8f, 0);

        using BoxShape groundShape = new(10f, 0.5f, 10f);
        using RigidBodyConstructionInfo groundInfo = new(0f, null, groundShape);
        using RigidBody ground = new(groundInfo);
        world.AddRigidBody(ground);

        using BoxShape boxShape = new(0.5f);
        BVector3 inertia = boxShape.CalculateLocalInertia(1f);
        RigidBody[] boxes = new RigidBody[24];
        for (int i = 0; i < boxes.Length; i++)
        {
            using RigidBodyConstructionInfo info = new(1f, null, boxShape, inertia)
            {
                StartWorldTransform = BMatrix.Translation(i % 4, 1f + (i / 4), (i % 3) * 0.5f)
            };
            boxes[i] = new RigidBody(info) { ActivationState = ActivationState.DisableDeactivation };
            world.AddRigidBody(boxes[i]);
        }

        for (int step = 0; step < 100 || System.Diagnostics.Stopwatch.GetTimestamp() < deadline; step++)
        {
            world.StepSimulation(1f / 64f, 1, 1f / 64f);
            if (step % 4 == 0)
            {
                RigidBody box = boxes[(step / 4) % boxes.Length];
                world.RemoveRigidBody(box);
                box.WorldTransform = BMatrix.Translation(step % 4, 6f, 0f);
                world.AddRigidBody(box);
            }
        }

        foreach (RigidBody box in boxes)
        {
            world.RemoveRigidBody(box);
            box.Dispose();
        }

        world.RemoveRigidBody(ground);
    }
}
