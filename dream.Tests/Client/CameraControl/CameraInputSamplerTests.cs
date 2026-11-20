using Dream.Client.CameraControl;
using Dream.Rendering.Core;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.CameraControl;

public class CameraInputSamplerTests
{
    private static readonly Vector2i Size = new(1000, 600);
    private static readonly CameraGate Open = new(true, true, false, false, false);
    private static readonly Vector2 Centre = new(500, 300);
    private static readonly Vector2 LeftEdge = new(0, 300);

    private static CameraInputSampler.RawInput Raw(
        float x = 0, float scroll = 0, bool middle = false, bool up = false, bool right = false,
        bool comma = false, bool period = false, bool home = false, bool y = false) =>
        new(x, scroll, middle, up, false, false, right, comma, period, home, y);

    private static WorldPointerState At(Vector2 p, bool overHud = false) => new(p, false, false, overHud);

    // Focused, inside, and past the grace window by t = 10.
    private static CameraInputSampler Ready(TacticalCameraSettings? s = null)
    {
        CameraInputSampler sampler = new(s ?? TacticalCameraSettings.Default);
        sampler.OnFocusChanged(true, 0);
        sampler.OnCursorEnterLeave(true, 0);
        return sampler;
    }

    private static void Step(CameraInputSampler s, CameraInputSampler.RawInput raw, WorldPointerState p, CameraGate gate, double now) =>
        s.Collect(raw, Size, 1f, p, gate, now);

