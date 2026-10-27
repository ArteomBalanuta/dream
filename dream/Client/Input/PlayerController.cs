using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Client.Input;


public class PlayerController
{
    public static PlayerCommandFrame CreateCommandFrame(
        PlayerSessionId sessionId,
        ulong sequence,
        bool allowGameplayInput,
        float forwardAxis,
        float strafeAxis,
        float verticalAxis,
        bool jumpPressed,
        bool freezePressed,
        bool toggleNoclipPressed,
        float yawDelta,
        float pitchDelta,
        bool primaryUsePressed,
        bool selectBuildingModePressed,
        bool selectCombatModePressed)
    {
        if (!allowGameplayInput)
        {
            return new PlayerCommandFrame(
                sessionId,
                sequence,
                PlayerModeSwitchIntent.None,
                PlayerMoveIntent.None,
                PlayerLookIntent.None,
                false);
        }

        PlayerInteractionMode? requestedMode = null;
        if (selectBuildingModePressed)
        {
            requestedMode = PlayerInteractionMode.Building;
        }
        else if (selectCombatModePressed)
        {
            requestedMode = PlayerInteractionMode.Combat;
        }

        return new PlayerCommandFrame(
            sessionId,
            sequence,
            new PlayerModeSwitchIntent(requestedMode),
            new PlayerMoveIntent(
                forwardAxis,
                strafeAxis,
                verticalAxis,
                jumpPressed,
                freezePressed,
                toggleNoclipPressed),
            new PlayerLookIntent(yawDelta, pitchDelta),
            primaryUsePressed);
    }

    public static PlayerCommandFrame CaptureCommandFrame(
        PlayerSessionId sessionId,
        ulong sequence,
        KeyboardState keyboardState,
        MouseState mouseState,
        bool allowGameplayInput)
    {
        float forwardAxis = 0f;
        if (keyboardState.IsKeyDown(Keys.W))
        {
            forwardAxis += 1f;
        }
        if (keyboardState.IsKeyDown(Keys.S))
        {
            forwardAxis -= 1f;
        }

        float strafeAxis = 0f;
        if (keyboardState.IsKeyDown(Keys.D))
        {
            strafeAxis += 1f;
        }
        if (keyboardState.IsKeyDown(Keys.A))
        {
            strafeAxis -= 1f;
        }

        float verticalAxis = 0f;
        if (keyboardState.IsKeyDown(Keys.Space))
        {
            verticalAxis += 1f;
        }
        if (keyboardState.IsKeyDown(Keys.LeftControl))
        {
            verticalAxis -= 1f;
        }

        return CreateCommandFrame(
            sessionId,
            sequence,
            allowGameplayInput,
            forwardAxis,
            strafeAxis,
            verticalAxis,
            jumpPressed: keyboardState.IsKeyDown(Keys.Space),
            freezePressed: keyboardState.IsKeyDown(Keys.F),
            toggleNoclipPressed: keyboardState.IsKeyDown(Keys.D0),
            yawDelta: mouseState.Delta.X,
            pitchDelta: mouseState.Delta.Y,
            primaryUsePressed: mouseState.IsButtonDown(MouseButton.Left),
            selectBuildingModePressed: keyboardState.IsKeyDown(Keys.D1),
            selectCombatModePressed: keyboardState.IsKeyDown(Keys.D2));
    }

    public static MovementCommand CreateMovementCommand(
        Camera camera,
        bool noclipEnabled,
        PlayerMoveIntent intent)
    {
        return PlayerMovementResolver.CreateMovementCommand(camera, noclipEnabled, intent);
    }

