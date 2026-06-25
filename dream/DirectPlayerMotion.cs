using OpenTK.Mathematics;

public class DirectPlayerMotion
{
    private bool _wasFreezePressed;

    public bool IsFrozen { get; private set; }

    public Vector3 Update(
        Vector3 currentPosition,
        MovementCommand command,
        GameSettings settings,
        float deltaTime,
        bool allowGameplayInput)
    {
        if (command.FreezePressed && !_wasFreezePressed)
        {
            IsFrozen = !IsFrozen;
        }

        _wasFreezePressed = command.FreezePressed;

        if (!allowGameplayInput || IsFrozen)
        {
            return currentPosition;
        }

        return currentPosition + (command.WorldDirection * settings.MoveSpeed * deltaTime);
    }
}
