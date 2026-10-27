using OpenTK.Mathematics;
using Dream.Simulation.Combat;

namespace Dream.Rendering.Hud;


public static class FloatingCombatTextStyles
{
    public static FloatingCombatTextStyle Resolve(FloatingCombatTextKind kind)
    {
        return kind switch
        {
            FloatingCombatTextKind.Critical => new FloatingCombatTextStyle(
                new Vector4(1.0f, 0.15f, 0.15f, 1.0f),
                2.20f,
                3.0f,
                0),
            FloatingCombatTextKind.Incoming => new FloatingCombatTextStyle(
                new Vector4(1.0f, 0.0f, 0.0f, 1.0f),
                1.3f,
                2.0f,
                50),
            _ => new FloatingCombatTextStyle(
                new Vector4(1.0f, 0.85f, 0.05f, 1.0f),
                1.65f,
                1.75f,
                10)
        };
    }
}
