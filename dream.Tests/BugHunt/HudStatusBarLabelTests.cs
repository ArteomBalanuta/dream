using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Tests.Rendering.Ui.Panels;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-HUD-3: the world labels are built first, under the status bar's opaque art, and with the
// inventory closed the bar registers no widget of its own over most of its width. A click on the
// bar's art must not reach the invisible label hit box behind it. Geometry only: no GL context.
public sealed class HudStatusBarLabelTests : IDisposable
{
    private readonly InventoryScene _scene = new();

    public void Dispose() => _scene.Dispose();

    private sealed class PickRecorder : IHudActions
    {
        public List<long> Pickups { get; } = new();

        public List<int> Talks { get; } = new();

        public void Inventory(InventoryHudAction action)
        {
        }

        public void CloseInventory()
        {
        }

        public void PickUpGround(long itemInstanceId) => Pickups.Add(itemInstanceId);

        public void InteractNpc(int npcActorId) => Talks.Add(npcActorId);
    }

    private PickRecorder ClickLabelAt(Vector2 anchor, Vector2 click, bool inventoryOpen)
    {
        PickRecorder actions = new();
        HudView view = new()
        {
            InventoryOpen = inventoryOpen,
            Labels = [new HudWorldLabel(anchor, "Torch", string.Empty, new HudLabelClick(HudLabelClickKind.PickUpGround, 77))],
        };
        _scene.Input.Pointer = click;
        _scene.Input.LeftClick = true;
        WorldLabelsPanel.Build(_scene.Canvas, _scene.Input, view, _scene.Layout, actions);
        _scene.Input.EndFrame();
        return actions;
    }

    [Fact]
    public void ALabelUnderTheStatusBar_TakesNoClick_WithTheInventoryClosed()
    {
        UiRect bar = _scene.Layout.Bar;
        // Inside the bar's art and inside the viewport, so the label is placed, not culled.
        Vector2 anchor = new(bar.X + (bar.W * 0.5f), bar.Y + 4f);
        Vector2 click = new(anchor.X, anchor.Y + 2f);
        Assert.True(bar.Contains(click), "the test point must be under the bar's art");

        Assert.Empty(ClickLabelAt(anchor, click, inventoryOpen: false).Pickups);
        Assert.Empty(ClickLabelAt(anchor, click, inventoryOpen: true).Pickups);
    }

    // The same label clear of the bar still works, so the gate suppresses the bar strip and nothing else.
    [Fact]
    public void ALabelAboveTheStatusBar_StillPicksUp()
    {
        Vector2 anchor = new(_scene.Layout.ViewportWidth * 0.5f, _scene.Layout.Bar.Y - 60f);

        Assert.Equal([77L], ClickLabelAt(anchor, new Vector2(anchor.X, anchor.Y + 2f), inventoryOpen: false).Pickups);
    }

    // The label gate and the click-to-move gate must agree: a point the hit test calls HUD is never
    // world for a label either.
    [Fact]
    public void TheLabelGate_UsesTheSameRuleAsTheClickToMoveGate()
    {
        UiLayout l = _scene.Layout;
        Vector2 inBar = new(l.Bar.X + (l.Bar.W * 0.5f), l.Bar.Y + 4f);

        Assert.True(ClassicHudHitTest.Over(in l, inventoryOpen: false, inBar));
        Assert.False(ClassicHudHitTest.OverWorld(in l, inventoryOpen: false, inBar));
    }
}
