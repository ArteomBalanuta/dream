using BulletSharp;
using BulletSharp.Math;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Xunit;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Simulation.Physics;

public class PhysicsSyncSystemTests
{
    [Fact]
    public void SyncDynamicMapBodies_SnapshotsBodiesBeforeIterating()
    {
        Map map = new(physics: null, loadVisualAssets: false, width: 8, depth: 8);
        map.AddCube(new TkVector3(3f, 1f, 3f), withPhysics: false);
        Cube cube = Assert.IsType<Cube>(map.Cubes[3, 1, 3]);

        List<RigidBody> bodies = [];
        bool allowMutation = false;
        RigidBody firstBody = CreateBody(
            new BulletSharp.Math.Vector3(4.1f, 1.5f, 3.5f),
            onGetWorldTransform: () =>
            {
                if (allowMutation && bodies.Count > 1)
                {
                    bodies.RemoveAt(1);
                }
            });
        firstBody.UserObject = (cube, cube.Position);

        RigidBody secondBody = CreateBody(new BulletSharp.Math.Vector3(8.5f, 1.5f, 8.5f));
        bodies.Add(firstBody);
        bodies.Add(secondBody);
        allowMutation = true;
        PhysicsSyncSystem sync = new();

        sync.SyncDynamicMapBodies(map, bodies);

        Assert.Null(map.TryGetCubeAtCell(3, 1, 3));
        Assert.Same(cube, map.TryGetCubeAtCell(4, 1, 3));
    }

    private static RigidBody CreateBody(
        BulletSharp.Math.Vector3 position,
        Action? onGetWorldTransform = null)
    {
        MotionState motionState = new CallbackMotionState(position, onGetWorldTransform);
        BoxShape shape = new(0.5f, 0.5f, 0.5f);
        RigidBodyConstructionInfo rbInfo = new(1f, motionState, shape, BulletSharp.Math.Vector3.Zero);
        return new RigidBody(rbInfo)
        {
            UserObject = null
        };
    }

    private sealed class CallbackMotionState : MotionState
    {
        private Matrix _worldTransform;
        private readonly Action? _onGetWorldTransform;

        public CallbackMotionState(BulletSharp.Math.Vector3 position, Action? onGetWorldTransform)
        {
            _worldTransform = Matrix.Translation(position);
            _onGetWorldTransform = onGetWorldTransform;
        }

        public override void GetWorldTransform(out Matrix worldTrans)
        {
            _onGetWorldTransform?.Invoke();
            worldTrans = _worldTransform;
        }

        public override void SetWorldTransform(ref Matrix worldTrans)
        {
            _worldTransform = worldTrans;
        }
    }
}
