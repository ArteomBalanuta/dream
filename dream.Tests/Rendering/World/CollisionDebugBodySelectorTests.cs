using System.Runtime.CompilerServices;
using BulletSharp;
using Xunit;
using Xunit.Abstractions;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Rendering.World;

public class CollisionDebugBodySelectorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 5)]
    [InlineData(4, 64)]
    [InlineData(5, 1024)]
    [InlineData(6, -1)]
    public void Select_MatchesTheLinqSelectionOnRandomInput(int seed, int maxBodies)
    {
        Random random = new(seed);
        CollisionDebugBodySelector selector = new();
        for (int round = 0; round < 20; round++)
        {
            Camera camera = new(new TkVector3(random.Next(-3, 4), random.Next(0, 3), random.Next(-3, 4)));
            camera.SetFacing(new TkVector3((float)random.NextDouble() - 0.5f, 0f, -1f));
            RigidBody[] bodies = CreateRandomBodies(random, count: random.Next(0, 300));

            IReadOnlyList<RigidBody> expected = SelectWithLinq(bodies, camera, maxBodies);

            Assert.Equal(expected, selector.Select(bodies, camera, maxBodies));
            Assert.Equal(expected, selector.Select(bodies.Where(_ => true), camera, maxBodies));
            Assert.Equal(expected, DebugRenderer.SelectCollisionBodiesForDebug(bodies, camera, maxBodies));
        }
    }

    [Fact]
    public void Select_KeepsInputOrderForEqualDistances()
    {
        Camera camera = new(TkVector3.Zero);
        RigidBody first = CreateGeometryBody(new TkVector3(1f, 0f, -4f));
        RigidBody second = CreateGeometryBody(new TkVector3(-1f, 0f, -4f));
        RigidBody third = CreateGeometryBody(new TkVector3(0f, 1f, -4f));
        RigidBody[] bodies = [first, second, third];

        IReadOnlyList<RigidBody> selected = new CollisionDebugBodySelector().Select(bodies, camera, maxBodies: 2);

        Assert.Equal([first, second], selected);
        Assert.Equal(SelectWithLinq(bodies, camera, 2), selected);
    }

    [Fact]
    public void Select_SecondCallAllocatesNothing()
    {
        Random random = new(42);
        Camera camera = new(TkVector3.Zero);
        RigidBody[] bodies = CreateRandomBodies(random, count: 200);
        CollisionDebugBodySelector selector = new();

        long before = AllocationAssert.MeasureBytesPerIteration(
            () => GC.KeepAlive(SelectWithLinq(bodies, camera, 64)));
        long after = AllocationAssert.AtMost(
            0,
            () => GC.KeepAlive(selector.Select(bodies, camera, 64)),
            warmupIterations: 1);

        output.WriteLine($"Collision debug selection (200 bodies, max 64): LINQ {before} B/frame, selector {after} B/frame");
    }

    // The selection before MEM-6, kept as the oracle.
    private static IReadOnlyList<RigidBody> SelectWithLinq(IEnumerable<RigidBody> bodies, Camera camera, int maxBodies)
    {
        if (maxBodies <= 0)
        {
            return Array.Empty<RigidBody>();
        }

        List<(RigidBody Body, float DistanceSquared)> visibleBodies = [];
        foreach (RigidBody body in bodies)
        {
            if (!DebugRenderer.ShouldDrawCollisionBody(body) ||
                !TryResolveGeometryPosition(body, out TkVector3 position) ||
                !WorldVisibility.IsPositionVisible(camera.Position, camera.Front, position))
            {
                continue;
            }

            visibleBodies.Add((body, (position - camera.Position).LengthSquared));
        }

        return visibleBodies
            .OrderBy(entry => entry.DistanceSquared)
            .Take(maxBodies)
            .Select(entry => entry.Body)
            .ToArray();
    }

    private static bool TryResolveGeometryPosition(RigidBody body, out TkVector3 position)
    {
        if (body.UserObject is ValueTuple<GeometryCellKey?, GeometrySubcellKey?, TkVector3> geometryBody)
        {
            position = geometryBody.Item3;
            return true;
        }

        position = default;
        return false;
    }

    private static RigidBody[] CreateRandomBodies(Random random, int count)
    {
        RigidBody[] bodies = new RigidBody[count];
        for (int index = 0; index < count; index++)
        {
            // Whole-number positions make equal distances common, so tie order is exercised.
            TkVector3 position = new(random.Next(-12, 13), random.Next(-4, 5), random.Next(-40, 10));
            bodies[index] = random.Next(10) == 0
                ? CreateUndrawableBody()
                : CreateGeometryBody(position);
        }

        return bodies;
    }

    private static RigidBody CreateGeometryBody(TkVector3 position)
    {
        RigidBody body = (RigidBody)RuntimeHelpers.GetUninitializedObject(typeof(RigidBody));
        GC.SuppressFinalize(body);
        body.UserObject = ((GeometryCellKey?)new GeometryCellKey(3, 1, 3), (GeometrySubcellKey?)null, position);
        return body;
    }

    private static RigidBody CreateUndrawableBody()
    {
        RigidBody body = (RigidBody)RuntimeHelpers.GetUninitializedObject(typeof(RigidBody));
        GC.SuppressFinalize(body);
        body.UserObject = "not a debug body";
        return body;
    }
}
