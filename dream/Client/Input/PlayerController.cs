using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Dream.Client.Presentation;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Client.Input;


public static class PlayerController
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
        bool selectCombatModePressed,
        bool sprintHeld = false)
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
                toggleNoclipPressed,
                sprintHeld),
            new PlayerLookIntent(yawDelta, pitchDelta),
            primaryUsePressed);
    }

    public static PlayerCommandFrame CaptureCommandFrame(
        PlayerSessionId sessionId,
        ulong sequence,
        KeyboardState keyboardState,
        bool allowGameplayInput) =>
        CaptureCommandFrame(sessionId, sequence, keyboardState.IsKeyDown, allowGameplayInput);

    // The same mapping over any key source, so a capture script can hold keys through the real code.
    // Walking and the primary action are not read here (see the overload below).
    public static PlayerCommandFrame CaptureCommandFrame(
        PlayerSessionId sessionId,
        ulong sequence,
        Func<Keys, bool> isKeyDown,
        bool allowGameplayInput) =>
        CaptureCommandFrame(sessionId, sequence, isKeyDown, allowGameplayInput, primaryUsePressed: false, out _);

    // W/A/S/D as a camera-space vector (X right, Y up-screen, each -1..1, not normalised), left out of the
    // frame's forward/strafe: those are facing-relative, and facing changes every tick, so the scene
    // projects the vector with Project once the tick's facing is known. primaryUsePressed is the held
    // attack button; it rides in the raw frame so GameplayInputResumeFilter sees a hold through death.
    public static PlayerCommandFrame CaptureCommandFrame(
        PlayerSessionId sessionId,
        ulong sequence,
        Func<Keys, bool> isKeyDown,
        bool allowGameplayInput,
        bool primaryUsePressed,
        out Vector2 cameraSpaceAxes)
    {
        float verticalAxis = 0f;
        if (isKeyDown(Keys.Space))
        {
            verticalAxis += 1f;
        }
        if (isKeyDown(Keys.LeftControl))
        {
            verticalAxis -= 1f;
        }

        cameraSpaceAxes = new Vector2(
            (isKeyDown(Keys.D) ? 1f : 0f) - (isKeyDown(Keys.A) ? 1f : 0f),
            (isKeyDown(Keys.W) ? 1f : 0f) - (isKeyDown(Keys.S) ? 1f : 0f));
        if (!allowGameplayInput)
        {
            cameraSpaceAxes = Vector2.Zero;
        }

        return CreateCommandFrame(
            sessionId,
            sequence,
            allowGameplayInput,
            forwardAxis: 0f,
            strafeAxis: 0f,
            verticalAxis,
            jumpPressed: isKeyDown(Keys.Space),
            freezePressed: isKeyDown(Keys.F),
            toggleNoclipPressed: isKeyDown(Keys.D0),
            yawDelta: 0f,
            pitchDelta: 0f,
            primaryUsePressed,
            selectBuildingModePressed: isKeyDown(Keys.D1),
            selectCombatModePressed: isKeyDown(Keys.D2),
            // Held sprint. LeftShift is free in gameplay: movement is W/A/S/D, Space, F, D0/D1/D2
            // and LeftControl.
            sprintHeld: isKeyDown(Keys.LeftShift));
    }

    // Camera-space axes (X right, Y up-screen) to the command's forward/strafe for a given facing. The
    // resolver's basis is facing-relative (Camera.Front is the facing), so once facing follows the
    // cursor, raw W/A/S/D would steer relative to the cursor. The play camera is the fixed isometric
    // one, so screen up/right are constant; if first/third-person input is ever revived, this needs a
    // per-view basis. Diagonals come out unit length; the facing's pitch is ignored (yaw only).
    public static (float Forward, float Strafe) Project(Vector2 cameraSpaceAxes, Vector3 facing)
    {
        Vector3 screenForward = Flatten(-IsometricView.FocusToCamera);
        Vector3 screenRight = Vector3.Normalize(Vector3.Cross(screenForward, Vector3.UnitY));
        Vector3 desired = (screenForward * cameraSpaceAxes.Y) + (screenRight * cameraSpaceAxes.X);
        if (desired.LengthSquared > 0.000001f)
        {
            desired.Normalize();
        }

        return ProjectWorld(desired, facing);
    }

    // A ground-plane direction to the forward/strafe that makes the resolver move that way for this
    // facing: its basis is (flat Front, flat Cross(Front, Up)), orthonormal, so this is a dot product each.
    public static (float Forward, float Strafe) ProjectWorld(Vector3 worldDirection, Vector3 facing)
    {
        Vector3 forward = Flatten(facing);
        if (forward == Vector3.Zero)
        {
            return (0f, 0f);
        }

        Vector3 right = Flatten(Vector3.Cross(facing, Vector3.UnitY));
        return (Vector3.Dot(worldDirection, forward), Vector3.Dot(worldDirection, right));
    }

    private static Vector3 Flatten(Vector3 direction)
    {
        Vector3 flat = new(direction.X, 0f, direction.Z);
        return flat.LengthSquared > 0.000001f ? flat.Normalized() : Vector3.Zero;
    }
}
