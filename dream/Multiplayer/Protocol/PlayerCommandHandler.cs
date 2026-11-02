using Dream.Simulation.Players;
using OpenTK.Mathematics;

namespace Dream.Multiplayer.Protocol;

public sealed class PlayerCommandHandler : IServerMessageHandler
{
    private readonly bool _allowDevMovement;

    // allowDevMovement: whether client noclip and freeze toggles reach the simulation. Off by
    // default (dedicated servers); the integrated single-player host turns it on.
    public PlayerCommandHandler(bool allowDevMovement = false)
    {
        _allowDevMovement = allowDevMovement;
    }

    public string MessageKind => "player.command";

    public void Handle(ServerRoutingContext context, IClientToServerMessage message)
    {
        PlayerCommandMessage commandMessage = (PlayerCommandMessage)message;
        context.LastCommandMessage = commandMessage;
        if (context.LastJoinRequest == null)
        {
            return;
        }

        if (!string.Equals(
            context.LastJoinRequest.RequestedSessionId,
            commandMessage.CommandFrame.SessionId.Value,
            StringComparison.Ordinal))
        {
            return;
        }

        context.CommandInbox?.Enqueue(SanitizeCommandFrame(commandMessage.CommandFrame, _allowDevMovement));
    }

    // As below; without allowDevMovement the noclip and freeze toggles are cleared.
    public static PlayerCommandFrame SanitizeCommandFrame(PlayerCommandFrame frame, bool allowDevMovement)
    {
        PlayerCommandFrame sanitized = SanitizeCommandFrame(frame);
        PlayerMoveIntent move = sanitized.MoveIntent;
        if (allowDevMovement || (!move.FreezePressed && !move.ToggleNoclipPressed))
        {
            return sanitized;
        }

        return sanitized with { MoveIntent = move with { FreezePressed = false, ToggleNoclipPressed = false } };
    }

    // Client command frames are untrusted. Move axes become finite and are clamped to [-1, 1]: a
    // huge finite axis overflows to infinity in the movement direction and turns into NaN when it
    // is normalized, and that NaN reaches the physics body. Look deltas become finite, and a facing
    // that is non-finite or zero is dropped. Flags, mode, sequence and primary use pass through.
    public static PlayerCommandFrame SanitizeCommandFrame(PlayerCommandFrame frame)
    {
        PlayerMoveIntent move = frame.MoveIntent;
        PlayerLookIntent look = frame.LookIntent;
        PlayerMoveIntent sanitizedMove = move with
        {
            ForwardAxis = SanitizeAxis(move.ForwardAxis),
            StrafeAxis = SanitizeAxis(move.StrafeAxis),
            VerticalAxis = SanitizeAxis(move.VerticalAxis)
        };
        PlayerLookIntent sanitizedLook = new(
            Finite(look.YawDelta),
            Finite(look.PitchDelta),
            SanitizeFacing(look.Facing));

        if (sanitizedMove == move && sanitizedLook == look)
        {
            return frame;
        }

        return frame with { MoveIntent = sanitizedMove, LookIntent = sanitizedLook };
    }

    private static float SanitizeAxis(float axis)
    {
        return float.IsFinite(axis) ? Math.Clamp(axis, -1f, 1f) : 0f;
    }

    private static float Finite(float value)
    {
        return float.IsFinite(value) ? value : 0f;
    }

    private static Vector3? SanitizeFacing(Vector3? facing)
    {
        if (facing is not { } value)
        {
            return null;
        }

        bool finite = float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        return finite && value != Vector3.Zero ? value : null;
    }
}
