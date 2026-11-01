using Dream.Client.Scenes;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using Dream.Tests.Support;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Rendering.Ui.Panels;

// Classic HUD iteration 2: the draw budget with the full inventory (twelve worn items, a bag packed with
// every icon in the art over all three tiers, held-back rows), through the real batch and the real router.
// The plan's gate: at most 12 draw commands in play, 16 with windows and a tooltip, under 8192 quads.
public sealed class InventoryDrawBudgetTests : IDisposable
{
    private const int PlayBudget = 12;
    private const int WindowBudget = 16;
    private const int MaxQuads = 8192;

    private readonly ITestOutputHelper _output;
    private readonly InventoryScene _scene = new();

    public InventoryDrawBudgetTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _scene.Dispose();

    private sealed class NoActions : IHudActions
    {
        public void Inventory(InventoryHudAction action) { }

        public void CloseInventory() { }
    }

    // Twelve worn items and a bag filled first-fit with one item of every icon kind, cycling the tiers.
    private void Populate()
    {
        string[] worn =
        [
            "gear.iron-blade", "gear.ward-lantern", "gear.leather-cap", "gear.leather-vest", "gear.iron-gauntlets", "gear.iron-boots",
            "gear.plated-leggings", "gear.ward-pendant", "gear.iron-band", "gear.iron-band", "gear.travel-cloak", "gear.ember-wisp"
        ];
        ItemRarity[] tiers = [ItemRarity.Common, ItemRarity.Uncommon, ItemRarity.Rare];
        for (int w = 0; w < worn.Length; w++)
        {
            _scene.Worn[w] = InventoryScene.Item(500 + w, worn[w], rarity: tiers[w % 3]);
        }

        ulong occupied = 0;
        int n = 0;
        foreach (string name in _scene.Table.Names.Where(x => x.StartsWith("item.", StringComparison.Ordinal)).Order())
        {
            string kind = name["item.".Length..];
            ItemFootprint footprint = _scene.FootprintOf(kind);
            int anchor = BagGrid.FirstFit(occupied, footprint);
            if (anchor < 0)
            {
                continue;
            }

            BagGrid.TryMask(anchor, footprint, out ulong mask);
            occupied |= mask;
            _scene.Items[anchor] = InventoryScene.Item(1 + n, kind, rarity: tiers[n % 3], stack: n % 5 == 4 ? 12 : 1);
            n++;
        }

        _scene.HeldBack = 2;
        _scene.Refresh();
        _scene.View.Status = new HudStatusSnapshot(
            73, 120, Dream.Simulation.Players.PlayerInteractionMode.Combat, 5, 9, 12, 1.2f, 14, 7, 40, 100, 10, 1f, 10, 3, false, 0);
        _scene.View.StatRows = CharacterPanelModel.BuildStatRows(_scene.View.Status);
        // The art's items cover 112 cells in all, so the board fills up: nearly every cell is under an item.
        Assert.True(n >= 12, $"only {n} icons fit");
        Assert.True(System.Numerics.BitOperations.PopCount(occupied) >= 56, $"{System.Numerics.BitOperations.PopCount(occupied)} cells covered");
    }

    private UiBatch Build(int w, int h, IEnumerable<UiPointer> frames)
    {
        (UiGlyphTable glyphs, _) = RealFont.Get();
        UiLayout layout = UiLayout.Create(w, h, 0f);
        UiBatch batch = new(glyphs);
        batch.SetSpriteTable(_scene.Table);
        UiInputRouter router = new();
        NoActions actions = new();
        foreach (UiPointer p in frames)
        {
            batch.BeginFrame(in layout);
            router.BeginFrame(p);
            ClassicHud.Build(batch, router, _scene.View, in layout, _scene.Table, actions);
            router.EndFrame();
        }

        foreach (UiDrawCommand cmd in batch.Commands)
        {
            _output.WriteLine($"{cmd.Page} quads={cmd.IndexCount / 6} clip={cmd.Clip} free={cmd.PageFree}");
        }

        return batch;
    }

    private static UiPointer At(Vector2 p, bool left = false) => new(p, left, false, true);

