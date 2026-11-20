namespace Dream.Client.CameraControl;

// What the scene allows the camera to read this Update.
// Open: mouse camera input allowed (focused, in play, not loading, no pause menu or settings window).
// KeysOpen: Open and no text field has the keyboard (chat typing).
// Confined: the cursor is clipped to the window, so it cannot leave (no edge dwell needed).
// HudDragActive: an inventory or ImGui drag is in flight (no edge pan).
// ScriptedPointer: a capture script drives the pointer; the camera then reads no mouse input at all.
public readonly record struct CameraGate(bool Open, bool KeysOpen, bool Confined, bool HudDragActive, bool ScriptedPointer)
{
    public static CameraGate Closed { get; } = new(false, false, false, false, false);
}
