using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Startup;
using Dream.Rendering.Ui;

namespace Dream.Tests.Rendering.Ui;

// One cursor at a time: the ImGui arrow yields to a sprite cursor, and the startup sprite is sized like the others.
public class CursorOwnershipTests
{
    private static UiSprite Sprite(int x, int y) =>
        new(UiPage.Cursors, x, y, 128, 128, 10, 10, 0, 0, 0, 0, UiTile.None, 0, 0);

    [Fact]
    public void ImGuiArrow_IsOffUnderThePauseMenu_ButOnOverAGenuineImGuiWindow()
    {
        // Pause menu open: the menu renderer's gauntlet is the only cursor, whatever the HUD says.
        Assert.False(ClassicCursorPolicy.ImGuiDrawsCursor(false, true, true, false));
        Assert.False(ClassicCursorPolicy.ImGuiDrawsCursor(false, true, true, true));
        // The developer window (settings visible, no pause menu): ImGui owns the pointer.
        Assert.True(ClassicCursorPolicy.ImGuiDrawsCursor(false, true, false, true));
        // Build panel over the classic HUD: the sprite is not drawn there.
        Assert.True(ClassicCursorPolicy.ImGuiDrawsCursor(true, false, false, true));
        // Plain play with the classic HUD: the sprite cursor only.
        Assert.False(ClassicCursorPolicy.ImGuiDrawsCursor(true, false, false, false));
        // ImGui surface in play: the ImGui arrow.
        Assert.True(ClassicCursorPolicy.ImGuiDrawsCursor(false, false, false, false));
    }

    [Theory]
    [InlineData(1920, 1080, 0f)]
    [InlineData(1280, 720, 0f)]
    [InlineData(1920, 1080, 1f)]
    [InlineData(2560, 1440, 3f)]
    public void StartupCursorScale_IsTheMenuLayoutScale(int w, int h, float setting)
    {
        Assert.Equal(UiLayout.Create(w, h, setting).Scale, StartupMenuRenderer.CursorLayoutScale(w, h, setting));
    }

    [Fact]
    public void StartupCursorSize_At1080pAuto_IsSmallerThanTheArtAndMatchesTheMenuCursor()
    {
        UiSprite sprite = Sprite(0, 0);
        float scale = StartupMenuRenderer.CursorLayoutScale(1920, 1080, 0f);
        UiRect r = UiCursor.Rect(in sprite, new Vector2(100f, 100f), scale);

        Assert.Equal(128f * scale / UiCursor.ArtPixelsPerUnit, r.W);
        Assert.True(r.W < 128f);
    }

    [Fact]
    public void StartupCursorUv_DividesByTheGivenPageSize()
    {
        UiSprite sprite = Sprite(128, 256);

        StartupMenuRenderer.CursorUv(in sprite, 512, 1024, out System.Numerics.Vector2 uv0, out System.Numerics.Vector2 uv1);

        Assert.Equal(0.25f, uv0.X);
        Assert.Equal(0.25f, uv0.Y);
        Assert.Equal(0.5f, uv1.X);
        Assert.Equal(0.375f, uv1.Y);
    }
}
