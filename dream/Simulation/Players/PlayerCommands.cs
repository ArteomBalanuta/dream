using OpenTK.Mathematics;
using Dream.Application;

namespace Dream.Simulation.Players;


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
    // Movement values the authoritative server simulates with. Client prediction must use the
    // same ones: predicting with a local NoclipSpeed of 150 against a server at 18 pulled the
    // player back on every snapshot.
    public static PlayerSimulationTuning AuthoritativeDefault { get; } = new(
        EyeHeightOffset: 0.35f,
        MouseSensitivity: 2.5f,
        MoveSpeed: 6f,
        NoclipSpeed: 18f,
        JumpVelocity: 5f);

    // Keeps client-only view settings (eye height, mouse sensitivity) and takes movement from the server.
    public PlayerSimulationTuning WithAuthoritativeMovement()
    {
        return this with
        {
            MoveSpeed = AuthoritativeDefault.MoveSpeed,
            NoclipSpeed = AuthoritativeDefault.NoclipSpeed,
            JumpVelocity = AuthoritativeDefault.JumpVelocity
        };
    }

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
