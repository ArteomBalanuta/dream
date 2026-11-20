using Dream.Client.Presentation;

namespace Dream.Client.CameraControl;

// Tuning for the tactical play camera. Defaults reproduce the fixed isometric view when no input arrives.
public sealed record TacticalCameraSettings
{
    public static TacticalCameraSettings Default { get; } = new();

    public static IReadOnlyList<int> AllowedSnapSteps { get; } = [15, 30, 45, 90];

    public bool EdgePanEnabled { get; init; } = true;
    public float EdgeMarginPixels { get; init; } = 10f;          // times the UI layout scale
    public float PanSpeed { get; init; } = 14f;                  // m/s at DefaultDistance; scales with distance
    public float PanAccelSeconds { get; init; } = 0.08f;
    public float PanDecelSeconds { get; init; } = 0.14f;
    public float EdgeDwellSeconds { get; init; } = 0.12f;
    public float EdgeFocusGraceSeconds { get; init; } = 0.25f;
    public CameraConfineMode ConfineMode { get; init; } = CameraConfineMode.FullscreenOnly;
    public CameraAnchorMode AnchorMode { get; init; } = CameraAnchorMode.Follow;
    public float ReturnToPlayerSeconds { get; init; }            // 0 = off
    public float RotateDegreesPerPixel { get; init; } = 0.3f;
    public float RotateSnapStepDegrees { get; init; } = 90f;
    public bool SnapOnDragRelease { get; init; }
    public float RotateSmoothingSeconds { get; init; } = 0.12f;
    public float ZoomStepRatio { get; init; } = 1.15f;
    public float ZoomSmoothingSeconds { get; init; } = 0.10f;
    public bool InvertZoom { get; init; }
    public float MinDistance { get; init; } = 10f;
    // The farthest zoom whose view still fits the sun shadow volume (derived, not restated).
    public float MaxDistance { get; init; } = TacticalCameraLimits.MaxDistance;
    public float LeashMetres { get; init; } = TacticalCameraLimits.LeashMetres;

    public float DefaultYawDegrees => IsometricView.YawDegrees;
    public float PitchDegrees => IsometricView.PitchDegrees;
    public float DefaultDistance => IsometricView.Distance;

    // Out-of-range or non-finite values are pulled back into range, never rejected.
    public TacticalCameraSettings Sanitized()
    {
        return this with
        {
            EdgeMarginPixels = Clamp(EdgeMarginPixels, 2f, 48f, Default.EdgeMarginPixels),
            PanSpeed = Clamp(PanSpeed, 4f, 40f, Default.PanSpeed),
            PanAccelSeconds = Clamp(PanAccelSeconds, 0f, 0.5f, Default.PanAccelSeconds),
            PanDecelSeconds = Clamp(PanDecelSeconds, 0f, 0.5f, Default.PanDecelSeconds),
            EdgeDwellSeconds = Clamp(EdgeDwellSeconds, 0f, 1f, Default.EdgeDwellSeconds),
            EdgeFocusGraceSeconds = Clamp(EdgeFocusGraceSeconds, 0f, 1f, Default.EdgeFocusGraceSeconds),
            ConfineMode = Enum.IsDefined(ConfineMode) ? ConfineMode : Default.ConfineMode,
            AnchorMode = Enum.IsDefined(AnchorMode) ? AnchorMode : Default.AnchorMode,
            ReturnToPlayerSeconds = Clamp(ReturnToPlayerSeconds, 0f, 10f, Default.ReturnToPlayerSeconds),
            RotateDegreesPerPixel = Clamp(RotateDegreesPerPixel, 0.05f, 1.5f, Default.RotateDegreesPerPixel),
            RotateSnapStepDegrees = NearestSnapStep(RotateSnapStepDegrees),
            RotateSmoothingSeconds = Clamp(RotateSmoothingSeconds, 0f, 0.5f, Default.RotateSmoothingSeconds),
            ZoomStepRatio = Clamp(ZoomStepRatio, 1.02f, 1.5f, Default.ZoomStepRatio),
            ZoomSmoothingSeconds = Clamp(ZoomSmoothingSeconds, 0f, 0.5f, Default.ZoomSmoothingSeconds),
            MinDistance = Clamp(MinDistance, 4f, IsometricView.Distance, Default.MinDistance),
            MaxDistance = Clamp(MaxDistance, IsometricView.Distance, TacticalCameraLimits.MaxDistance, TacticalCameraLimits.MaxDistance),
            LeashMetres = Clamp(LeashMetres, 0f, TacticalCameraLimits.LeashMetres, Default.LeashMetres),
        };
    }

    private static float Clamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static float NearestSnapStep(float value)
    {
        if (!float.IsFinite(value))
        {
            return Default.RotateSnapStepDegrees;
        }

        int best = AllowedSnapSteps[0];
        foreach (int step in AllowedSnapSteps)
        {
            if (MathF.Abs(step - value) < MathF.Abs(best - value))
            {
                best = step;
            }
        }

        return best;
    }
}
