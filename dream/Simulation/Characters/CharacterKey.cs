namespace Dream.Simulation.Characters;

// The immutable persistence key of a character (player_profiles.player_name). Compared ordinally.
// default(CharacterKey) is "none" (IsNone). ToString is for keys and logs only.
public readonly record struct CharacterKey
{
    public CharacterKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public bool IsNone => Value is null;

    public override string ToString() => Value ?? string.Empty;
}
