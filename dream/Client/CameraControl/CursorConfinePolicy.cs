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
        bool inPlay) =>
        throw new NotImplementedException("tactical-camera stub");
}
