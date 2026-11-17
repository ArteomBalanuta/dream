using Dream.Client.Menu;
using Xunit;

namespace Dream.Tests.Client.Menu;

public class LoadingProgressTests
{
    [Fact]
    public void ForLauncher_RisesAlongThePhasesAndNeverPassesTheSceneStart()
    {
        float[] values =
        [
            LoadingProgress.ForLauncher(SinglePlayerPhase.Waking),
            LoadingProgress.ForLauncher(SinglePlayerPhase.Starting),
            LoadingProgress.ForLauncher(SinglePlayerPhase.Connecting),
            LoadingProgress.ForLauncher(SinglePlayerPhase.Connected)
        ];

        for (int i = 1; i < values.Length; i++)
        {
            Assert.True(values[i] > values[i - 1]);
        }

        Assert.True(values[0] > 0f);
        Assert.True(values[^1] <= LoadingProgress.SceneStart);
        Assert.Equal(0f, LoadingProgress.ForLauncher(SinglePlayerPhase.Idle));
        Assert.Equal(0f, LoadingProgress.ForLauncher(SinglePlayerPhase.Failed));
    }

    [Fact]
    public void ForScene_MapsOntoTheRestOfTheBar()
    {
        Assert.Equal(LoadingProgress.SceneStart, LoadingProgress.ForScene(0f), 4);
        Assert.Equal(1f, LoadingProgress.ForScene(1f), 4);
        Assert.Equal(LoadingProgress.SceneStart, LoadingProgress.ForScene(float.NaN), 4);
        Assert.Equal(1f, LoadingProgress.ForScene(5f), 4);
        Assert.Equal(LoadingProgress.SceneStart, LoadingProgress.ForScene(-3f), 4);
    }
}
