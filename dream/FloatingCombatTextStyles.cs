using OpenTK.Mathematics;

public static class FloatingCombatTextStyles
{
    public static FloatingCombatTextStyle Resolve(FloatingCombatTextKind kind)
    {
        return kind switch
        {
            FloatingCombatTextKind.Incoming => new FloatingCombatTextStyle(
                new Vector4(1.0f, 0.0f, 0.0f, 1.0f),
                1.0f,
                50),
            _ => new FloatingCombatTextStyle(
                new Vector4(1.0f, 0.4f, 0.0f, 1.0f),
                1.0f,
                10)
        };
    }
}
