using OpenTK.Mathematics;

public readonly record struct MovementCommand(
    Vector3 WorldDirection,
    float VerticalDirection = 0f,
    bool JumpPressed = false,
    bool FreezePressed = false,
    bool ToggleNoclipPressed = false);

public readonly record struct PlayerInteractionCommand(
    bool PrimaryUsePressed,
    bool SelectBuildingModePressed = false,
    bool SelectCombatModePressed = false)
{
    public bool PlaceCubePressed => PrimaryUsePressed;
}

public readonly record struct PlayerSimulationInput(
    MovementCommand Movement,
    Vector2 LookDelta,
    bool AllowGameplayInput,
    bool AllowLook = true);

public readonly record struct PlayerSimulationTuning(
    float EyeHeightOffset,
    float MouseSensitivity,
    float MoveSpeed,
    float NoclipSpeed,
    float JumpVelocity)
{
    public static PlayerSimulationTuning FromSettings(GameSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new PlayerSimulationTuning(
            settings.EyeHeightOffset,
            settings.MouseSensitivity,
            settings.MoveSpeed,
            settings.NoclipSpeed,
            settings.JumpVelocity);
    }
}

public readonly record struct BuildUseRequest(Vector3 PlacementTarget);
