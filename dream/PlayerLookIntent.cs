public readonly record struct PlayerLookIntent(
    float YawDelta,
    float PitchDelta)
{
    public static PlayerLookIntent None => new(0f, 0f);
}
