using Xunit;
using Dream.Rendering.Ui;

namespace Dream.Tests.Rendering.Ui;

public class HudSurfaceSelectorTests
{
    [Fact]
    public void Default_IsClassic()
    {
        HudSurfaceSelector selector = new(adminAvailable: false, forceImGui: false);

        Assert.Equal(HudSurface.Classic, selector.Current);
        Assert.False(selector.Latched);
    }

    [Fact]
    public void ToggleAdmin_IsANoOpWhenAdminIsNotAvailable()
    {
        HudSurfaceSelector selector = new(adminAvailable: false, forceImGui: false);

        Assert.False(selector.ToggleAdmin());
        Assert.Equal(HudSurface.Classic, selector.Current);
    }

    [Fact]
    public void ToggleAdmin_FlipsBetweenSurfacesWhenAvailable()
    {
        HudSurfaceSelector selector = new(adminAvailable: true, forceImGui: false);

        Assert.True(selector.ToggleAdmin());
        Assert.Equal(HudSurface.ImGui, selector.Current);
        Assert.True(selector.ToggleAdmin());
        Assert.Equal(HudSurface.Classic, selector.Current);
    }

    [Fact]
    public void ForcedImGui_StaysImGuiAndF3IsInert()
    {
        HudSurfaceSelector selector = new(adminAvailable: true, forceImGui: true);

        Assert.Equal(HudSurface.ImGui, selector.Current);
        Assert.False(selector.ToggleAdmin());
        Assert.Equal(HudSurface.ImGui, selector.Current);
    }

    [Fact]
    public void Latch_SwitchesToImGuiOnceRegardlessOfAdminAndKeepsF3Inert()
    {
        HudSurfaceSelector selector = new(adminAvailable: false, forceImGui: false);

        Assert.True(selector.Latch("atlas is corrupt"));
        Assert.False(selector.Latch("a second reason"));

        Assert.Equal(HudSurface.ImGui, selector.Current);
        Assert.Equal("atlas is corrupt", selector.LatchReason);
    }

    [Fact]
    public void Latch_LeavesF3InertEvenWhenAdminIsAvailable()
    {
        HudSurfaceSelector selector = new(adminAvailable: true, forceImGui: false);
        selector.Latch("x");

        Assert.False(selector.ToggleAdmin());
        Assert.Equal(HudSurface.ImGui, selector.Current);
    }

    [Fact]
    public void Observe_FailedLatchesOnceWithTheBackendReason()
    {
        HudSurfaceSelector selector = new(false, false);

        Assert.True(selector.Observe(ClassicHudBackendState.Failed, "hud.png is missing", 0.016f));
        Assert.False(selector.Observe(ClassicHudBackendState.Failed, "hud.png is missing", 0.016f));
        Assert.Equal("hud.png is missing", selector.LatchReason);
        Assert.Equal(HudSurface.ImGui, selector.Current);
    }

    [Fact]
    public void Observe_LoadingLatchesOnlyPastTheTimeout()
    {
        HudSurfaceSelector selector = new(false, false);

        for (int i = 0; i < 50; i++)
        {
            Assert.False(selector.Observe(ClassicHudBackendState.Loading, null, 0.1f));
        }

        Assert.Equal(HudSurface.Classic, selector.Current);
        Assert.True(selector.Observe(ClassicHudBackendState.Loading, null, 0.2f));
        Assert.Equal(HudSurface.ImGui, selector.Current);
    }

    [Fact]
    public void Observe_ReadyResetsTheLoadingWait()
    {
        HudSurfaceSelector selector = new(false, false);
        selector.Observe(ClassicHudBackendState.Loading, null, 4.9f);
        selector.Observe(ClassicHudBackendState.Ready, null, 0.016f);

        Assert.False(selector.Observe(ClassicHudBackendState.Loading, null, 4.9f));
        Assert.Equal(HudSurface.Classic, selector.Current);
    }

    [Fact]
    public void Observe_IgnoresNonFiniteDeltas()
    {
        HudSurfaceSelector selector = new(false, false);

        Assert.False(selector.Observe(ClassicHudBackendState.Loading, null, float.NaN));
        Assert.False(selector.Observe(ClassicHudBackendState.Loading, null, float.PositiveInfinity));
        Assert.False(selector.Observe(ClassicHudBackendState.Loading, null, -5f));
        Assert.Equal(HudSurface.Classic, selector.Current);
    }
}
