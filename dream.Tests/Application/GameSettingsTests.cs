using OpenTK.Mathematics;
using Xunit;
using Dream.Application;
using Dream.Configuration;
using Dream.Rendering.Hud;
using Dream.Rendering.ViewModels;
using Dream.Simulation.Players;

namespace Dream.Tests.Application;


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
        Assert.Equal(18f, settings.NoclipSpeed);
        Assert.Equal(CameraViewMode.FirstPerson, settings.CameraViewMode);
        Assert.Equal(FloatingCombatTextTuning.Default, settings.FloatingCombatText);
        Assert.Equal(56, settings.FloorSize);
        Assert.Equal(56, settings.PendingFloorSize);
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

        Assert.Equal(56, settings.FloorSize);
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
    public void Constructor_Defaults_Performance_Counters_To_Hidden()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        Assert.False(settings.ShowPerformanceCounters);
    }

    [Fact]
    public void SetShowCollisionGeometry_Enables_Debug_Rendering()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        settings.SetShowCollisionGeometry(true);

        Assert.True(settings.ShowCollisionGeometry);
    }

    [Fact]
    public void SetShowPerformanceCounters_Enables_Performance_Hud()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        settings.SetShowPerformanceCounters(true);

        Assert.True(settings.ShowPerformanceCounters);
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
    public void ToggleInventory_Uses_Inventory_Runtime_Mode_And_Blocks_Gameplay_Input()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        settings.ToggleInventory();

        Assert.True(settings.InventoryVisible);
        Assert.Equal(RuntimeMode.InventoryOverlay, settings.CurrentRuntimeState.Mode);
        Assert.False(settings.CurrentRuntimeState.AllowGameplayInput);
    }

    [Fact]
    public void ToggleSettings_Closes_Inventory_Overlay()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));
        settings.ToggleInventory();

        settings.ToggleSettings();

        Assert.True(settings.SettingsVisible);
        Assert.False(settings.InventoryVisible);
        Assert.Equal(RuntimeMode.SettingsMenu, settings.CurrentRuntimeState.Mode);
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
    public void ApplyFloatingCombatTextTuning_Clamps_Runtime_Values()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        settings.ApplyFloatingCombatTextTuning(new FloatingCombatTextTuning(
            SizeMultiplier: 99f,
            MotionSpeedMultiplier: -10f,
            CriticalDepthMultiplier: 99f));

        Assert.Equal(FloatingCombatTextTuning.MaxSizeMultiplier, settings.FloatingCombatText.SizeMultiplier);
        Assert.Equal(FloatingCombatTextTuning.MinMotionSpeedMultiplier, settings.FloatingCombatText.MotionSpeedMultiplier);
        Assert.Equal(FloatingCombatTextTuning.MaxCriticalDepthMultiplier, settings.FloatingCombatText.CriticalDepthMultiplier);
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
    public void Constructor_Uses_Injected_Client_Defaults()
    {
        DesktopClientConfig config = new()
        {
            FieldOfViewDegrees = 84f,
            MouseSensitivity = 3.5f,
            NoclipSpeed = 28f,
            FloatingCombatText = new FloatingCombatTextTuning(2.3f, 0.7f, 4.5f)
        };

        GameSettings settings = new(new Vector2i(1440, 1024), ViewModelRigSettings.CreateDefault(), config);

        Assert.Equal(84f, settings.FieldOfViewDegrees);
        Assert.Equal(3.5f, settings.MouseSensitivity);
        Assert.Equal(28f, settings.NoclipSpeed);
        Assert.Equal(new FloatingCombatTextTuning(2.3f, 0.7f, 4.5f), settings.FloatingCombatText);
    }

    [Fact]
    public void ToggleCameraViewMode_Switches_Between_First_And_Third_Person()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        settings.ToggleCameraViewMode();

        Assert.Equal(CameraViewMode.ThirdPerson, settings.CameraViewMode);

        settings.ToggleCameraViewMode();

        Assert.Equal(CameraViewMode.FirstPerson, settings.CameraViewMode);
    }

    [Fact]
    public void Constructor_Uses_Injected_Camera_View_Mode()
    {
        DesktopClientConfig config = new()
        {
            CameraViewMode = CameraViewMode.ThirdPerson
        };

        GameSettings settings = new(new Vector2i(1440, 1024), ViewModelRigSettings.CreateDefault(), config);

        Assert.Equal(CameraViewMode.ThirdPerson, settings.CameraViewMode);
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

    [Fact]
    public void ToPlayerSimulationTuning_Noclip_Is_Faster_Than_Standard_Move_Speed()
    {
        GameSettings settings = new(new Vector2i(1440, 1024))
        {
            MoveSpeed = 4f,
            NoclipSpeed = 18f
        };

        PlayerSimulationTuning tuning = settings.ToPlayerSimulationTuning();

        Assert.True(tuning.NoclipSpeed > tuning.MoveSpeed);
    }

    [Fact]
    public void ToPlayerSimulationTuning_Copies_Every_Movement_Value_From_Settings()
    {
        GameSettings settings = new(new Vector2i(1440, 1024))
        {
            EyeHeightOffset = 0.5f,
            MouseSensitivity = 1.25f,
            MoveSpeed = 7.5f,
            NoclipSpeed = 33f,
            JumpVelocity = 9f
        };

        PlayerSimulationTuning tuning = settings.ToPlayerSimulationTuning();

        Assert.Equal(new PlayerSimulationTuning(0.5f, 1.25f, 7.5f, 33f, 9f), tuning);
    }

    [Fact]
    public void ToPlayerSimulationTuning_Throws_For_Null_Settings()
    {
        Assert.Throws<ArgumentNullException>(() => GameSettingsSimulationExtensions.ToPlayerSimulationTuning(null!));
    }
}
