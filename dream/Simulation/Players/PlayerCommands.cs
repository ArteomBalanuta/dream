using OpenTK.Mathematics;

namespace Dream.Simulation.Players;


public readonly record struct MovementCommand(
    Vector3 WorldDirection,
    float VerticalDirection = 0f,
    bool JumpPressed = false,
    bool FreezePressed = false,
    bool ToggleNoclipPressed = false,
    // Magnitude of the analog move axes, clamped to 0..1 and captured before the direction is
    // normalized. A partial throttle is how the walk tier reaches the motor: the client scales
    // the axes it emits and the magnitude replicates on its own.
    float Throttle = 1f,
    bool Sprint = false);

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
    // MoveSpeed is the run cruise: the speed you move at by default. Walking and sprinting are
    // the tiers either side of it.
    float MoveSpeed,
    float NoclipSpeed,
    float JumpVelocity,
    float WalkSpeed = PlayerSimulationTuning.DefaultWalkSpeed,
    float SprintSpeed = PlayerSimulationTuning.DefaultSprintSpeed,
    float GroundAcceleration = PlayerSimulationTuning.DefaultGroundAcceleration,
    float GroundDeceleration = PlayerSimulationTuning.DefaultGroundDeceleration)
{
    public const float DefaultWalkSpeed = 1.6f;
    public const float DefaultRunSpeed = 4.3f;
    public const float DefaultSprintSpeed = 6.0f;
    public const float DefaultGroundAcceleration = 18f;
    public const float DefaultGroundDeceleration = 24f;

    // Movement values the authoritative server simulates with. Client prediction must use the
    // same ones: predicting with a local NoclipSpeed of 150 against a server at 18 pulled the
    // player back on every snapshot.
    public static PlayerSimulationTuning AuthoritativeDefault { get; } = new(
        EyeHeightOffset: 0.35f,
        MouseSensitivity: 2.5f,
        MoveSpeed: DefaultRunSpeed,
        NoclipSpeed: 18f,
        JumpVelocity: 5f);

    // Keeps client-only view settings (eye height, mouse sensitivity) and takes movement from the
    // server. Every movement field has to come across, not just the speeds: predicting with a
    // different acceleration ramp drifts from authority and gets pulled back on every snapshot
    // exactly like a mismatched speed does.
    public PlayerSimulationTuning WithAuthoritativeMovement()
    {
        return this with
        {
            MoveSpeed = AuthoritativeDefault.MoveSpeed,
            NoclipSpeed = AuthoritativeDefault.NoclipSpeed,
            JumpVelocity = AuthoritativeDefault.JumpVelocity,
            WalkSpeed = AuthoritativeDefault.WalkSpeed,
            SprintSpeed = AuthoritativeDefault.SprintSpeed,
            GroundAcceleration = AuthoritativeDefault.GroundAcceleration,
            GroundDeceleration = AuthoritativeDefault.GroundDeceleration
        };
    }

}

public readonly record struct BuildUseRequest(Vector3 PlacementTarget);
