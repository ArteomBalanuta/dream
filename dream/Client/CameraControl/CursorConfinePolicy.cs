namespace Dream.Client.CameraControl;

// Whether the OS cursor should be clipped to the window right now. The pause menu and settings always release it.
public static class CursorConfinePolicy
{
    public static bool ShouldConfine(
        CameraConfineMode mode,
        bool focused,
        bool minimised,
        bool fullscreenLike,
        bool pauseOrSettingsOpen,
        bool loading,
        bool inPlay)
    {
        if (mode == CameraConfineMode.Off || !focused || minimised || !inPlay || pauseOrSettingsOpen || loading)
        {
            return false;
        }

        return mode switch
        {
            CameraConfineMode.FullscreenOnly => fullscreenLike,
            CameraConfineMode.Always => true,
            _ => false
        };
    }
}
