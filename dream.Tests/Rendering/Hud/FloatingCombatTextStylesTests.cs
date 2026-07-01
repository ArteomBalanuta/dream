using Xunit;
using Dream.Rendering.Hud;

namespace Dream.Tests.Rendering.Hud;


public class FloatingCombatTextStylesTests
{
    [Fact]
    public void Resolve_RegularDamage_Uses_Larger_Than_Default_Scale()
    {
        FloatingCombatTextStyle style = FloatingCombatTextStyles.Resolve(FloatingCombatTextKind.Regular);

        Assert.True(style.ScaleMultiplier > 1f);
    }

    [Fact]
    public void Resolve_CriticalDamage_Uses_Red_High_Priority_Style()
    {
        FloatingCombatTextStyle style = FloatingCombatTextStyles.Resolve(FloatingCombatTextKind.Critical);

        Assert.Equal(1.0f, style.Color.X, 3);
        Assert.Equal(0.15f, style.Color.Y, 3);
        Assert.Equal(0.15f, style.Color.Z, 3);
        Assert.Equal(0, style.LayerPriority);
    }
}
