using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using Dream.Tests.Rendering.Ui.Panels;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-HUD-1: HudViewBuilder deliberately does not count a gold-only inventory push as an inventory
// change, so that kill gold or a quest reward cannot cancel a drag. The panel state keys its own
// "the bag moved" test on the Slots/Equipment list references, so the carve-out only holds if those
// references survive the push. State only: no GL context, no window.
public sealed class HudGoldOnlyPushTests : IDisposable
{
    private readonly InventoryScene _scene = new();

    public void Dispose() => _scene.Dispose();

    private static PlayerInventorySnapshot Bag(long gold, long itemId = 1)
        => new(
            "Tester",
            BagGrid.Columns,
            BagGrid.Rows,
            [new InventorySlotSnapshot(0, InventoryScene.Item(itemId, "tool.torch"))],
            null,
            [],
            gold);

    private (HudView View, HudViewBuilder Builder) FreshBuilder(PlayerInventorySnapshot first)
    {
        HudView view = new() { InventoryOpen = true };
        view.FootprintOf = _scene.FootprintOf;
        HudViewBuilder builder = new(_scene.Table, view);
        HudStatusSnapshot status = default;
        builder.Refresh(in status, first);
        InventoryUiState.Current.BeginFrame(view);
        return (view, builder);
    }

    private static void Push(HudViewBuilder builder, PlayerInventorySnapshot snapshot)
    {
        HudStatusSnapshot status = default;
        builder.Refresh(in status, snapshot);
    }

    [Fact]
    public void AGoldOnlyPush_KeepsADragAlive_AndStillShowsTheNewWallet()
    {
        (HudView view, HudViewBuilder builder) = FreshBuilder(Bag(gold: 10));
        InventoryUiState st = InventoryUiState.Current;
        st.StartDrag(0, view);
        Assert.True(st.DragActive);

        // A fresh snapshot object with fresh lists, identical except for the wallet: a kill-gold or
        // quest-gold push, whose own server comment says the snapshot only carries the wallet.
        Push(builder, Bag(gold: 50));

        Assert.False(builder.InventoryChanged);
        Assert.Equal(50, view.Inventory!.Value.Gold);

        st.BeginFrame(view);

        Assert.True(st.DragActive);
        Assert.False(st.DragCancelled);
    }

    [Fact]
    public void AGoldOnlyPush_LeavesTheOpenItemMenuUp()
    {
        (HudView view, HudViewBuilder builder) = FreshBuilder(Bag(gold: 10));
        InventoryUiState st = InventoryUiState.Current;
        st.OpenMenu(view, 0, new UiRect(0f, 0f, 10f, 10f), new Vector2(100f, 100f));
        Assert.True(st.MenuOpen);

        Push(builder, Bag(gold: 50));
        st.BeginFrame(view);

        Assert.True(st.MenuOpen);
    }

    // The carve-out is only for the wallet: a real bag change must still cancel the drag and close the
    // menu, because the slots may no longer hold what was picked up.
    [Fact]
    public void ARealBagChange_StillCancelsTheDrag_AndClosesTheMenu()
    {
        (HudView view, HudViewBuilder builder) = FreshBuilder(Bag(gold: 10));
        InventoryUiState st = InventoryUiState.Current;
        st.OpenMenu(view, 0, new UiRect(0f, 0f, 10f, 10f), new Vector2(100f, 100f));
        st.StartDrag(0, view);

        Push(builder, Bag(gold: 10, itemId: 2));

        Assert.True(builder.InventoryChanged);
        st.BeginFrame(view);

        Assert.True(st.DragCancelled);
        Assert.False(st.MenuOpen);
    }
}
