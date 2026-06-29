public readonly record struct PlayerSessionId(string Value)
{
    public static PlayerSessionId LocalDefault => new("local-player-1");

    public override string ToString() => Value;
}