    [Fact]
    public void Held_cursor_pans_after_dwell_but_not_before()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(), At(LeftEdge), Open, 10.0);
        Assert.Equal(Vector2.Zero, s.Take().EdgePan);
        Step(s, Raw(), At(LeftEdge), Open, 10.2);
        Assert.Equal(-1f, s.Take().EdgePan.X, 3);
    }

    [Fact]
    public void Quick_sweep_through_the_margin_does_not_pan()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(), At(LeftEdge), Open, 10.0);
        Step(s, Raw(), At(Centre), Open, 10.05);
        Step(s, Raw(), At(LeftEdge), Open, 10.1);
        Assert.Equal(Vector2.Zero, s.Take().EdgePan);
    }

    [Fact]
    public void Dwell_is_skipped_when_confined()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(), At(LeftEdge), Open with { Confined = true }, 10.0);
        Assert.Equal(-1f, s.Take().EdgePan.X, 3);
    }

    [Fact]
    public void Leaving_the_window_stops_the_pan()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(), At(LeftEdge), Open, 10.0);
        Step(s, Raw(), At(LeftEdge), Open, 10.2);
        Assert.NotEqual(Vector2.Zero, s.Take().EdgePan);
        s.OnCursorEnterLeave(false, 10.25);
        Step(s, Raw(), At(LeftEdge), Open, 10.3);
        Assert.Equal(Vector2.Zero, s.Take().EdgePan);
    }

    [Fact]
    public void Position_outside_the_rect_does_not_pan()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(), At(new Vector2(-5, 300)), Open, 10.0);
        Step(s, Raw(), At(new Vector2(-5, 300)), Open, 10.5);
        Assert.Equal(Vector2.Zero, s.Take().EdgePan);
        Step(s, Raw(), At(new Vector2(1000, 300)), Open, 11.0);
        Step(s, Raw(), At(new Vector2(1000, 300)), Open, 11.5);
        Assert.Equal(Vector2.Zero, s.Take().EdgePan);
    }

    [Fact]
    public void Focus_grace_blocks_edge_pan()
    {
        CameraInputSampler s = Ready();
        s.OnFocusChanged(true, 20.0);
        Step(s, Raw(), At(LeftEdge), Open, 20.0);
        Step(s, Raw(), At(LeftEdge), Open, 20.2);
        Assert.Equal(Vector2.Zero, s.Take().EdgePan);
        Step(s, Raw(), At(LeftEdge), Open, 20.3);
        Step(s, Raw(), At(LeftEdge), Open, 20.5);
        Assert.NotEqual(Vector2.Zero, s.Take().EdgePan);
    }

    [Fact]
    public void Edge_pan_ignores_over_hud_but_not_hud_drag()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(), At(LeftEdge, overHud: true), Open, 10.0);
        Step(s, Raw(), At(LeftEdge, overHud: true), Open, 10.2);
        Assert.NotEqual(Vector2.Zero, s.Take().EdgePan);
        Step(s, Raw(), At(LeftEdge), Open with { HudDragActive = true }, 10.3);
        Assert.Equal(Vector2.Zero, s.Take().EdgePan);
    }

    [Fact]
    public void Scripted_pointer_gives_a_zero_frame()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(x: 5, scroll: 3, middle: true), At(LeftEdge), Open with { ScriptedPointer = true }, 10.0);
        Step(s, Raw(x: 9, scroll: 3, middle: true), At(LeftEdge), Open with { ScriptedPointer = true }, 10.5);
        CameraInputFrame f = s.Take();
        Assert.Equal(Vector2.Zero, f.EdgePan);
        Assert.Equal(0f, f.ZoomNotches);
        Assert.False(f.RotateDragActive);
        Assert.Equal(0f, f.RotateDragPixels);
    }

    [Fact]
    public void Wheel_over_hud_is_dropped_and_over_world_counts()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(scroll: 2), At(Centre, overHud: true), Open, 10.0);
        Assert.Equal(0f, s.Take().ZoomNotches);
        Step(s, Raw(scroll: 2), At(Centre), Open, 10.1);
        Assert.Equal(2f, s.Take().ZoomNotches);
    }

    [Fact]
    public void Invert_zoom_flips_the_wheel()
    {
        CameraInputSampler s = Ready(TacticalCameraSettings.Default with { InvertZoom = true });
        Step(s, Raw(scroll: 2), At(Centre), Open, 10.0);
        Assert.Equal(-2f, s.Take().ZoomNotches);
    }

    [Fact]
    public void Two_collects_before_one_take_sum_and_take_resets()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(scroll: 1), At(Centre), Open, 10.0);
        Step(s, Raw(scroll: 2), At(Centre), Open, 10.016);
        Assert.Equal(3f, s.Take().ZoomNotches);
        Assert.Equal(0f, s.Take().ZoomNotches);
    }

    [Fact]
    public void Drag_started_over_hud_never_rotates()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(x: 100, middle: true), At(Centre, overHud: true), Open, 10.0);
        Step(s, Raw(x: 160, middle: true), At(Centre), Open, 10.016);
        CameraInputFrame f = s.Take();
        Assert.False(s.RotateDragHeld);
        Assert.False(f.RotateDragActive);
        Assert.Equal(0f, f.RotateDragPixels);
    }

    [Fact]
    public void Drag_started_over_world_stays_latched_over_hud_and_accumulates()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(x: 100, middle: true), At(Centre), Open, 10.0);
        Step(s, Raw(x: 130, middle: true), At(Centre, overHud: true), Open, 10.016);
        Step(s, Raw(x: 145, middle: true), At(Centre, overHud: true), Open, 10.032);
        CameraInputFrame f = s.Take();
        Assert.True(f.RotateDragActive);
        Assert.True(s.RotateDragHeld);
        Assert.Equal(45f, f.RotateDragPixels, 3);
        Step(s, Raw(x: 145), At(Centre), Open, 10.05);
        f = s.Take();
        Assert.True(f.RotateDragReleased);
        Assert.False(f.RotateDragActive);
    }

    [Fact]
    public void Focus_loss_releases_the_drag_once()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(x: 100, middle: true), At(Centre), Open, 10.0);
        s.OnFocusChanged(false, 10.1);
        Assert.False(s.RotateDragHeld);
        Assert.True(s.Take().RotateDragReleased);
        Assert.False(s.Take().RotateDragReleased);
    }

    [Fact]
    public void Gate_close_releases_the_drag()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(x: 100, middle: true), At(Centre), Open, 10.0);
        Step(s, Raw(x: 110, middle: true), At(Centre), CameraGate.Closed, 10.016);
        Assert.True(s.Take().RotateDragReleased);
        Assert.False(s.RotateDragHeld);
    }

    [Fact]
    public void Held_period_counts_once_and_comma_is_negative()
    {
        CameraInputSampler s = Ready();
        for (int i = 0; i < 5; i++)
        {
            Step(s, Raw(period: true), At(Centre), Open, 10.0 + i * 0.016);
        }

        Assert.Equal(1, s.Take().RotateSteps);
        Step(s, Raw(), At(Centre), Open, 11.0);
        Step(s, Raw(comma: true), At(Centre), Open, 11.1);
        Assert.Equal(-1, s.Take().RotateSteps);
    }

    [Fact]
    public void Home_and_y_are_edge_triggered_and_arrows_pan()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(home: true, y: true, up: true, right: true), At(Centre), Open, 10.0);
        CameraInputFrame f = s.Take();
        Assert.True(f.RecentrePressed);
        Assert.True(f.ToggleAnchorPressed);
        Assert.Equal(1f, f.KeyPan.Length, 3);
        Assert.True(f.KeyPan.X > 0 && f.KeyPan.Y > 0);
        Step(s, Raw(home: true, y: true), At(Centre), Open, 10.016);
        f = s.Take();
        Assert.False(f.RecentrePressed);
        Assert.False(f.ToggleAnchorPressed);
    }

    [Fact]
    public void Closed_gate_gives_no_input()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(scroll: 3, period: true, up: true), At(LeftEdge), CameraGate.Closed, 10.0);
        Step(s, Raw(scroll: 3, period: true, up: true), At(LeftEdge), CameraGate.Closed, 10.5);
        Assert.Equal(CameraInputFrame.None, s.Take());
    }

    [Fact]
    public void Keys_closed_blocks_keys_but_not_the_wheel()
    {
        CameraInputSampler s = Ready();
        Step(s, Raw(scroll: 2, period: true, up: true), At(Centre), Open with { KeysOpen = false }, 10.0);
        CameraInputFrame f = s.Take();
        Assert.Equal(2f, f.ZoomNotches);
        Assert.Equal(0, f.RotateSteps);
        Assert.Equal(Vector2.Zero, f.KeyPan);
    }

    [Fact]
    public void Settings_are_sanitised()
    {
        CameraInputSampler s = new(TacticalCameraSettings.Default with { EdgeMarginPixels = 9999f });
        Assert.Equal(48f, s.Settings.EdgeMarginPixels);
    }
}
