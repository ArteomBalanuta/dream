using OpenTK.Mathematics;
using Xunit;

public class FloatingCombatTextMotionTests
{
    [Fact]
    public void EvaluateScale_StartsSmall_Peaks_Then_Settles()
    {
        float start = FloatingCombatTextMotion.EvaluateScale(0f, 1f);
        float peak = FloatingCombatTextMotion.EvaluateScale(0.075f, 1f);
        float settled = FloatingCombatTextMotion.EvaluateScale(0.15f, 1f);

        Assert.Equal(0.2f, start, 3);
        Assert.True(peak > 1f);
        Assert.Equal(1f, settled, 3);
    }

    [Fact]
    public void EvaluateVerticalOffset_Uses_EaseOut_Progression()
    {
        float start = FloatingCombatTextMotion.EvaluateVerticalOffset(0f);
        float middle = FloatingCombatTextMotion.EvaluateVerticalOffset(0.30f);
        float end = FloatingCombatTextMotion.EvaluateVerticalOffset(0.60f);

        Assert.Equal(0f, start, 3);
        Assert.True(middle > 0.5f);
        Assert.Equal(1f, end, 3);
    }

    [Fact]
    public void EvaluateAlpha_Remains_Opaque_Before_Fade_Then_Reaches_Zero()
    {
        Assert.Equal(1f, FloatingCombatTextMotion.EvaluateAlpha(0.59f), 3);
        Assert.True(FloatingCombatTextMotion.EvaluateAlpha(0.80f) < 1f);
        Assert.Equal(0f, FloatingCombatTextMotion.EvaluateAlpha(1.0f), 3);
    }

    [Fact]
    public void EvaluateDepthOffset_Bursts_Forward_Then_Settles_Back()
    {
        float start = FloatingCombatTextMotion.EvaluateDepthOffset(0f);
        float middle = FloatingCombatTextMotion.EvaluateDepthOffset(0.05f);
        float end = FloatingCombatTextMotion.EvaluateDepthOffset(FloatingCombatTextMotion.BurstDuration);

        Assert.Equal(0f, start, 3);
        Assert.True(middle > 0f);
        Assert.Equal(0f, end, 3);
    }
}

public class FloatingCombatTextSystemTests
{
    [Fact]
    public void Spawn_Respects_Maximum_Active_Count_Per_Target()
    {
        FloatingCombatTextSystem system = new();

        for (int i = 0; i < 6; i++)
        {
            system.Spawn(7, new Vector3(1f, 2f, 3f), 100 + i, FloatingCombatTextKind.Regular);
        }

        List<FloatingCombatTextRenderState> states = new();
        system.CollectRenderStates(states);

        Assert.Equal(FloatingCombatTextSystem.MaxActivePerTarget, states.Count);
    }

    [Fact]
    public void Update_Expires_Entries_After_Lifetime()
    {
        FloatingCombatTextSystem system = new();
        system.Spawn(1, Vector3.Zero, 123, FloatingCombatTextKind.Regular);

        system.Update(FloatingCombatTextMotion.Lifetime + 0.01f);

        List<FloatingCombatTextRenderState> states = new();
        system.CollectRenderStates(states);

        Assert.Empty(states);
    }
}