    public MovementCommand CaptureMovement(
        Camera camera,
        KeyboardState keyboardState,
        bool allowGameplayInput,
        bool noclipEnabled)
    {
        if (!allowGameplayInput)
        {
            return new MovementCommand(Vector3.Zero, 0f, false, false, false);
        }

        return CreateMovementCommand(
            camera,
            noclipEnabled,
            moveForward: keyboardState.IsKeyDown(Keys.W),
            moveBackward: keyboardState.IsKeyDown(Keys.S),
            moveLeft: keyboardState.IsKeyDown(Keys.A),
            moveRight: keyboardState.IsKeyDown(Keys.D),
            moveUp: keyboardState.IsKeyDown(Keys.Space),
            moveDown: keyboardState.IsKeyDown(Keys.LeftControl),
            jumpPressed: keyboardState.IsKeyDown(Keys.Space),
            freezePressed: keyboardState.IsKeyDown(Keys.F),
            toggleNoclipPressed: keyboardState.IsKeyDown(Keys.D0),
            allowGameplayInput: allowGameplayInput);
    }

    public static MovementCommand CreateMovementCommand(
        Camera camera,
        bool noclipEnabled,
        bool moveForward,
        bool moveBackward,
        bool moveLeft,
        bool moveRight,
        bool moveUp,
        bool moveDown,
        bool jumpPressed,
        bool freezePressed,
        bool toggleNoclipPressed,
        bool allowGameplayInput)
    {
        if (!allowGameplayInput)
        {
            return new MovementCommand(Vector3.Zero, 0f, false, false, false);
        }

        return CreateMovementCommand(
            camera,
            noclipEnabled,
            new PlayerMoveIntent(
                ForwardAxis: (moveForward ? 1f : 0f) + (moveBackward ? -1f : 0f),
                StrafeAxis: (moveRight ? 1f : 0f) + (moveLeft ? -1f : 0f),
                VerticalAxis: (moveUp ? 1f : 0f) + (moveDown ? -1f : 0f),
                JumpPressed: jumpPressed,
                FreezePressed: freezePressed,
                ToggleNoclipPressed: toggleNoclipPressed));
    }

    public static MovementCommand CreateMovementCommand(
        Vector3 cameraFront,
        Vector3 cameraRight,
        bool moveForward,
        bool moveBackward,
        bool moveLeft,
        bool moveRight,
        bool jumpPressed,
        bool freezePressed,
        bool allowGameplayInput)
    {
        if (!allowGameplayInput)
        {
            return new MovementCommand(Vector3.Zero, 0f, false, false, false);
        }

        Vector3 movementDirection = Vector3.Zero;
        Vector3 forward = PlayerMovementResolver.FlattenToWorldPlane(cameraFront);
        Vector3 right = PlayerMovementResolver.FlattenToWorldPlane(cameraRight);

        if (moveForward) movementDirection += forward;
        if (moveBackward) movementDirection -= forward;
        if (moveLeft) movementDirection -= right;
        if (moveRight) movementDirection += right;

        if (movementDirection != Vector3.Zero)
        {
            movementDirection = movementDirection.Normalized();
        }

        return new MovementCommand(
            movementDirection,
            0f,
            jumpPressed,
            freezePressed,
            false);
    }

    public PlayerInteractionCommand CaptureInteraction(
        KeyboardState keyboardState,
        MouseState mouseState,
        bool allowGameplayInput)
    {
        return CreateInteractionCommand(
            mouseState.IsButtonDown(MouseButton.Left),
            keyboardState.IsKeyDown(Keys.D1),
            keyboardState.IsKeyDown(Keys.D2),
            allowGameplayInput);
    }

    public static PlayerInteractionCommand CreateInteractionCommand(bool primaryUsePressed, bool allowGameplayInput)
    {
        return new PlayerInteractionCommand(
            PrimaryUsePressed: allowGameplayInput && primaryUsePressed,
            SelectBuildingModePressed: false,
            SelectCombatModePressed: false);
    }

    public static PlayerInteractionCommand CreateInteractionCommand(
        bool primaryUsePressed,
        bool selectBuildingModePressed,
        bool selectCombatModePressed,
        bool allowGameplayInput)
    {
        return new PlayerInteractionCommand(
            PrimaryUsePressed: allowGameplayInput && primaryUsePressed,
            SelectBuildingModePressed: allowGameplayInput && selectBuildingModePressed,
            SelectCombatModePressed: allowGameplayInput && selectCombatModePressed);
    }
}
