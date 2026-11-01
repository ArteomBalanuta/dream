using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using Xunit;
using Dream.Application;
using Dream.Assets;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Simulation.Combat;

namespace Dream.Tests.Rendering.Ui;

public class ClassicHudFixLoopTests
{
    // ---- cursor decision ----------------------------------------------------------------------

    // classic active, inventory, settings, focused -> expected (gameplay state is Grabbed here).
    [Theory]
    [InlineData(true, true, false, true, CursorState.Hidden)]    // inventory open, classic drawing, focused
    [InlineData(true, true, true, true, CursorState.Grabbed)]    // Settings open: OS cursor rules
    [InlineData(true, false, false, true, CursorState.Grabbed)]  // play, nothing open
    [InlineData(true, true, false, false, CursorState.Grabbed)]  // window not focused: keep an OS cursor
    [InlineData(false, true, false, true, CursorState.Grabbed)]  // loading, latched, F3 to ImGui or forced ImGui
    [InlineData(false, true, true, false, CursorState.Grabbed)]
    public void CursorDecision_Table(bool classic, bool inventory, bool settings, bool focused, CursorState expected)
    {
        Assert.Equal(expected, ClassicCursorPolicy.Decide(classic, inventory, settings, focused, CursorState.Grabbed));
    }

    [Fact]
    public void CursorDecision_KeepsTheGameplayStateWhenNotHidden()
    {
        Assert.Equal(CursorState.Normal, ClassicCursorPolicy.Decide(false, true, false, true, CursorState.Normal));
    }

    [Theory]
    [InlineData(HudSurface.Classic, ClassicHudBackendState.Ready, true)]
    [InlineData(HudSurface.Classic, ClassicHudBackendState.Loading, false)]   // loading
    [InlineData(HudSurface.Classic, ClassicHudBackendState.Failed, false)]    // failed atlas
    [InlineData(HudSurface.ImGui, ClassicHudBackendState.Ready, false)]       // latched, F3 or forced
    public void WillDraw_NeedsTheClassicSurfaceAndAReadyBackend(HudSurface current, ClassicHudBackendState state, bool expected)
    {
        Assert.Equal(expected, ClassicCursorPolicy.WillDraw(current, state));
        Assert.False(ClassicCursorPolicy.WillDraw(current, null));
    }

    // ---- a throwing panel ---------------------------------------------------------------------

