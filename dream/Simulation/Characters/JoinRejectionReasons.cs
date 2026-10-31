namespace Dream.Simulation.Characters;

public static class JoinRejectionReasons
{
    public const string TokenRequired = "This server needs a player identity. Update your client and try again.";
    public const string TokenInvalid = "Your player identity is not valid. Update your client and try again.";
    public const string CharacterOwned = "That character belongs to someone else. Choose another name.";
    public const string CharacterLimit = "You already have the maximum of 5 characters. Pick one of them, or ask the server admin to release one.";
    public const string NameInvalid = "Character names must be 1 to 32 characters, with no control characters.";
    public const string NameTaken = "That character name is not available. Choose another name.";
    public const string CharacterInUse = "That character is being played right now.";
    public const string AlreadyJoined = "This connection is already playing another character. Reconnect to switch.";
}
