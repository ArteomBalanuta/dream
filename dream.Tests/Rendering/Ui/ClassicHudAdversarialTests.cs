using Dream.Assets;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using Dream.Tests.Rendering.Ui.Panels;
using Dream.Tests.Support;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui;

// Hostile and awkward situations driven through ClassicHudSurface, the way the game drives it: the
// real builder, router, panels and (where it matters) the real UiBatch. Each test names the thing
// that goes wrong at the table (a resize in a drag, a lost window, an item that vanishes, a name that
// is a format string) and pins that the HUD stays up and sends nothing it should not.
[Collection(TimingSensitiveCollection.Name)]
public sealed class ClassicHudAdversarialTests : IDisposable
{
    private sealed class Backend : IClassicHudBackend
    {
        private readonly UiSpriteTable _table;

        public Backend(UiSpriteTable table) => _table = table;

        public ClassicHudBackendState State => ClassicHudBackendState.Ready;

        public string? FailureReason => null;

        public UiSpriteTable? Sprites => _table;

        public UiBatch? Batch { get; set; }

        public InventoryFakeCanvas? Fake { get; private set; }

        public Action<UiBatch>? AfterBegin { get; set; }

        public IUiCanvas BeginFrame(in UiLayout layout)
        {
            if (Batch is UiBatch batch)
            {
                batch.BeginFrame(in layout);
                AfterBegin?.Invoke(batch);
                return batch;
            }

            Fake = new InventoryFakeCanvas(_table);
            return Fake;
        }

        public void Submit()
        {
        }

        public void Dispose()
        {
        }
    }

    private readonly UiSpriteTable _table;
    private readonly Backend _backend;
    private readonly ClassicHudSurface _surface;
    private PlayerInventorySnapshot? _inventory;
    private readonly Dictionary<int, InventoryItemSnapshot> _bag = new();
    private bool _open = true;

    public ClassicHudAdversarialTests()
    {
        InventoryUiState.Reset();
        ClassicHud.Reset();
        Assert.True(UiSpriteTable.TryLoad(AssetPaths.UiDirectory, out UiSpriteTable? table, out string error), error);
        _table = table!;
        _backend = new Backend(_table);
        _surface = new ClassicHudSurface(_backend);
        Publish();
    }

    public void Dispose() => InventoryUiState.Reset();

    private static InventoryItemSnapshot Item(long id, string kind, string name, ItemRarity rarity = ItemRarity.Common) =>
        new(id, kind, name, "visual", 1, "desc of " + name, ["detail of " + name], null, HeldLightKind.None, 0, rarity, false);

    // A NEW snapshot object from _bag, as every decoded snapshot is.
    private void Publish()
    {
        List<InventorySlotSnapshot> slots = new();
        for (int n = 0; n < HudView.BackpackSlots; n++)
        {
            slots.Add(new InventorySlotSnapshot(n, _bag.TryGetValue(n, out InventoryItemSnapshot item) ? item : null, false));
        }

        List<InventorySlotSnapshot> equipment = new();
        for (int w = 1; w < HudView.WornSlots; w++)
        {
            equipment.Add(new InventorySlotSnapshot(EquipmentSlots.FirstSlotIndex + w, null));
        }

        _inventory = new PlayerInventorySnapshot("Tester", 4, 4, slots, new InventorySlotSnapshot(EquipmentSlots.FirstSlotIndex, null), equipment);
    }

    private static readonly HudStatusSnapshot Alive = new(10, 10, default, 1, 2, 3, 1f, 4, 1, 0, 10);

    private int _width = 1920;
    private int _height = 1080;

    private UiLayout Layout => UiLayout.Create(_width, _height, 0f);

    private UiRect Slot(int n) => InventoryWindow.SlotRect(Layout, Layout.Inventory, n);

    private static Vector2 Centre(UiRect r) => InventoryScene.Centre(r);

