Purpose: record the client-local third-person camera/view-mode implementation
Status: implemented
Scope: settings/config persistence, camera presentation, local player world model rendering, tests, and docs

## Summary

The desktop client now supports switching between first-person and third-person presentation.

Implemented behavior:

- added `CameraViewMode` to `GameSettings`
- added `camera_view=first_person|third_person` support to `DesktopClientTextConfig`
- added a settings-menu `Third Person View` checkbox
- added `C` as a gameplay key toggle for first-person / third-person view
- added `ThirdPersonCameraRig` to derive a render-only follow camera from the unchanged local player camera and body position
- added `LocalPlayerRenderer` and `LocalPlayerPresentationState` so the local blocky player model renders in world space when third-person view is active
- skipped the first-person viewmodel pass while third-person view is active
- kept movement, combat, collision, server commands, and replicated facing tied to the original local player camera/facing state

## Verification

Focused verification added coverage for:

- `GameSettings` default and toggle behavior
- desktop-client text config load/seed behavior for camera view mode
- third-person follow-camera math
- local player render-state creation
- HUD request propagation for the nullable camera-view mode setting

