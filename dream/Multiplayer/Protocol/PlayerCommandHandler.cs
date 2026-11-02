using Dream.Simulation.Players;
using OpenTK.Mathematics;

namespace Dream.Multiplayer.Protocol;

public sealed class PlayerCommandHandler : IServerMessageHandler
{
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

        PlayerCommandFrame sanitized = SanitizeCommandFrame(commandMessage.CommandFrame);
        if (context.Authority?.DevCommandsAllowed != true)
        {
            // Noclip and freeze are development commands; a server that has not enabled them
            // drops the flags and keeps the rest of the frame.
            PlayerMoveIntent move = sanitized.MoveIntent;
            if (move.FreezePressed || move.ToggleNoclipPressed)
            {
                sanitized = sanitized with { MoveIntent = move with { FreezePressed = false, ToggleNoclipPressed = false } };
            }
        }

        context.CommandInbox?.Enqueue(sanitized);
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
