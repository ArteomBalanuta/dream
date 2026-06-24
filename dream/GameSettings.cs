using OpenTK.Mathematics;

public class GameSettings
{
    public const int MinFloorSize = 5;
    public const int MaxFloorSize = 100;

    public float FieldOfViewDegrees { get; set; } = 70f;

    public float MouseSensitivity { get; set; } = 2.5f;

    public float EyeHeightOffset { get; set; } = 0.35f;

    public float MoveSpeed { get; set; } = 6f;

    public float JumpVelocity { get; set; } = 5f;

    public int FloorSize { get; private set; } = 25;

    public int PendingFloorSize { get; private set; } = 25;

    public bool SettingsVisible { get; private set; }

    public int WindowWidth { get; private set; }

    public int WindowHeight { get; private set; }

    public GameSettings(Vector2i initialSize)
    {
        ApplyResolution(initialSize);
    }

    public void ApplyResolution(Vector2i size)
    {
        WindowWidth = size.X;
        WindowHeight = size.Y;
    }

    public void ToggleSettings()
    {
        SettingsVisible = !SettingsVisible;
    }

    public void StageFloorSize(int floorSize)
    {
        PendingFloorSize = Math.Clamp(floorSize, MinFloorSize, MaxFloorSize);
    }

    public void ApplyPendingFloorSize()
    {
        FloorSize = PendingFloorSize;
    }
}
