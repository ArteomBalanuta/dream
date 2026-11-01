using OpenTK.Windowing.GraphicsLibraryFramework;
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

    // Keys only: walking, looking and the primary action come from clicks (ClickMoveController).
    public static PlayerCommandFrame CaptureCommandFrame(
        PlayerSessionId sessionId,
        ulong sequence,
        KeyboardState keyboardState,
        bool allowGameplayInput) =>
        CaptureCommandFrame(sessionId, sequence, keyboardState.IsKeyDown, allowGameplayInput);

    // The same mapping over any key source, so a capture script can hold keys through the real code.
    public static PlayerCommandFrame CaptureCommandFrame(
        PlayerSessionId sessionId,
        ulong sequence,
        Func<Keys, bool> isKeyDown,
        bool allowGameplayInput)
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
            primaryUsePressed: false,
            selectBuildingModePressed: isKeyDown(Keys.D1),
            selectCombatModePressed: isKeyDown(Keys.D2));
    }
}
