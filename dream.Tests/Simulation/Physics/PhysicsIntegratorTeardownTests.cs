using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Xunit;
using Dream.Client.Startup;
using Dream.Client.World;
using Dream.Server;
using Dream.Simulation.Physics;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Simulation.Physics;

// Native Bullet teardown. BulletSharp 0.11.1's finalizers delete native objects in any order, so an
// undisposed world used to crash the finalizer thread inside btCollisionWorld_delete (0xC0000005)
// once its collision configuration had been finalized first. PhysicsIntegrator now keeps the world
// infrastructure off the finalizer queue: Dispose tears it down in native order, and a leaked
// integrator leaks native memory instead. These tests drive both paths under GC pressure and check
// that the owners exercised here dispose their worlds.
//
// The leak tracker is process-wide, so this class runs alone; tracking stays on while it runs.
[Collection(TimingSensitiveCollection.Name)]
public class PhysicsIntegratorTeardownTests
{
    [Fact]
    public void Leaked_Populated_Worlds_Survive_Repeated_Collection_While_Other_Worlds_Step()
    {
        using CancellationTokenSource stop = new();
        // Live worlds stepping on other threads, as other tests and the server do, while the
        // finalizer thread works through the leaked ones.
        Task[] steppers = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using PhysicsIntegrator physics = new();
                    Map map = CreatePopulatedMap(physics);
                    for (int step = 0; step < 20; step++)
                    {
                        physics.StepSimulationFixed(1f / 60f);
                    }

                    GC.KeepAlive(map);
                }
            }))
            .ToArray();

        try
        {
            for (int round = 0; round < 12; round++)
            {
                LeakPopulatedWorlds(4);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }
        finally
        {
            stop.Cancel();
            Task.WaitAll(steppers);
        }
    }

    [Fact]
    public void Disposed_Integrator_Tears_Down_Once_And_Tolerates_A_Second_Dispose()
    {
        long before = PhysicsIntegrator.LiveCount;
        PhysicsIntegrator physics = new();
        Map map = CreatePopulatedMap(physics);
        physics.StepSimulationFixed(1f / 60f);

        physics.Dispose();
        physics.Dispose();

        Assert.Empty(physics.MapCubeBodies);
        Assert.Null(physics.PlayerBody);
        GC.KeepAlive(map);
        // The count is process-wide, so this only guards against the second Dispose decrementing
        // it again; the weak-registry tests below are the precise leak checks.
        Assert.True(PhysicsIntegrator.LiveCount >= 0, $"LiveCount went negative ({PhysicsIntegrator.LiveCount}); before={before}.");
    }

    [Fact]
    public void Tracker_Names_An_Integrator_That_Was_Collected_Without_Dispose()
    {
        bool previous = PhysicsIntegrator.TrackLeaks;
        PhysicsIntegrator.TrackLeaks = true;
        try
        {
            LeakTrackedIntegrator();
            IReadOnlyList<string> leaked = CollectLeaksFromThisClass();

            Assert.Contains(leaked, stack => stack.Contains(nameof(LeakTrackedIntegrator), StringComparison.Ordinal));
        }
        finally
        {
            PhysicsIntegrator.TrackLeaks = previous;
        }
    }

    [Fact]
    public async Task Session_And_Server_Owners_Dispose_Their_Physics_Worlds()
    {
        bool previous = PhysicsIntegrator.TrackLeaks;
        PhysicsIntegrator.TrackLeaks = true;
        try
        {
            using (ClientWorldSession client = new(12, new TkVector3(5f, 1.5f, 5f), new SceneEventHub()))
            {
                client.Physics.StepSimulationFixed(1f / 60f);
            }

            using (WorldSessionRuntime runtime = new(12, new TkVector3(5f, 1.5f, 5f), new SceneEventHub()))
            {
                runtime.Physics.StepSimulationFixed(1f / 60f);
            }

            using (RuntimeAuthoritativeSessionHost authority = RuntimeAuthoritativeSessionHost.CreateDefault())
            {
                Assert.NotNull(authority.Runtime);
            }

            using (DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null))
            {
                Assert.NotNull(host.Runtime);
            }

            await using (IntegratedLocalServerHandle server = await IntegratedLocalServerHandle.StartAsync(
                0,
                () => DedicatedServerHost.CreateRuntimeAttached(persistence: null)))
            {
                Assert.True(server.IsReady, server.ErrorMessage);
            }

            // A start that fails after the host was built (its port is taken) must still dispose it.
            using (TcpListener occupied = new(IPAddress.Loopback, 0))
            {
                occupied.Start();
                int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
                await using IntegratedLocalServerHandle failed = await IntegratedLocalServerHandle.StartAsync(
                    port,
                    () => DedicatedServerHost.CreateRuntimeAttached(persistence: null));
                Assert.False(failed.IsReady);
            }

            Assert.Empty(CollectLeaksFromThisClass());
        }
        finally
        {
            PhysicsIntegrator.TrackLeaks = previous;
        }
    }

    // Integrators other classes leak while this one runs are not this class's to report.
    private static IReadOnlyList<string> CollectLeaksFromThisClass()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return PhysicsIntegrator.TakeCollectedUndisposedCreationStacks()
            .Where(stack => stack.Contains(nameof(PhysicsIntegratorTeardownTests), StringComparison.Ordinal))
            .ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LeakTrackedIntegrator()
    {
        PhysicsIntegrator physics = new();
        GC.KeepAlive(CreatePopulatedMap(physics));
    }

    // Worlds with a player body, a floor, static terrain and dynamic cubes resting on it, stepped so
    // the pair cache holds collision algorithms and contact manifolds, then dropped undisposed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LeakPopulatedWorlds(int count)
    {
        for (int i = 0; i < count; i++)
        {
            PhysicsIntegrator physics = new();
            Map map = CreatePopulatedMap(physics);
            physics.RegisterPlayerBody(
                new PhysicsFactory().CreatePlayerBody(new BulletSharp.Math.Vector3(5f, 1.5f, 5f)),
                PhysicsBodyOwnership.IntegratorOwned);
            for (int step = 0; step < 20; step++)
            {
                physics.StepSimulationFixed(1f / 60f);
            }

            GC.KeepAlive(map);
        }
    }

    private static Map CreatePopulatedMap(PhysicsIntegrator physics)
    {
        Map map = new(physics, generateStartupTerrain: true, width: 10, depth: 10);
        for (int x = 2; x < 8; x += 2)
        {
            map.TryAddCube(new TkVector3(x + 0.5f, 3f, 4.5f), withPhysics: true);
        }

        return map;
    }
}
