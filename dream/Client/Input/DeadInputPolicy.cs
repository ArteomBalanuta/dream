namespace Dream.Client.Input;

// Whether the local player's keyboard and mouse reach the command. A dead player sends no movement,
// look or use, but the command itself is still built and sent with its tick's sequence: the
// reconciler only compares on a new ack, and the respawn snap needs one.
public static class DeadInputPolicy
{
    public static bool AllowGameplayInput(bool settingsAllowGameplayInput, bool localPlayerIsAlive)
        => settingsAllowGameplayInput && localPlayerIsAlive;
}