    private InventoryHudAction? Frame(
        Vector2 at,
        bool left = false,
        bool right = false,
        bool interactive = true,
        HudStatusSnapshot? status = null,
        string? rejection = null,
        WorldTransferHudState transfer = default,
        LootFeedbackSystem? loot = null,
        PlayerInventorySnapshot? inventory = null,
        bool noInventory = false)
    {
        ClassicHudFrame frame = new()
        {
            Width = _width,
            Height = _height,
            ConfiguredScale = 0f,
            Pointer = new UiPointer(at, left, right, interactive),
            Status = status ?? Alive,
            Inventory = noInventory ? null : inventory ?? _inventory,
            InventoryOpen = _open,
            RejectionMessage = rejection,
            WorldTransfer = transfer,
            Loot = loot,
        };
        Assert.True(_surface.Draw(in frame));
        _surface.TakeRequests(out InventoryHudAction? action, out _);
        return action;
    }

    private List<InventoryHudAction> Drag(int from, int to, Action<int>? between = null)
    {
        List<InventoryHudAction> actions = new();
        void Add(InventoryHudAction? picked)
        {
            if (picked is InventoryHudAction x)
            {
                actions.Add(x);
            }
        }

        Vector2 a = Centre(Slot(from));
        Add(Frame(a));
        Add(Frame(a, left: true));
        Add(Frame(a + new Vector2(12f, 0f), left: true));
        between?.Invoke(0);
        // The target is read after the callback, so a resize in it moves the target too.
        Vector2 b = Centre(Slot(to));
        Add(Frame(b, left: true));
        Add(Frame(b));
        Add(Frame(b));
        return actions;
    }

