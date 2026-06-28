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
        Assert.Equal(60f, settings.ViewModelRig.FieldOfViewDegrees);
        Assert.Equal(Vector3.Zero, settings.ViewModelRig.PositionOffset);
        Assert.Equal(Vector3.Zero, settings.ViewModelRig.RotationOffsetDegrees);
        Assert.Equal(Vector3.Zero, settings.ViewModelRig.Hands.PositionOffset);
        Assert.Equal(new Vector3(-0.046f, 0.204f, -0.156f), settings.ViewModelRig.HeldItem.PositionOffset);
        Assert.Equal(new Vector3(0.938f, 1.406f, -1.406f), settings.ViewModelRig.HeldItem.RotationOffsetDegrees);
        Assert.Equal(0.00f, settings.ViewModelRig.BodyAnchor.HorizontalSpread);
        Assert.Equal(0.306f, settings.ViewModelRig.BodyAnchor.VerticalDrop);
        Assert.Equal(0.18f, settings.ViewModelRig.BodyAnchor.ForwardPull);
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

    [Fact]
    public void ResetViewModelRig_Restores_Defaults()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));
        settings.ViewModelRig.FieldOfViewDegrees = 78f;
        settings.ViewModelRig.PositionOffset = new Vector3(0.2f, -0.3f, 0.1f);
        settings.ViewModelRig.Hands.RotationOffsetDegrees = new Vector3(2f, 14f, -9f);
        settings.ViewModelRig.BodyAnchor.HorizontalSpread = 0.7f;

        settings.ResetViewModelRig();

        Assert.Equal(60f, settings.ViewModelRig.FieldOfViewDegrees);
        Assert.Equal(Vector3.Zero, settings.ViewModelRig.PositionOffset);
        Assert.Equal(Vector3.Zero, settings.ViewModelRig.RotationOffsetDegrees);
        Assert.Equal(Vector3.Zero, settings.ViewModelRig.Hands.RotationOffsetDegrees);
        Assert.Equal(0.00f, settings.ViewModelRig.BodyAnchor.HorizontalSpread);
        Assert.Equal(new Vector3(-0.046f, 0.204f, -0.156f), settings.ViewModelRig.HeldItem.PositionOffset);
        Assert.Equal(new Vector3(0.938f, 1.406f, -1.406f), settings.ViewModelRig.HeldItem.RotationOffsetDegrees);
    }

    [Fact]
    public void ViewModelRig_Allows_Live_Shared_Adjustments()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        settings.ViewModelRig.FieldOfViewDegrees = 72f;
        settings.ViewModelRig.PositionOffset = new Vector3(0.05f, -0.10f, 0.02f);

        Assert.Equal(72f, settings.ViewModelRig.FieldOfViewDegrees);
        Assert.Equal(new Vector3(0.05f, -0.10f, 0.02f), settings.ViewModelRig.PositionOffset);
    }

    [Fact]
    public void Constructor_Uses_Injected_ViewModelRig()
    {
        ViewModelRigSettings rig = ViewModelRigSettings.CreateDefault();
        rig.BodyAnchor.VerticalDrop = 0.500f;

        GameSettings settings = new(new Vector2i(1440, 1024), rig);

        Assert.Equal(0.500f, settings.ViewModelRig.BodyAnchor.VerticalDrop);
    }

    [Fact]
    public void ResetViewModelRig_Restores_Injected_Baseline()
    {
        ViewModelRigSettings rig = ViewModelRigSettings.CreateDefault();
        rig.BodyAnchor.VerticalDrop = 0.500f;

        GameSettings settings = new(new Vector2i(1440, 1024), rig);
        settings.ViewModelRig.BodyAnchor.VerticalDrop = 0.100f;

        settings.ResetViewModelRig();

        Assert.Equal(0.500f, settings.ViewModelRig.BodyAnchor.VerticalDrop);
    }
}
