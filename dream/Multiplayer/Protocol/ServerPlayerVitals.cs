namespace Dream.Multiplayer.Protocol;

public readonly record struct AbilityCooldown(string Key, ulong ReadyTick);

// M4 (protocol v17). Owner only, unprompted, outside AuthoritativeActionResult so tick goldens do not
// move. Key is a skill key or an item kind. Cooldowns lists only those still running at send time,
// at most MaxCooldowns; a client drops the rest of a longer list.
public sealed record ServerPlayerVitals(int CurrentMana, int MaxMana, IReadOnlyList<AbilityCooldown> Cooldowns) : IServerToClientMessage
{
    public const string Kind = "server.vitals";
    public const int MaxCooldowns = 32;
    public string MessageKind => Kind;
}
