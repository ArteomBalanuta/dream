namespace Dream.Client.Menu;

// What the Multiplayer screen's fields start with, and what it hands back to be saved on Join or Host.
public sealed record MultiplayerDefaults(string Address, int JoinPort, int HostPort, string PlayerName);
