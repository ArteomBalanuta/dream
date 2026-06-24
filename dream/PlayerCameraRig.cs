using BulletSharp;
using BulletSharp.Math;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vector3 = OpenTK.Mathematics.Vector3;

public class PlayerCameraRig
{
    public Camera Camera { get; }

    public PlayerCameraRig(Vector3 position)
    {
        Camera = new Camera(position);
    }

    public Vector3 SyncFromBody(RigidBody body, GameSettings settings)
    {
        body.MotionState.GetWorldTransform(out Matrix transform);
        Vector3 currentPosition = transform.Origin.ToOpenTK();
        Camera.Position = currentPosition + new Vector3(0f, settings.EyeHeightOffset, 0f);
        Camera.MouseSensitivity = settings.MouseSensitivity;
        return currentPosition;
    }

    public void UpdateLook(float deltaTime, MouseState mouseState, bool allowGameplayInput, bool isFrozen)
    {
        if (allowGameplayInput && !isFrozen)
        {
            Camera.Update(deltaTime, mouseState);
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
