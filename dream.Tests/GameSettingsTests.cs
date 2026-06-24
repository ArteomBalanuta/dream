using OpenTK.Mathematics;
using Xunit;

public class GameSettingsTests
{
    [Fact]
    public void Constructor_Seeds_Window_Size_And_Defaults()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        Assert.Equal(1440, settings.WindowWidth);
        Assert.Equal(1024, settings.WindowHeight);
        Assert.False(settings.SettingsVisible);
        Assert.Equal(70f, settings.FieldOfViewDegrees);
        Assert.Equal(25, settings.FloorSize);
        Assert.Equal(25, settings.PendingFloorSize);
    }

    [Fact]
    public void ApplyResolution_Updates_Window_Size()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        settings.ApplyResolution(new Vector2i(1280, 720));

        Assert.Equal(1280, settings.WindowWidth);
        Assert.Equal(720, settings.WindowHeight);
    }

    [Fact]
    public void StageFloorSize_Updates_Pending_Without_Changing_Current()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        settings.StageFloorSize(10);

        Assert.Equal(25, settings.FloorSize);
        Assert.Equal(10, settings.PendingFloorSize);
    }

    [Fact]
    public void ApplyPendingFloorSize_Promotes_Staged_Value()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));
        settings.StageFloorSize(10);

        settings.ApplyPendingFloorSize();

        Assert.Equal(10, settings.FloorSize);
        Assert.Equal(10, settings.PendingFloorSize);
    }

    [Fact]
    public void StageFloorSize_Clamps_To_Minimum()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        settings.StageFloorSize(1);

        Assert.Equal(5, settings.PendingFloorSize);
    }

    [Fact]
    public void StageFloorSize_Clamps_To_Maximum()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        settings.StageFloorSize(500);

        Assert.Equal(100, settings.PendingFloorSize);
    }

    [Fact]
    public void Constructor_Defaults_Collision_Debug_Geometry_To_Hidden()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        Assert.False(settings.ShowCollisionGeometry);
    }

    [Fact]
    public void SetShowCollisionGeometry_Enables_Debug_Rendering()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        settings.SetShowCollisionGeometry(true);

        Assert.True(settings.ShowCollisionGeometry);
    }

    [Fact]
    public void CurrentRuntimeState_Reflects_Settings_Menu_Mode()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        settings.ToggleSettings();

        Assert.Equal(RuntimeMode.SettingsMenu, settings.CurrentRuntimeState.Mode);
        Assert.False(settings.CurrentRuntimeState.AllowGameplayInput);
    }

    [Fact]
    public void SetShowCollisionGeometry_Raises_Event()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));
        CollisionDebugVisibilityChangedEvent? observedEvent = null;
        settings.CollisionDebugVisibilityChanged += e => observedEvent = e;

        settings.SetShowCollisionGeometry(true);

        Assert.NotNull(observedEvent);
        Assert.True(observedEvent.Value.IsVisible);
    }
}
