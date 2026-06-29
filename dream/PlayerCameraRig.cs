using BulletSharp;
using BulletSharp.Math;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

public class PlayerCameraRig
{
    public Camera Camera { get; }

    public PlayerCameraRig(Vector3 position)
    {
        Camera = new Camera(position);
    }

    public Vector3 SyncFromBody(RigidBody body, float eyeHeightOffset, float mouseSensitivity)
    {
        body.MotionState.GetWorldTransform(out Matrix transform);
        Vector3 currentPosition = transform.Origin.ToOpenTK();
        Camera.Position = currentPosition + new Vector3(0f, eyeHeightOffset, 0f);
        Camera.MouseSensitivity = mouseSensitivity;
        return currentPosition;
    }

    public void UpdateLook(float deltaTime, Vector2 lookDelta, bool allowLook)
    {
        _ = deltaTime;
        if (allowLook)
        {
            Camera.ApplyLookDelta(lookDelta);
        }
    }

    public void ApplyImmediateLook(float mouseSensitivity, Vector2 lookDelta, bool allowLook)
    {
        Camera.MouseSensitivity = mouseSensitivity;
        if (allowLook)
        {
            Camera.ApplyLookDelta(lookDelta);
        }
    }

    public void Teleport(RigidBody body, Vector3 position, float eyeHeightOffset)
    {
        Matrix transform = Matrix.Translation(position.ToBullet());
        body.WorldTransform = transform;
        body.MotionState.SetWorldTransform(ref transform);
        body.LinearVelocity = BulletSharp.Math.Vector3.Zero;
        body.AngularVelocity = BulletSharp.Math.Vector3.Zero;
        body.ClearForces();
        body.Activate(true);
        Camera.Position = position + new Vector3(0f, eyeHeightOffset, 0f);
    }
}
