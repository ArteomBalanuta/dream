using System.IO;
using System.Reflection;
using Xunit;
using Dream.Rendering.Actors;

namespace Dream.Tests.Rendering.Actors;


public class EnemyRendererSignatureTests
{
    [Fact]
    public void Draw_Accepts_Debug_Collision_Flag()
    {
        MethodInfo method = typeof(EnemyRenderer).GetMethod("Draw")!;

        ParameterInfo[] parameters = method.GetParameters();

        Assert.Equal(4, parameters.Length);
        Assert.Equal(typeof(bool), parameters[3].ParameterType);
    }

    // The gait's playback rate has to reach the instance, and the only place it can is the per-frame
    // advance. A rate computed and dropped looks exactly like one applied, so this pins the signature
    // and keeps its trailing default: every pre-gait caller still means "as authored".
    [Fact]
    public void AdvanceInstance_TakesThePlaybackRate_WithAnAsAuthoredDefault()
    {
        MethodInfo method = typeof(EnemyRenderer).GetMethod("AdvanceInstance")!;

        ParameterInfo[] parameters = method.GetParameters();

        Assert.Equal(4, parameters.Length);
        Assert.Equal(typeof(float), parameters[3].ParameterType);
        Assert.True(parameters[3].HasDefaultValue);
        Assert.Equal(1f, (float)parameters[3].DefaultValue!);
    }

    // GL cannot open in a test, so the wiring is pinned as source: the renderer must measure the
    // enemy's motion from the pose it is about to draw and hand the rate to the instance. Measuring
    // and then not using it would leave every enemy walking, which is what this wave fixed.
    [Fact]
    public void TheRenderer_MeasuresTheDrawnPose_AndPutsTheRateOnTheInstance()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "dream", "Rendering", "Actors", "EnemyRenderer.cs")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        string source = File.ReadAllText(Path.Combine(root!, "dream", "Rendering", "Actors", "EnemyRenderer.cs"));

        int observe = source.IndexOf("ObserveMotion(pose.Position, deltaTime)", StringComparison.Ordinal);
        int resolve = source.IndexOf("ResolveGaitSpeed(", StringComparison.Ordinal);
        Assert.True(observe > 0, "The renderer no longer measures the enemy's drawn motion.");
        Assert.True(resolve > observe, "The gait must be resolved from the speed measured this frame.");
        Assert.Contains("instance.AnimationSpeed = playbackSpeed;", source, StringComparison.Ordinal);
        Assert.Contains("renderState.PlaybackSpeed", source, StringComparison.Ordinal);
    }
}
