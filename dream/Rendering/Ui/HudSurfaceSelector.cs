namespace Dream.Rendering.Ui;

public enum HudSurface : byte
{
    Classic,
    ImGui
}

public enum ClassicHudBackendState : byte
{
    Loading,
    Ready,
    Failed
}

// Decides which HUD surface draws. Pure: no GL, no clock. Classic is the default; ImGui shows when
// the run forces it (DREAM_HUD=imgui), when the admin key (F3) has toggled it on, or for good once
// the classic surface has failed. A failure latches regardless of the admin setting, and F3 is
// then inert. Client-only; nothing here is sent to the server.
public sealed class HudSurfaceSelector
{
    public const float LoadingTimeoutSeconds = 5f;

    private float _loadingSeconds;

    public HudSurfaceSelector(bool adminAvailable, bool forceImGui)
    {
        AdminAvailable = adminAvailable;
        ForcedImGui = forceImGui;
    }

    public bool AdminAvailable { get; }

    public bool ForcedImGui { get; }

    // F3 has switched to the ImGui surface for this session. Never persisted.
    public bool AdminImGui { get; private set; }

    public bool Latched { get; private set; }

    public string? LatchReason { get; private set; }

    public HudSurface Current => Latched || ForcedImGui || AdminImGui ? HudSurface.ImGui : HudSurface.Classic;

    // F3. A no-op (false) unless admin is available, and also after a latched failure or when the
    // run forces ImGui. Returns true when the surface changed.
    public bool ToggleAdmin()
    {
        if (!AdminAvailable || Latched || ForcedImGui)
        {
            return false;
        }

        AdminImGui = !AdminImGui;
        return true;
    }

    // Latches the ImGui surface. Returns true only the first time, so the caller logs one line.
    public bool Latch(string reason)
    {
        if (Latched)
        {
            return false;
        }

        Latched = true;
        LatchReason = string.IsNullOrWhiteSpace(reason) ? "classic HUD failed" : reason;
        return true;
    }

    // Called once per frame with the backend's state. Failed latches at once; Loading latches after
    // LoadingTimeoutSeconds; Ready resets the wait. Returns true when this call latched.
    public bool Observe(ClassicHudBackendState state, string? failureReason, float deltaSeconds)
    {
        if (Latched)
        {
            return false;
        }

        switch (state)
        {
            case ClassicHudBackendState.Failed:
                return Latch(failureReason ?? "the classic HUD atlas failed to load");
            case ClassicHudBackendState.Loading:
                if (float.IsFinite(deltaSeconds) && deltaSeconds > 0f)
                {
                    _loadingSeconds += deltaSeconds;
                }

                return _loadingSeconds > LoadingTimeoutSeconds
                    && Latch($"the classic HUD atlas was still loading after {LoadingTimeoutSeconds:0} s");
            default:
                _loadingSeconds = 0f;
                return false;
        }
    }
}
