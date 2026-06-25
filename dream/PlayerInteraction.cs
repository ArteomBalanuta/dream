using BulletSharp.Math;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

public class PlayerInteraction
{
    private bool _wasPrimaryUsePressed;

    public bool CapturePrimaryUse(PlayerInteractionCommand command)
    {
        bool triggered = command.PlaceCubePressed && !_wasPrimaryUsePressed;
        _wasPrimaryUsePressed = command.PlaceCubePressed;
        return triggered;
    }

    public void TryPlaceCube(Map map, bool primaryUseTriggered, Camera camera, BulletSharp.RigidBody body)
    {
        if (!primaryUseTriggered)
        {
            return;
        }

        body.MotionState.GetWorldTransform(out Matrix transform);
        Vector3 position = transform.Origin.ToOpenTK() + camera.Front * 2.0f;
        map.AddCube(position);
    }

    public void TryPlaceCube(Map map, bool primaryUseTriggered, Camera camera, Vector3 worldPosition)
    {
        if (!primaryUseTriggered)
        {
            return;
        }

        Vector3 position = worldPosition + camera.Front * 2.0f;
        map.AddCube(position, withPhysics: false);
    }
}
