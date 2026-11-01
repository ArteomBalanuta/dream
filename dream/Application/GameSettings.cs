using OpenTK.Mathematics;
using Dream.Configuration;
using Dream.Rendering.Hud;
using Dream.Rendering.ViewModels;
using Dream.Simulation.World;

namespace Dream.Application;


public class GameSettings
{
    private readonly ViewModelRigSettings _defaultViewModelRig;

    public const int MinFloorSize = 5;
    public const int MaxFloorSize = 100;
    public const float MinNoclipSpeed = 6f;
    public const float MaxNoclipSpeed = 300f;

    public float FieldOfViewDegrees { get; set; } = 70f;

    public float MouseSensitivity { get; set; } = 2.5f;

    public float EyeHeightOffset { get; set; } = 0.35f;

    public float MoveSpeed { get; set; } = 6f;

    public float NoclipSpeed { get; set; } = 18f;

    public CameraViewMode CameraViewMode { get; private set; } = CameraViewMode.FirstPerson;

    public float JumpVelocity { get; set; } = 5f;

    public FloatingCombatTextTuning FloatingCombatText { get; private set; } = FloatingCombatTextTuning.Default;

    public ViewModelRigSettings ViewModelRig { get; private set; }

    public int FloorSize { get; private set; } = WorldDefaults.StartupFloorSize;

    public int PendingFloorSize { get; private set; } = WorldDefaults.StartupFloorSize;

    public bool ShowCollisionGeometry { get; private set; }

    public bool ShowPerformanceCounters { get; private set; }

    public bool SettingsVisible { get; private set; }

    public bool InventoryVisible { get; private set; }

    // True when F3 and the developer controls in Settings are available (admin_hud=1 or DREAM_ADMIN_HUD=1).
    // Display only: nothing about it is sent to the server, and the env override is never saved.
    public bool AdminHudAvailable { get; private set; }

    // DREAM_HUD=imgui: the ImGui surface for the whole run.
    public bool ForceImGuiHud { get; private set; }

    // The configured HUD scale; 0 is auto. See ResolvedUiScale for the scale in use.
    public float UiScaleSetting { get; private set; }

    public RuntimeState CurrentRuntimeState { get; private set; } = new(RuntimeMode.Gameplay);

    public int WindowWidth { get; private set; }

    public int WindowHeight { get; private set; }

    public event Action<CollisionDebugVisibilityChangedEvent>? CollisionDebugVisibilityChanged;
    public event Action<ResolutionAppliedEvent>? ResolutionApplied;
    public event Action<FloorSizeAppliedEvent>? FloorSizeApplied;
    public event Action<RuntimeStateChangedEvent>? RuntimeStateChanged;

    public GameSettings(Vector2i initialSize, ViewModelRigSettings? viewModelRig = null, DesktopClientConfig? clientConfig = null, HudOptions? hudOptions = null)
    {
        _defaultViewModelRig = (viewModelRig ?? ViewModelRigSettings.CreateDefault()).Clone();
        ViewModelRig = _defaultViewModelRig.Clone();
        if (clientConfig != null)
        {
            clientConfig.Sanitize();
            FieldOfViewDegrees = clientConfig.FieldOfViewDegrees;
            MouseSensitivity = clientConfig.MouseSensitivity;
            NoclipSpeed = clientConfig.NoclipSpeed;
            CameraViewMode = clientConfig.CameraViewMode;
            FloatingCombatText = clientConfig.FloatingCombatText;
        }

        HudOptions hud = hudOptions ?? HudOptions.ResolveFromEnvironment(clientConfig);
        AdminHudAvailable = hud.AdminHudAvailable;
        ForceImGuiHud = hud.ForceImGuiSurface;
        UiScaleSetting = hud.UiScale;

        ApplyResolution(initialSize);
    }

    // The HUD scale in use for the current window size.
    public float ResolvedUiScale => Dream.Rendering.Ui.UiScale.Resolve(WindowWidth, WindowHeight, UiScaleSetting);

    public void SetUiScale(float configured)
    {
        UiScaleSetting = HudOptions.SanitizeUiScale(configured);
    }

    public void ApplyResolution(Vector2i size)
    {
        WindowWidth = size.X;
        WindowHeight = size.Y;
        ResolutionApplied?.Invoke(new ResolutionAppliedEvent(size));
    }

    public void ToggleSettings()
    {
        SettingsVisible = !SettingsVisible;
        if (SettingsVisible)
        {
            InventoryVisible = false;
        }

        ApplyRuntimeState();
    }

    public void ToggleInventory()
    {
        if (SettingsVisible)
        {
            return;
        }

        InventoryVisible = !InventoryVisible;
        ApplyRuntimeState();
    }

    public void StageFloorSize(int floorSize)
    {
        PendingFloorSize = Math.Clamp(floorSize, MinFloorSize, MaxFloorSize);
    }

    public void ApplyPendingFloorSize()
    {
        FloorSize = PendingFloorSize;
        FloorSizeApplied?.Invoke(new FloorSizeAppliedEvent(FloorSize));
    }

    public void SetShowCollisionGeometry(bool showCollisionGeometry)
    {
        ShowCollisionGeometry = showCollisionGeometry;
        CollisionDebugVisibilityChanged?.Invoke(new CollisionDebugVisibilityChangedEvent(ShowCollisionGeometry));
    }

    public void SetShowPerformanceCounters(bool showPerformanceCounters)
    {
        ShowPerformanceCounters = showPerformanceCounters;
    }

    public void SetCameraViewMode(CameraViewMode cameraViewMode)
    {
        CameraViewMode = cameraViewMode;
    }

    public void ToggleCameraViewMode()
    {
        CameraViewMode = CameraViewMode == CameraViewMode.FirstPerson
            ? CameraViewMode.ThirdPerson
            : CameraViewMode.FirstPerson;
    }

    public void ApplyFloatingCombatTextTuning(FloatingCombatTextTuning tuning)
    {
        FloatingCombatText = tuning.Clamp();
    }

    public void ResetViewModelRig()
    {
        ViewModelRig = _defaultViewModelRig.Clone();
    }

    private void ApplyRuntimeState()
    {
        CurrentRuntimeState = new RuntimeState(
            SettingsVisible
                ? RuntimeMode.SettingsMenu
                : InventoryVisible
                    ? RuntimeMode.InventoryOverlay
                    : RuntimeMode.Gameplay);
        RuntimeStateChanged?.Invoke(new RuntimeStateChangedEvent(CurrentRuntimeState));
    }
}
