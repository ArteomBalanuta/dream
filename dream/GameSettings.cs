using OpenTK.Mathematics;

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

    public float NoclipSpeed { get; set; } = 150f;

    public float JumpVelocity { get; set; } = 5f;

    public FloatingCombatTextTuning FloatingCombatText { get; private set; } = FloatingCombatTextTuning.Default;

    public ViewModelRigSettings ViewModelRig { get; private set; }

    public int FloorSize { get; private set; } = 25;

    public int PendingFloorSize { get; private set; } = 25;

    public bool ShowCollisionGeometry { get; private set; }

    public bool SettingsVisible { get; private set; }

    public RuntimeState CurrentRuntimeState { get; private set; } = new(RuntimeMode.Gameplay);

    public int WindowWidth { get; private set; }

    public int WindowHeight { get; private set; }

    public event Action<CollisionDebugVisibilityChangedEvent>? CollisionDebugVisibilityChanged;
    public event Action<ResolutionAppliedEvent>? ResolutionApplied;
    public event Action<FloorSizeAppliedEvent>? FloorSizeApplied;
    public event Action<RuntimeStateChangedEvent>? RuntimeStateChanged;

    public GameSettings(Vector2i initialSize, ViewModelRigSettings? viewModelRig = null)
    {
        _defaultViewModelRig = (viewModelRig ?? ViewModelRigSettings.CreateDefault()).Clone();
        ViewModelRig = _defaultViewModelRig.Clone();
        ApplyResolution(initialSize);
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
        CurrentRuntimeState = new RuntimeState(SettingsVisible ? RuntimeMode.SettingsMenu : RuntimeMode.Gameplay);
        RuntimeStateChanged?.Invoke(new RuntimeStateChangedEvent(CurrentRuntimeState));
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

    public void ApplyFloatingCombatTextTuning(FloatingCombatTextTuning tuning)
    {
        FloatingCombatText = tuning.Clamp();
    }

    public void ResetViewModelRig()
    {
        ViewModelRig = _defaultViewModelRig.Clone();
    }
}