    private sealed class NullCanvas : IUiCanvas
    {
        public void Sprite(in UiSprite s, UiRect dst, UiColor tint) { }
        public void SpriteClipped(in UiSprite s, UiRect dst, UiRect srcNormalized, UiColor tint) { }
        public void NineSlice(in UiSprite s, UiRect dst, UiColor tint) { }
        public void Tile(in UiSprite s, UiRect dst, UiColor tint) { }
        public void Solid(UiRect dst, UiColor color) { }
        public void MaskGlyph(in UiSprite s, UiRect dst, UiColor fill, UiColor rim) { }
        public float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color) => 0f;
        public float MeasureText(ReadOnlySpan<char> text, UiTextStyle style) => 0f;
        public void PushClip(UiRect r) { }
        public void PopClip() { }
    }

    private sealed class ThrowingBackend : IClassicHudBackend
    {
        public ClassicHudBackendState State => ClassicHudBackendState.Ready;
        public string? FailureReason => null;
        public UiSpriteTable? Sprites { get; } = Load();
        public IUiCanvas BeginFrame(in UiLayout layout) => new NullCanvas();
        public void Submit() => throw new InvalidOperationException("panel blew up");
        public void Dispose() { }

        private static UiSpriteTable Load()
        {
            Assert.True(UiSpriteTable.TryLoad(AssetPaths.UiDirectory, out UiSpriteTable? table, out string error), error);
            return table!;
        }
    }

    [Fact]
    public void AThrowingFrame_LeavesTheActionUntaken_LatchesImGuiOnce_AndDrawsNothing()
    {
        ClassicHudSurface surface = new(new ThrowingBackend());
        HudSurfaceSelector selector = new(adminAvailable: false, forceImGui: false);
        // A panel earlier in the same frame had already picked an action and a close.
        IHudActions actions = surface;
        actions.Inventory(new InventoryHudAction(InventoryHudActionKind.Move, 1, 2));
        actions.CloseInventory();
        ClassicHudFrame frame = new()
        {
            Width = 1920,
            Height = 1080,
            Pointer = new UiPointer(Vector2.Zero, false, false, false)
        };
        int faults = 0;
        int latches = 0;

        bool drew = ClassicHudFrameGuard.Draw(surface, in frame, out InventoryHudAction? action, out bool close, out Exception? fault);
        if (fault is not null)
        {
            faults++;
            if (selector.Latch(fault.Message))
            {
                latches++;
            }
        }

        Assert.False(drew);
        Assert.Null(action);
        Assert.False(close);
        Assert.Equal(1, faults);
        Assert.Equal(1, latches);
        Assert.Equal(HudSurface.ImGui, selector.Current);
        surface.TakeRequests(out InventoryHudAction? left, out bool leftClose);
        Assert.Null(left);
        Assert.False(leftClose);
    }

    // ---- the local player's incoming number in first person -----------------------------------

    [Theory]
    [InlineData(FloatingCombatTextKind.Incoming, CameraViewMode.FirstPerson, true, true)]    // anchor at the camera
    [InlineData(FloatingCombatTextKind.Incoming, CameraViewMode.FirstPerson, false, true)]   // behind the near plane
    [InlineData(FloatingCombatTextKind.Incoming, CameraViewMode.ThirdPerson, true, false)]   // over the character
    [InlineData(FloatingCombatTextKind.Incoming, CameraViewMode.ThirdPerson, false, true)]   // cannot be projected
    [InlineData(FloatingCombatTextKind.Regular, CameraViewMode.FirstPerson, true, false)]
    [InlineData(FloatingCombatTextKind.Critical, CameraViewMode.FirstPerson, false, false)]
    [InlineData(FloatingCombatTextKind.Critical, CameraViewMode.ThirdPerson, false, false)]
    public void IncomingNumber_UsesTheScreenAnchorByViewMode(FloatingCombatTextKind kind, CameraViewMode mode, bool projected, bool expected)
    {
        Assert.Equal(expected, IncomingNumberAnchor.UsesScreenAnchor(kind, mode, projected));
    }

    // The anchor is frozen at spawn while the camera moves, so the decision cannot depend on distance.
    [Theory]
    [InlineData(0.2f)]
    [InlineData(1.4f)]
    [InlineData(1.6f)]
    [InlineData(30f)]
    public void IncomingNumber_InFirstPerson_StaysOnTheScreenAnchorHoweverFarTheCameraWalks(float distance)
    {
        Vector3 anchor = new(10f, 2f, 10f);
        Vector3 camera = anchor + new Vector3(0f, 0f, distance);

        Assert.True(camera != anchor);
        Assert.True(IncomingNumberAnchor.UsesScreenAnchor(FloatingCombatTextKind.Incoming, CameraViewMode.FirstPerson, true));
    }

    [Theory]
    [InlineData(0.2f)]
    [InlineData(1.4f)]
    [InlineData(30f)]
    public void IncomingNumber_InThirdPerson_ProjectsOverTheCharacterEvenWhenTheCameraIsClose(float distance)
    {
        Vector3 anchor = new(10f, 2f, 10f);
        Vector3 camera = anchor + new Vector3(0f, 0f, distance);

        Assert.True(camera != anchor);
        Assert.False(IncomingNumberAnchor.UsesScreenAnchor(FloatingCombatTextKind.Incoming, CameraViewMode.ThirdPerson, true));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(1100, 700)]
    public void IncomingScreenAnchor_IsLowerCentre_AboveTheBarAndBelowTheCrosshair(int w, int h)
    {
        Vector2 a = IncomingNumberAnchor.ScreenAnchor(w, h);
        UiLayout layout = UiLayout.Create(w, h, 0f);

        Assert.Equal(w * 0.5f, a.X);
        Assert.True(a.Y > h * 0.5f, "below the crosshair");
        Assert.True(a.Y < layout.Bar.Y - (20f * layout.Scale), "clear of the bar");
    }
}
