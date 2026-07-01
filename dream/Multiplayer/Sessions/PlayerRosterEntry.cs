namespace Dream.Multiplayer.Sessions;

public readonly record struct PlayerRosterEntry(
    string SessionId,
    int ActorId,
    string DisplayName);
