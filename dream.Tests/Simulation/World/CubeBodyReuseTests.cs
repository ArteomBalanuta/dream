using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Simulation.Physics;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.World;

// Replicated CubeMoved deltas arrive every server tick per moving cube. Applying one used to
// dispose and recreate the cube's Bullet body (shape, motion state, body) each time.
public class CubeBodyReuseTests
{
    [Fact]
    public void UpdateCubePosition_SameCell_KeepsBodyInstanceAtNewCenter()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 8, depth: 8);
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 1f, 3f)));
        BulletSharp.RigidBody bodyBefore = FindCubeBody(physics, cube);
        int bodyCount = physics.MapCubeBodies.Count;

        Vector3 center = new(3.72f, 1.5f, 3.18f);
        map.UpdateCubePosition(cube, center, syncPhysics: true);

        Assert.Same(bodyBefore, FindCubeBody(physics, cube));
        Assert.Equal(bodyCount, physics.MapCubeBodies.Count);
        AssertBodyAt(bodyBefore, center);
        Assert.Equal((cube, center), (ValueTuple<Cube, Vector3>)bodyBefore.UserObject);
    }

    [Fact]
    public void UpdateCubePosition_CrossCell_KeepsBodyAndReindexesItUnderNewCell()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 8, depth: 8);
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 1f, 3f)));
        BulletSharp.RigidBody bodyBefore = FindCubeBody(physics, cube);
        int bodyCount = physics.MapCubeBodies.Count;

        Vector3 center = new(4.4f, 1.5f, 3.5f);
        map.UpdateCubePosition(cube, center, syncPhysics: true);
        Assert.Same(cube, map.TryGetCubeAtCell(4, 1, 3));

        Assert.Same(bodyBefore, FindCubeBody(physics, cube));
        Assert.Equal(bodyCount, physics.MapCubeBodies.Count);
        AssertBodyAt(bodyBefore, center);

        // Removal looks the body up by its new cell key; a stale index would leave it in the world.
        Assert.True(map.RemoveCube(cube));
        Assert.DoesNotContain(bodyBefore, physics.MapCubeBodies);
    }

    [Fact]
    public void UpdateCubePosition_CubeWithoutBody_FallsBackToCreatingOne()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 8, depth: 8);
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 1f, 3f), withPhysics: false));
        Assert.DoesNotContain(physics.MapCubeBodies, body => IsCubeBody(body, cube));

        Vector3 center = new(3.6f, 1.5f, 3.4f);
        map.UpdateCubePosition(cube, center, syncPhysics: true);

        AssertBodyAt(FindCubeBody(physics, cube), center);
    }

    [Fact]
    public void ReplicatedMirror_RepeatedCubeMovedDeltas_ReuseOneKinematicBody()
    {
        using ClientWorldSession mirror = new(
            12,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub());
        RemoteWorldMutationApplier.ApplyWorldMutations(
            mirror.Map,
            [new WorldMutationDelta(WorldMutationKind.CubePlaced, new Vector3(8f, 1f, 2f))]);
        Cube cube = Assert.IsType<Cube>(mirror.Map.TryGetCubeAtCell(8, 1, 2));
        BulletSharp.RigidBody body = FindCubeBody(mirror.Physics, cube);
        int bodyCount = mirror.Physics.MapCubeBodies.Count;

        Vector3 source = new(8f, 1f, 2f);
        for (int step = 1; step <= 6; step++)
        {
            Vector3 center = new(8.5f + (step * 0.25f), 1.5f, 2.5f);
            RemoteWorldMutationApplier.ApplyWorldMutations(mirror.Map, [WorldMutationDelta.Moved(source, center)]);
            source = new Vector3(MathF.Floor(center.X), 1f, 2f);

            Assert.Same(body, FindCubeBody(mirror.Physics, cube));
            AssertBodyAt(body, center);
        }

        Assert.Equal(bodyCount, mirror.Physics.MapCubeBodies.Count);
        Assert.True(body.CollisionFlags.HasFlag(BulletSharp.CollisionFlags.KinematicObject));
        Assert.Same(cube, mirror.Map.TryGetCubeAtCell(10, 1, 2));
    }

    private static BulletSharp.RigidBody FindCubeBody(PhysicsIntegrator physics, Cube cube)
    {
        return Assert.Single(physics.MapCubeBodies, body => IsCubeBody(body, cube));
    }

    private static bool IsCubeBody(BulletSharp.RigidBody body, Cube cube)
    {
        return body.UserObject is ValueTuple<Cube, Vector3> cubeBody && ReferenceEquals(cubeBody.Item1, cube);
    }

    private static void AssertBodyAt(BulletSharp.RigidBody body, Vector3 center)
    {
        body.MotionState.GetWorldTransform(out BulletSharp.Math.Matrix transform);
        Assert.Equal(center.X, transform.Origin.X, 3);
        Assert.Equal(center.Y, transform.Origin.Y, 3);
        Assert.Equal(center.Z, transform.Origin.Z, 3);
        Assert.Equal(center.X, body.WorldTransform.Origin.X, 3);
        Assert.Equal(center.Z, body.WorldTransform.Origin.Z, 3);
    }
}
