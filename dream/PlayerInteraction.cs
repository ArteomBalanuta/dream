using BulletSharp.Math;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

public class PlayerInteraction
{
    private bool _wasPlaceCubePressed;

    public void TryPlaceCube(Map map, PlayerInteractionCommand command, Camera camera, BulletSharp.RigidBody body)
    {
        if (command.PlaceCubePressed && !_wasPlaceCubePressed)
        {
            body.MotionState.GetWorldTransform(out Matrix transform);
            Vector3 position = transform.Origin.ToOpenTK() + camera.Front * 2.0f;
            map.AddCube(position);
        }

        _wasPlaceCubePressed = command.PlaceCubePressed;
    }
}
