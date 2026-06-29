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

public readonly record struct BuildUseRequest(Vector3 PlacementTarget);