    private static void AssertFinite(UiBatch batch)
    {
        foreach (UiVertex v in batch.Vertices)
        {
            Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.U) && float.IsFinite(v.V), "non-finite vertex");
        }
    }

    // ---- hostile names -------------------------------------------------------------------------

    public static TheoryData<string> HostileNames() => new()
    {
        "100%s {0} %d %n {1:X} {{}}",
        new string('W', 20_000),
        "%" + new string('%', 4_000),
        "{0}",
        "tab\t newline\n nul\0 bidi‮ emoji \U0001F600 high\uD800",
        string.Empty,
    };

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void HostileItemName_ThroughHoverMenuAndConfirm_NeverThrowsAndStaysInTheBatch(string name)
    {
        UiBatch batch = new(BlockGlyphs.Create());
        batch.SetSpriteTable(_table);
        _backend.Batch = batch;
        _bag[0] = Item(11, "tool.torch", name, ItemRarity.Rare);
        _bag[1] = Item(12, "mystery.gizmo", name, ItemRarity.Uncommon);
        Publish();
        Vector2 on0 = Centre(Slot(0));

        // Hover: the tooltip builds from the name, the description and the detail line.
        Frame(on0);
        Frame(on0);
        Frame(on0);
        Assert.True(InventoryUiState.Current.HoverActive);
        Assert.InRange(batch.QuadCount, 1, UiBatch.MaxQuads);
        AssertFinite(batch);

        // Right click: the menu, then the destroy confirm with the name in it.
        Frame(on0, right: true);
        Frame(on0);
        Assert.True(InventoryUiState.Current.MenuOpen);
        AssertFinite(batch);
        List<CharacterMenuEntry> entries = InventoryUiState.Current.MenuEntries;
        int destroyRow = entries.FindIndex(e => e.Label.Contains("Destroy", StringComparison.OrdinalIgnoreCase));
        Assert.True(destroyRow >= 0, "the menu offers Destroy for a bag item");
        Vector2 row = Centre(InventoryUiState.Current.MenuRowRects[destroyRow]);
        Frame(row);
        Frame(row, left: true);
        Frame(row);
        Assert.True(InventoryUiState.Current.ConfirmOpen);
        Frame(row);
        Assert.InRange(batch.QuadCount, 1, UiBatch.MaxQuads);
        AssertFinite(batch);

        // The confirm carries the item id, not the name, so a hostile name changes nothing sent.
        Vector2 ok = Centre(InventoryUiState.Current.ConfirmDestroyRect);
        Frame(ok);
        Frame(ok, left: true);
        InventoryHudAction? sent = Frame(ok);
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Destroy, 0, 0, 11), sent ?? default);

        // The other item (an unknown kind) hovers too.
        Vector2 on1 = Centre(Slot(1));
        for (int i = 0; i < 4; i++)
        {
            Frame(on1);
        }

        Assert.True(InventoryUiState.Current.HoverActive);
        AssertFinite(batch);
    }

    [Fact]
    public void HostileName_InTheNameSlotOfLootAndLabels_StaysLiteral()
    {
        _backend.Batch = null;
        LootFeedbackSystem loot = new();
        loot.AddNotices([new LootNotice("%s{0}%n", ItemRarity.Rare, false), new LootNotice(new string('{', 3000), ItemRarity.Common, false)]);
        Frame(new Vector2(5f, 5f), loot: loot);

        Assert.Contains(_backend.Fake!.Texts, t => t.Text == "Looted: %s{0}%n (Rare)");
        Assert.All(_backend.Fake.Texts, t => Assert.True(t.Text.Length < 3500));
    }

    // ---- unknown kind --------------------------------------------------------------------------

    [Fact]
    public void UnknownItemKind_HasNoIconButHoversDragsAndOpensItsMenu()
    {
        _bag[3] = Item(21, "no.such.kind", "Mystery");
        Publish();

        Frame(Centre(Slot(3)));
        Frame(Centre(Slot(3)));
        Assert.True(InventoryUiState.Current.HoverActive);
        Assert.Contains(_backend.Fake!.Texts, t => t.Text.Contains("Mystery"));

        InventoryHudAction move = Assert.Single(Drag(3, 9));
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 3, 9), move);

        Frame(Centre(Slot(3)));
        Frame(Centre(Slot(3)), right: true);
        Frame(Centre(Slot(3)));
        Assert.True(InventoryUiState.Current.MenuOpen);
    }

    // ---- an item that vanishes mid-hover, mid-menu, mid-drag ---------------------------------------

    [Fact]
    public void ItemRemovedMidHover_TheTooltipGoesAndNothingBreaks()
    {
        _bag[0] = Item(31, "tool.torch", "Torch");
        Publish();
        Vector2 on = Centre(Slot(0));
        Frame(on);
        Frame(on);
        Assert.Contains(_backend.Fake!.Texts, t => t.Text.Contains("Torch"));

        _bag.Remove(0);
        Publish();
        Frame(on);
        Frame(on);

        Assert.DoesNotContain(_backend.Fake!.Texts, t => t.Text.Contains("Torch"));
        Assert.False(InventoryUiState.Current.DragActive);
    }

    [Fact]
    public void ItemRemovedMidDrag_CancelsTheDropAndANewDragWorks()
    {
        _bag[0] = Item(41, "tool.torch", "Torch");
        _bag[1] = Item(42, "tool.torch", "Torch two");
        Publish();

        List<InventoryHudAction> sent = Drag(0, 5, between: _ =>
        {
            _bag.Remove(0);
            Publish();
        });

        Assert.Empty(sent);
        Assert.Equal(-1, _surface.Input.ActiveDragPayload);

        // The button is up and the slot is empty: a drag from slot 1 still works.
        InventoryHudAction move = Assert.Single(Drag(1, 6));
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 1, 6), move);
    }

    // ---- resize, focus, rejection in a drag ------------------------------------------------------

    [Fact]
    public void ResizeDuringADrag_KeepsTheDragAndDropsOnTheNewLayout()
    {
        _bag[0] = Item(51, "tool.torch", "Torch");
        Publish();

        List<InventoryHudAction> sent = Drag(0, 5, between: _ =>
        {
            _width = 1280;
            _height = 720;
        });

        InventoryHudAction move = Assert.Single(sent);
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, 5), move);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(160, 90)]
    [InlineData(7680, 4320)]
    public void ResizeDuringADrag_ToAnExtremeSize_NeverThrowsAndSendsNothingStray(int w, int h)
    {
        _bag[0] = Item(52, "tool.torch", "Torch");
        Publish();
        Vector2 a = Centre(Slot(0));
        Frame(a);
        Frame(a, left: true);
        Frame(a + new Vector2(12f, 0f), left: true);
        _width = w;
        _height = h;

        // A drop that lands on a slot of the resized layout is a move between those two slots.
        Vector2 b = Centre(Slot(5));
        Frame(b, left: true);
        InventoryHudAction? onSlot = Frame(b);
        Frame(b);
        Assert.False(_surface.Input.Dragging);
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, 5), onSlot);

        // And a drop that lands outside every target sends nothing at all.
        Vector2 from = Centre(Slot(0));
        Frame(from);
        Frame(from, left: true);
        Frame(from + new Vector2(12f, 0f), left: true);
        Frame(new Vector2(3f, 3f), left: true);
        InventoryHudAction? outside = Frame(new Vector2(-50f, -50f));
        Frame(new Vector2(-50f, -50f));

        Assert.False(_surface.Input.Dragging);
        Assert.Null(outside);
    }

    [Fact]
    public void RejectionWhileDragging_ShowsTheLine_AndTheDragStillDrops()
    {
        _bag[0] = Item(61, "tool.torch", "Torch");
        Publish();
        string line = "Inventory move rejected because too many inventory actions, try again shortly.";
        bool seen = false;

        List<InventoryHudAction> sent = Drag(0, 5, between: _ =>
        {
            Frame(Centre(Slot(0)) + new Vector2(14f, 0f), left: true, rejection: line);
            seen = _backend.Fake!.Texts.Any(t => t.Text.StartsWith("Inventory move rejected", StringComparison.Ordinal));
        });

        Assert.True(seen, "the rejection line is drawn while the drag is up");
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, 5), Assert.Single(sent));
    }

    [Fact]
    public void WindowLostMidDrag_CancelsIt_AndAButtonStillHeldOnReturnStartsNothing()
    {
        _bag[0] = Item(71, "tool.torch", "Torch");
        Publish();
        Vector2 a = Centre(Slot(0));
        Vector2 b = Centre(Slot(5));
        Frame(a);
        Frame(a, left: true);
        Frame(a + new Vector2(12f, 0f), left: true);
        Assert.True(_surface.Input.Dragging);

        // Minimised or alt-tabbed: not interactive, the button state is whatever the OS last said.
        Assert.Null(Frame(b, left: true, interactive: false));
        Assert.False(_surface.Input.Dragging);
        Assert.Null(Frame(b, left: false, interactive: false));

        // Back, with a press that began elsewhere still held: not a new press, so no drag.
        Frame(a, left: true);
        Frame(a + new Vector2(14f, 0f), left: true);
        Assert.False(_surface.Input.Dragging);
        Assert.Null(Frame(b, left: true));
        Assert.Null(Frame(b));

        // A fresh press and drag afterwards works.
        InventoryHudAction move = Assert.Single(Drag(0, 5));
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, 5), move);
    }

    [Fact]
    public void WindowLostWithAMenuOpen_NeverSendsAnAction_AndRecovers()
    {
        _bag[0] = Item(72, "tool.torch", "Torch");
        Publish();
        Vector2 a = Centre(Slot(0));
        Frame(a);
        Frame(a, right: true);
        Frame(a);
        Assert.True(InventoryUiState.Current.MenuOpen);

        for (int i = 0; i < 5; i++)
        {
            Assert.Null(Frame(new Vector2(400f, 300f), left: i % 2 == 0, interactive: false));
        }

        // Whatever state the menu is in, a click on empty space ends it and a drag still works.
        Frame(new Vector2(10f, 10f));
        Frame(new Vector2(10f, 10f), left: true);
        Frame(new Vector2(10f, 10f));
        Frame(new Vector2(10f, 10f));
        Assert.False(InventoryUiState.Current.ModalOpen);
        Assert.Single(Drag(0, 5));
    }

    // ---- world transfer with the inventory open ----------------------------------------------------

    [Fact]
    public void WorldTransferWithTheInventoryOpen_DropsMenuAndDrag_ShowsTheLine_AndRecovers()
    {
        _bag[0] = Item(81, "tool.torch", "Torch");
        Publish();
        Vector2 a = Centre(Slot(0));
        Frame(a);
        Frame(a, right: true);
        Frame(a);
        Assert.True(InventoryUiState.Current.MenuOpen);

        // The transfer: no snapshot for a while, the receiving line up, the window still open.
        WorldTransferHudState receiving = new(true, 2048, 3, TimeSpan.FromSeconds(1));
        for (int i = 0; i < 3; i++)
        {
            Assert.Null(Frame(a, left: i == 1, transfer: receiving, noInventory: true));
        }

        Assert.Contains(_backend.Fake!.Texts, t => t.Text.StartsWith("Receiving world...", StringComparison.Ordinal));
        Assert.False(InventoryUiState.Current.ModalOpen);

        // The new world's snapshot arrives with a different item in the slot.
        _bag[0] = Item(82, "tool.torch", "Other torch");
        Publish();
        Frame(a, inventory: _inventory);
        Frame(new Vector2(5f, 5f));
        InventoryHudAction move = Assert.Single(Drag(0, 6));
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, 6), move);
    }

    [Fact]
    public void WorldTransferMidDrag_CancelsTheDrop()
    {
        _bag[0] = Item(83, "tool.torch", "Torch");
        Publish();
        Vector2 a = Centre(Slot(0));
        Vector2 b = Centre(Slot(5));
        Frame(a);
        Frame(a, left: true);
        Frame(a + new Vector2(12f, 0f), left: true);
        Assert.True(_surface.Input.Dragging);

        Frame(b, left: true, noInventory: true);
        Assert.Null(Frame(b, noInventory: true));
        Assert.Null(Frame(b));
        Assert.False(_surface.Input.Dragging);
    }

    // ---- the quad limit ------------------------------------------------------------------------------

    [Fact]
    public void QuadLimit_WithLongNoticesAndAFullBatch_DropsAndCountsButNeverThrowsOrBlocksInput()
    {
        UiBatch batch = new(BlockGlyphs.Create());
        batch.SetSpriteTable(_table);
        _backend.Batch = batch;
        for (int n = 0; n < HudView.BackpackSlots; n++)
        {
            _bag[n] = Item(100 + n, "tool.torch", new string('N', 400), (ItemRarity)(n % 3));
        }

        Publish();
        LootFeedbackSystem loot = new();
        loot.AddNotices(Enumerable.Range(0, 20).Select(n => new LootNotice(new string('L', 600) + n, ItemRarity.Rare, false)).ToArray());
        string longRejection = new('R', 5000);

        // Nearly full before the HUD draws anything: the panels hit the limit part way through.
        _backend.AfterBegin = b =>
        {
            for (int i = 0; i < UiBatch.MaxQuads - 40; i++)
            {
                b.Solid(new UiRect(0, 0, 1, 1), UiColor.White);
            }
        };
        List<InventoryHudAction> sent = Drag(0, 5);

        Assert.Equal(UiBatch.MaxQuads, batch.QuadCount);
        Assert.True(batch.DroppedQuads > 0);
        AssertFinite(batch);
        // The drop still resolves: input does not depend on what was drawn.
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, 5), Assert.Single(sent));
        Frame(new Vector2(5f, 5f), loot: loot, rejection: longRejection);
        Assert.Equal(UiBatch.MaxQuads, batch.QuadCount);
        Assert.True(batch.CommandCount <= UiBatch.MaxCommands);

        // The next frame starts empty: the limit is per frame, not sticky.
        _backend.AfterBegin = null;
        Frame(new Vector2(5f, 5f), loot: loot, rejection: longRejection);
        Assert.True(batch.QuadCount < UiBatch.MaxQuads, $"a normal frame fits, used {batch.QuadCount}");
        Assert.Equal(0, batch.DroppedQuads);
    }

    // ---- the real frame, allocation ------------------------------------------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WholeSurfaceFrame_WithTheRealBatchAndBuilder_AllocatesNothingOnceWarm(bool inventoryOpen, bool hovering)
    {
        UiBatch batch = new(BlockGlyphs.Create());
        batch.SetSpriteTable(_table);
        _backend.Batch = batch;
        _open = inventoryOpen;
        _bag[0] = Item(201, "tool.torch", "Torch", ItemRarity.Rare);
        _bag[1] = Item(202, "tool.field-blade", "Blade", ItemRarity.Uncommon);
        Publish();
        LootFeedbackSystem loot = new();
        loot.AddNotices([new LootNotice("Iron blade", ItemRarity.Rare, false)]);
        Vector2 at = hovering ? Centre(Slot(0)) : new Vector2(5f, 5f);

        long bytes = Dream.Tests.Utilities.AllocationAssert.AtMost(
            0,
            () => Frame(at, loot: loot, rejection: "Inventory move rejected."),
            iterations: 50);

        Assert.True(batch.QuadCount > 0);
        Assert.Equal(0, bytes);
    }
}
