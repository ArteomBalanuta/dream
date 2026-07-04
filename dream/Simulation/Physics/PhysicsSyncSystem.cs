using BulletSharp;
using BulletSharp.Math;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Physics;


public class PhysicsSyncSystem
{
    public void SyncDynamicMapBodies(Map map, IEnumerable<RigidBody> dynamicBodies)
    {
        RigidBody[] bodies = dynamicBodies as RigidBody[] ?? dynamicBodies.ToArray();

        foreach (RigidBody body in bodies)
        {
            if (body.UserObject is (Cube cube, Vector3))
            {
                body.MotionState.GetWorldTransform(out Matrix transform);
                map.UpdateCubePosition(cube, transform.Origin.ToOpenTK());
                continue;
            }

            if (body.UserObject is GeometryDynamicBodyReference dynamicGeometry)
            {
                body.MotionState.GetWorldTransform(out Matrix transform);
                map.UpdateFracturePiecePosition(
                    dynamicGeometry.CellKey,
                    dynamicGeometry.PieceIndex,
                    transform.Origin.ToOpenTK());
            }
        }
    }
}
