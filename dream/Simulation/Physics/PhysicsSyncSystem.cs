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
        List<PendingCubeSync> pendingCubeSyncs = [];
        List<PendingFracturePieceSync> pendingFracturePieceSyncs = [];

        foreach (RigidBody body in bodies)
        {
            if (body.UserObject is (Cube cube, Vector3))
            {
                body.MotionState.GetWorldTransform(out Matrix transform);
                pendingCubeSyncs.Add(new PendingCubeSync(
                    body,
                    cube,
                    transform.Origin.ToOpenTK()));
                continue;
            }

            if (body.UserObject is GeometryDynamicBodyReference dynamicGeometry)
            {
                body.MotionState.GetWorldTransform(out Matrix transform);
                pendingFracturePieceSyncs.Add(new PendingFracturePieceSync(
                    dynamicGeometry.CellKey,
                    dynamicGeometry.PieceIndex,
                    transform.Origin.ToOpenTK()));
            }
        }

        HashSet<GeometryCellKey> reservedTargetCells = [];
        foreach (PendingCubeSync pendingSync in pendingCubeSyncs)
        {
            map.UpdateCubePosition(
                pendingSync.Cube,
                pendingSync.Position,
                reservedTargetCells,
                syncPhysics: false);
            pendingSync.Body.UserObject = (pendingSync.Cube, pendingSync.Position);
        }

        foreach (PendingFracturePieceSync pendingSync in pendingFracturePieceSyncs)
        {
            map.UpdateFracturePiecePosition(
                pendingSync.CellKey,
                pendingSync.PieceIndex,
                pendingSync.Center);
        }
    }

    private readonly record struct PendingCubeSync(
        RigidBody Body,
        Cube Cube,
        Vector3 Position);

    private readonly record struct PendingFracturePieceSync(
        GeometryCellKey CellKey,
        int PieceIndex,
        Vector3 Center);
}