    private int AnchorOfFirst(int cols, int rows)
    {
        for (int a = 0; a < 64; a++)
        {
            if (InventoryWindow.IsPlaced(_scene.View, a) && _scene.View.BagFootprints[a] == new ItemFootprint((byte)cols, (byte)rows))
            {
                return a;
            }
        }

        throw new InvalidOperationException($"no {cols}x{rows} item in the bag");
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(1100, 700)]
    public void Play_StaysInsideTheBudget(int w, int h)
    {
        Populate();
        _scene.View.InventoryOpen = false;

        UiBatch batch = Build(w, h, [At(Vector2.Zero)]);

        _output.WriteLine($"play commands={batch.CommandCount} quads={batch.QuadCount}");
        Assert.True(batch.CommandCount <= PlayBudget, $"{batch.CommandCount} commands in play, budget {PlayBudget}");
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(1100, 700)]
    public void FullInventory_NoPointer_StaysInsideTheBudget(int w, int h)
    {
        Populate();

        UiBatch batch = Build(w, h, [At(new Vector2(5f, 5f)), At(new Vector2(5f, 5f))]);

        _output.WriteLine($"full inventory commands={batch.CommandCount} quads={batch.QuadCount} dropped={batch.DroppedQuads}");
        Assert.True(batch.CommandCount <= WindowBudget, $"{batch.CommandCount} commands, budget {WindowBudget}");
        Assert.InRange(batch.QuadCount, 1, MaxQuads - 1);
        Assert.Equal(0, batch.DroppedQuads);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(1100, 700)]
    public void FullInventory_WithATooltipOverAMultiCellItem_StaysInsideTheBudget(int w, int h)
    {
        Populate();
        int anchor = AnchorOfFirst(2, 3);
        UiLayout layout = UiLayout.Create(w, h, 0f);
        // The pointer is on the footprint's last cell, not its anchor.
        Vector2 hover = InventoryScene.Centre(InventoryWindow.SlotRect(layout, layout.Inventory, anchor + 17));
        UiPointer p = At(hover);

        UiBatch batch = Build(w, h, [p, p, p]);

        _output.WriteLine($"inventory+tooltip commands={batch.CommandCount} quads={batch.QuadCount}");
        Assert.True(InventoryUiState.Current.HoverActive);
        Assert.Equal(anchor, InventoryUiState.Current.HoverSource);
        Assert.True(batch.CommandCount <= WindowBudget, $"{batch.CommandCount} commands with windows and a tooltip, budget {WindowBudget}");
        Assert.Equal(0, batch.DroppedQuads);
        Assert.InRange(batch.QuadCount, 1, MaxQuads - 1);
    }

    [Fact]
    public void FullInventory_WithATooltipOverAWornItem_StaysInsideTheBudget()
    {
        Populate();
        UiLayout layout = UiLayout.Create(1920, 1080, 0f);
        UiPointer p = At(InventoryScene.Centre(InventoryWindow.WellRect(layout, layout.Inventory, (int)EquipmentSlot.Back)));

        UiBatch batch = Build(1920, 1080, [p, p, p]);

        Assert.True(InventoryUiState.Current.HoverActive);
        Assert.Equal(EquipmentSlots.ToSlotIndex(EquipmentSlot.Back), InventoryUiState.Current.HoverSource);
        Assert.True(batch.CommandCount <= WindowBudget, $"{batch.CommandCount} commands, budget {WindowBudget}");
    }

    // The drag-ghost case, reported: the window, then the ghost on an item page, then the highlight.
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void FullInventory_WhileDraggingAMultiCellItem_ReportsItsCommandCount(int w, int h)
    {
        Populate();
        int anchor = AnchorOfFirst(2, 3);
        UiLayout layout = UiLayout.Create(w, h, 0f);
        Vector2 from = InventoryScene.Centre(InventoryWindow.SlotRect(layout, layout.Inventory, anchor));
        // Carried over the middle of the board: the ghost, a highlight (valid or not) and no tooltip.
        Vector2 over = InventoryScene.Centre(InventoryWindow.SlotRect(layout, layout.Inventory, 36));
        List<ScriptedPointerFrame> script = new();
        HudCapturePointerPlan.Drag(script, from, over, release: false);

        UiBatch batch = Build(w, h, script.Select(f => new UiPointer(f.Position, f.LeftDown, f.RightDown, true)));

        _output.WriteLine($"drag ghost {w}x{h}: commands={batch.CommandCount} quads={batch.QuadCount} dropped={batch.DroppedQuads}");
        Assert.True(InventoryUiState.Current.DragActive);
        Assert.Equal(0, batch.DroppedQuads);
        Assert.InRange(batch.QuadCount, 1, MaxQuads - 1);
        Assert.True(batch.CommandCount <= WindowBudget, $"{batch.CommandCount} commands while dragging, budget {WindowBudget}");
    }
}
