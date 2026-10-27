using BulletSharp;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Simulation.Physics;

// WorldPhysicsBinder moves and reindexes a compatibility cube's live body on every synced
// replicated move, and WorldSpatialIndex re-sectors every moved placed cube. Bytes per call,
// Debug, before MEM-10 and after:
//   TryMoveCompatibilityCubeBody, same cell                      200 -> 0
//   TryMoveCompatibilityCubeBody, cell change (sync on)          552 -> 0
//   ReindexCompatibilityCubeBody, cell change (sync off)         376 -> 0
//   WorldSpatialIndex.UpdatePosition, lone cube crossing sectors  88 -> 0
public class WorldPhysicsBinderAllocationTests
{
    private static readonly GeometryCellKey EvenKey = new(3, 2, 3);
    private static readonly GeometryCellKey OddKey = new(4, 2, 3);
    private static readonly Vector3 EvenCenter = new(3.4f, 2.5f, 3.5f);

    private readonly ITestOutputHelper _output;

    public WorldPhysicsBinderAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryMoveCompatibilityCubeBody_SameCell_DoesNotAllocate(bool replicated)
    {
        using PhysicsIntegrator physics = new();
        (WorldPhysicsBinder binder, Cube cube, _) = CreateBoundCube(physics, replicated);
        Vector3 odd = new(3.6f, 2.5f, 3.5f);
        int toggle = 0;

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            bool toOdd = (toggle++ & 1) == 0;
            Assert.True(binder.TryMoveCompatibilityCubeBody(cube, EvenKey, EvenKey, toOdd ? odd : EvenCenter));
        });

        _output.WriteLine($"same-cell synced move, replicated={replicated}: {bytes} B");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryMoveCompatibilityCubeBody_CellChange_DoesNotAllocate(bool replicated)
    {
        using PhysicsIntegrator physics = new();
        (WorldPhysicsBinder binder, Cube cube, _) = CreateBoundCube(physics, replicated);
        Vector3 odd = new(4.6f, 2.5f, 3.5f);
        int toggle = 0;

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            bool toOdd = (toggle++ & 1) == 0;
            Assert.True(binder.TryMoveCompatibilityCubeBody(
                cube,
                toOdd ? EvenKey : OddKey,
                toOdd ? OddKey : EvenKey,
                toOdd ? odd : EvenCenter));
        });

        _output.WriteLine($"cell-change synced move, replicated={replicated}: {bytes} B");
    }

    [Fact]
    public void ReindexCompatibilityCubeBody_CellChange_DoesNotAllocate()
    {
        using PhysicsIntegrator physics = new();
        (WorldPhysicsBinder binder, Cube cube, _) = CreateBoundCube(physics, replicated: false);
        int toggle = 0;

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            bool toOdd = (toggle++ & 1) == 0;
            binder.ReindexCompatibilityCubeBody(cube, toOdd ? EvenKey : OddKey, toOdd ? OddKey : EvenKey);
        });

        _output.WriteLine($"cell-change unsynced reindex: {bytes} B");
    }

    [Fact]
    public void WorldSpatialIndex_LoneCubeCrossingSectors_DoesNotAllocate()
    {
        WorldSpatialIndex index = new();
        Cube cube = new(isEmpty: false, isSolid: true) { Position = new Vector3(15.5f, 1f, 3f) };
        index.Add(cube);
        Vector3 inFirst = new(15.5f, 1f, 3f);
        Vector3 inSecond = new(16.5f, 1f, 3f);
        int toggle = 0;

        long bytes = AllocationAssert.AtMost(0, () =>
            index.UpdatePosition(cube, (toggle++ & 1) == 0 ? inSecond : inFirst));

        _output.WriteLine($"lone cube sector crossing: {bytes} B");
    }

    [Fact]
    public void WorldSpatialIndex_SectorCrossing_KeepsNearbyResultsExact()
    {
        WorldSpatialIndex index = new();
        Cube cube = new(isEmpty: false, isSolid: true) { Position = new Vector3(15.5f, 1f, 3f) };
        index.Add(cube);

        for (int i = 0; i < 4; i++)
        {
            index.UpdatePosition(cube, new Vector3(40.5f, 1f, 3f));
            Assert.Empty(index.GetNearby(new Vector3(8f, 1f, 8f), 1f));
            Assert.Equal([cube], index.GetNearby(new Vector3(40f, 1f, 8f), 1f));

            index.UpdatePosition(cube, new Vector3(15.5f, 1f, 3f));
            Assert.Equal([cube], index.GetNearby(new Vector3(8f, 1f, 8f), 1f));
            Assert.Empty(index.GetNearby(new Vector3(40f, 1f, 8f), 1f));
        }

        Assert.True(index.Remove(cube));
        Assert.Empty(index.GetNearby(new Vector3(8f, 1f, 8f), 1f));
        Assert.False(index.Remove(cube));

        // A pooled list handed to a new sector must start empty.
        Cube other = new(isEmpty: false, isSolid: true) { Position = new Vector3(40.5f, 1f, 3f) };
        index.Add(other);
        index.Add(cube);
        Assert.Equal([other], index.GetNearby(new Vector3(40f, 1f, 8f), 1f));
        Assert.Equal([cube], index.GetNearby(new Vector3(8f, 1f, 8f), 1f));
    }

    [Fact]
    public void TryMoveCompatibilityCubeBody_StoresTheExactCenterInTheBodysOwnTuple()
    {
        using PhysicsIntegrator physics = new();
        WorldPhysicsBinder binder = new(physics);
        Cube cube = new(isEmpty: false, isSolid: true) { Position = EvenCenter };
        Cube neighbour = new(isEmpty: false, isSolid: true) { Position = EvenCenter };
        binder.SyncCellCollisionBodies(EvenKey, [Descriptor(EvenKey, neighbour), Descriptor(EvenKey, cube)]);
        RigidBody neighbourBody = physics.MapCubeBodies[0];
        RigidBody body = physics.MapCubeBodies[1];
        object neighbourUser = neighbourBody.UserObject;

        Vector3 moved = new(3.7f, 2.5f, 3.5f);
        Assert.True(binder.TryMoveCompatibilityCubeBody(cube, EvenKey, EvenKey, moved));
        (Cube userCube, Vector3 userCenter) = Assert.IsType<ValueTuple<Cube, Vector3>>(body.UserObject);
        Assert.Same(cube, userCube);
        Assert.Equal(moved, userCenter);

        // -0 and +0 compare equal but are different bits; the stored center takes the new bits.
        Assert.True(binder.TryMoveCompatibilityCubeBody(cube, EvenKey, EvenKey, new Vector3(3.7f, 2.5f, 0f)));
        Assert.True(binder.TryMoveCompatibilityCubeBody(cube, EvenKey, EvenKey, new Vector3(3.7f, 2.5f, -0f)));
        (_, Vector3 zeroCenter) = Assert.IsType<ValueTuple<Cube, Vector3>>(body.UserObject);
        Assert.True(float.IsNegative(zeroCenter.Z));

        // The other cube's body in the same cell keeps its own tuple and value.
        Assert.Same(neighbourUser, neighbourBody.UserObject);
        (Cube otherCube, Vector3 otherCenter) = Assert.IsType<ValueTuple<Cube, Vector3>>(neighbourBody.UserObject);
        Assert.Same(neighbour, otherCube);
        Assert.Equal(EvenCenter, otherCenter);
    }

    [Fact]
    public void TryMoveCompatibilityCubeBody_CellChanges_ReindexBodyAndKeepIdentity()
    {
        using PhysicsIntegrator physics = new();
        (WorldPhysicsBinder binder, Cube cube, RigidBody body) = CreateBoundCube(physics, replicated: false);
        Vector3 odd = new(4.6f, 2.5f, 3.5f);

        for (int i = 0; i < 3; i++)
        {
            Assert.True(binder.TryMoveCompatibilityCubeBody(cube, EvenKey, OddKey, odd));
            Assert.False(binder.TryMoveCompatibilityCubeBody(cube, EvenKey, EvenKey, EvenCenter));
            Assert.True(binder.TryMoveCompatibilityCubeBody(cube, OddKey, EvenKey, EvenCenter));
            Assert.False(binder.TryMoveCompatibilityCubeBody(cube, OddKey, OddKey, odd));
        }

        Assert.Same(body, Assert.Single(physics.DynamicMapCubeBodies));
        Assert.Contains(body, physics.MapCubeBodies);

        // The emptied (and possibly recycled) old-cell list no longer owns the body.
        binder.RemoveCellCollisionBodies(OddKey);
        Assert.Contains(body, physics.MapCubeBodies);
        binder.RemoveCellCollisionBodies(EvenKey);
        Assert.DoesNotContain(body, physics.MapCubeBodies);
        Assert.Empty(physics.DynamicMapCubeBodies);
    }

    [Fact]
    public void ReindexCompatibilityCubeBody_KeepsOtherBodiesAndOrderInTheOldCell()
    {
        using PhysicsIntegrator physics = new();
        WorldPhysicsBinder binder = new(physics);
        Cube moving = new(isEmpty: false, isSolid: true) { Position = EvenCenter };
        Cube staying = new(isEmpty: false, isSolid: true) { Position = EvenCenter };
        binder.SyncCellCollisionBodies(
            EvenKey,
            [Descriptor(EvenKey, staying), Descriptor(EvenKey, moving), Descriptor(EvenKey, staying)]);
        Assert.Equal(3, physics.MapCubeBodies.Count);

        binder.ReindexCompatibilityCubeBody(moving, EvenKey, OddKey);

        // Removing the old cell drops exactly the two bodies that stayed behind.
        binder.RemoveCellCollisionBodies(EvenKey);
        RigidBody remaining = Assert.Single(physics.MapCubeBodies);
        (Cube owner, _) = Assert.IsType<ValueTuple<Cube, Vector3>>(remaining.UserObject);
        Assert.Same(moving, owner);
        binder.RemoveCellCollisionBodies(OddKey);
        Assert.Empty(physics.MapCubeBodies);
    }

    private static (WorldPhysicsBinder Binder, Cube Cube, RigidBody Body) CreateBoundCube(
        PhysicsIntegrator physics,
        bool replicated)
    {
        WorldPhysicsBinder binder = new(physics, replicated);
        Cube cube = new(isEmpty: false, isSolid: true) { Position = EvenCenter };
        binder.SyncCellCollisionBodies(EvenKey, [Descriptor(EvenKey, cube)]);
        RigidBody body = Assert.Single(physics.MapCubeBodies);
        Assert.Equal(replicated, body.CollisionFlags.HasFlag(CollisionFlags.KinematicObject));
        return (binder, cube, body);
    }

    private static GeometryCollisionBodyDescriptor Descriptor(GeometryCellKey key, Cube cube)
    {
        return new GeometryCollisionBodyDescriptor(
            GeometryCollisionBodyKind.Cell,
            cube.Position,
            new Vector3(0.5f),
            IsStatic: false,
            CellKey: key,
            CompatibilityCube: cube);
    }
}
