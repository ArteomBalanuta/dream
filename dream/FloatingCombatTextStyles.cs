using OpenTK.Mathematics;

public static class FloatingCombatTextStyles
{
    public static FloatingCombatTextStyle Resolve(FloatingCombatTextKind kind)
    {
        return kind switch
        {
            FloatingCombatTextKind.Critical => new FloatingCombatTextStyle(
                new Vector4(1.0f, 0.15f, 0.15f, 1.0f),
                1.75f,
                0),
            FloatingCombatTextKind.Incoming => new FloatingCombatTextStyle(
                new Vector4(1.0f, 0.0f, 0.0f, 1.0f),
                1.3f,
                50),
            _ => new FloatingCombatTextStyle(
                new Vector4(1.0f, 0.4f, 0.0f, 1.0f),
                1.5f,
                10)
        };
    }
}
