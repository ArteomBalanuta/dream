using BulletSharp.Math;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

public class PlayerInteraction
{
    private bool _wasPrimaryUsePressed;
    private float _primaryUseRepeatRemaining;

    public bool ShouldTriggerPrimaryUse(PlayerInteractionCommand command, float repeatIntervalSeconds, float deltaTime)
    {
        if (!command.PrimaryUsePressed)
        {
            _wasPrimaryUsePressed = false;
            _primaryUseRepeatRemaining = 0f;
            return false;
        }

        float clampedRepeatInterval = Math.Max(0.001f, repeatIntervalSeconds);
        if (!_wasPrimaryUsePressed)
        {
            _wasPrimaryUsePressed = true;
            _primaryUseRepeatRemaining = clampedRepeatInterval;
            return true;
        }

        _primaryUseRepeatRemaining -= Math.Max(0f, deltaTime);
        if (_primaryUseRepeatRemaining > 0f)
        {
            return false;
        }

        _primaryUseRepeatRemaining = clampedRepeatInterval;
        return true;
    }

    public Cube? TryPlaceCube(Map map, bool primaryUseTriggered, Camera camera, BulletSharp.RigidBody body)
    {
        if (!primaryUseTriggered)
        {
            return null;
        }

        body.MotionState.GetWorldTransform(out Matrix transform);
        Vector3 position = transform.Origin.ToOpenTK() + camera.Front * 2.0f;
        return map.AddCube(position);
    }
}
