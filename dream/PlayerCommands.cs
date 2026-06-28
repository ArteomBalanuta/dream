using OpenTK.Mathematics;

public readonly record struct MovementCommand(
    Vector3 WorldDirection,
    float VerticalDirection,
    bool JumpPressed,
    bool FreezePressed,
    bool ToggleNoclipPressed);

public readonly record struct PlayerInteractionCommand(
    bool PrimaryUsePressed,
    bool SelectBuildingModePressed,
    bool SelectCombatModePressed);
