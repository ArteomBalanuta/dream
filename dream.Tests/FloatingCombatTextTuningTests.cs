using OpenTK.Mathematics;
using Xunit;

public class FloatingCombatTextTuningTests
{
    [Fact]
    public void Defaults_Make_Damage_Text_Larger_And_Crits_More_Emphatic()
    {
        FloatingCombatTextTuning tuning = FloatingCombatTextTuning.Default;

        Assert.True(tuning.SizeMultiplier > 1f);
        Assert.True(tuning.CriticalDepthMultiplier > 1f);
        Assert.True(tuning.MotionSpeedMultiplier >= 1f);
    }

    [Fact]
    public void Clamp_Restricts_Runtime_Tuning_To_Safe_Ranges()
    {
        FloatingCombatTextTuning tuning = new FloatingCombatTextTuning(
            SizeMultiplier: 99f,
            MotionSpeedMultiplier: -5f,
            CriticalDepthMultiplier: 99f).Clamp();

        Assert.Equal(FloatingCombatTextTuning.MaxSizeMultiplier, tuning.SizeMultiplier);
        Assert.Equal(FloatingCombatTextTuning.MinMotionSpeedMultiplier, tuning.MotionSpeedMultiplier);
        Assert.Equal(FloatingCombatTextTuning.MaxCriticalDepthMultiplier, tuning.CriticalDepthMultiplier);
    }

    [Fact]
    public void System_Applies_Size_Speed_And_Critical_Depth_Tuning()
    {
        FloatingCombatTextSystem system = new();
        system.Spawn(1, Vector3.Zero, 100, FloatingCombatTextKind.Critical);
        system.Update(0.0375f);

        List<FloatingCombatTextRenderState> states = new();
        system.CollectRenderStates(states, new FloatingCombatTextTuning(
            SizeMultiplier: 2f,
            MotionSpeedMultiplier: 2f,
            CriticalDepthMultiplier: 3f));

        FloatingCombatTextRenderState state = Assert.Single(states);
        Assert.True(state.Scale > 2f);
        Assert.True(state.VerticalOffset > FloatingCombatTextMotion.EvaluateVerticalOffset(0.0375f));
        Assert.Equal(
            FloatingCombatTextMotion.EvaluateDepthOffset(0.075f, FloatingCombatTextMotion.MaxDepthOffset * 3f),
            state.DepthOffset,
            3);
    }

    [Fact]
    public void Critical_Text_Uses_Stronger_Style_Than_Regular_Damage()
    {
        FloatingCombatTextStyle regular = FloatingCombatTextStyles.Resolve(FloatingCombatTextKind.Regular);
        FloatingCombatTextStyle critical = FloatingCombatTextStyles.Resolve(FloatingCombatTextKind.Critical);

        Assert.True(critical.ScaleMultiplier > regular.ScaleMultiplier);
        Assert.True(critical.OutlineThickness > regular.OutlineThickness);
    }
}
