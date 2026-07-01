using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Simulation.Players;


public class PlayerInteraction
{
    private bool _wasPrimaryUsePressed;
    private float _primaryUseRepeatRemaining;
    private const float CubePlacementDistance = 2.0f;

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

    public bool TryCaptureBuildUseRequest(
        PlayerInteractionCommand command,
        Vector3 interactionOrigin,
        Vector3 interactionDirection,
        out BuildUseRequest request)
    {
        bool triggered = command.PrimaryUsePressed && !_wasPrimaryUsePressed;
        _wasPrimaryUsePressed = command.PrimaryUsePressed;

        if (!triggered)
        {
            request = default;
            return false;
        }

        return TryCreateBuildUseRequest(
            interactionOrigin,
            interactionDirection,
            out request);
    }

    public static bool TryCreateBuildUseRequest(
        Vector3 interactionOrigin,
        Vector3 interactionDirection,
        out BuildUseRequest request)
    {
        if (interactionDirection == Vector3.Zero)
        {
            request = default;
            return false;
        }

        request = new BuildUseRequest(
            interactionOrigin + interactionDirection * CubePlacementDistance);
        return true;
    }
}
