using Dream.Assets;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

public enum CanvasCallKind
{
    Sprite,
    SpriteClipped,
    NineSlice,
    Tile,
    Solid,
    MaskGlyph,
    Text
}

public sealed record CanvasCall(
    CanvasCallKind Kind,
    string Name,
    UiSprite Sprite,
    UiRect Rect,
    UiColor Color,
    string Text,
    Vector2 TextPos,
    float TextScale = 1f,
    UiTextStyle TextStyle = UiTextStyle.Body);

// Records every draw call. Sprite names are recovered from the real sprite table.
public sealed class InventoryFakeCanvas : IUiCanvas
{
    private readonly Dictionary<(UiPage, int, int, int, int), string> _names = new();

    public InventoryFakeCanvas(UiSpriteTable table)
    {
        foreach (string name in table.Names)
        {
            UiSprite s = table.Get(name);
            _names[(s.Page, s.X, s.Y, s.W, s.H)] = name;
        }
    }

    public List<CanvasCall> Calls { get; } = new();

    public int ClipDepth { get; private set; }

    public float CharWidth { get; set; } = 6f;

    private string NameOf(in UiSprite s)
    {
        UiPage page = s.Page is UiPage.ItemsUncommon or UiPage.ItemsRare ? UiPage.ItemsCommon : s.Page;
        return _names.TryGetValue((page, s.X, s.Y, s.W, s.H), out string? n) ? n : "?";
    }

    private void Add(CanvasCallKind kind, in UiSprite s, UiRect r, UiColor color) =>
        Calls.Add(new CanvasCall(kind, NameOf(s), s, r, color, string.Empty, default));

    public void Sprite(in UiSprite s, UiRect dst, UiColor tint) => Add(CanvasCallKind.Sprite, s, dst, tint);

    public void SpriteClipped(in UiSprite s, UiRect dst, UiRect srcNormalized, UiColor tint) => Add(CanvasCallKind.SpriteClipped, s, dst, tint);

    public void NineSlice(in UiSprite s, UiRect dst, UiColor tint) => Add(CanvasCallKind.NineSlice, s, dst, tint);

    public void Tile(in UiSprite s, UiRect dst, UiColor tint) => Add(CanvasCallKind.Tile, s, dst, tint);

    public void Solid(UiRect dst, UiColor color) =>
        Calls.Add(new CanvasCall(CanvasCallKind.Solid, string.Empty, default, dst, color, string.Empty, default));

    public void MaskGlyph(in UiSprite s, UiRect dst, UiColor fill, UiColor rim) => Add(CanvasCallKind.MaskGlyph, s, dst, fill);

    // The real canvas scales glyphs by TextScale; a fake that ignored it made every "the text fits
    // its box" assertion pass without measuring anything.
    public float TextScale { get; set; } = 1f;

    public void SetTextScale(float scale) => TextScale = scale;

    public float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color)
    {
        Calls.Add(new CanvasCall(CanvasCallKind.Text, string.Empty, default, default, color, text.ToString(), pos, TextScale, style));
        return MeasureText(text, style);
    }

    public float MeasureText(ReadOnlySpan<char> text, UiTextStyle style) =>
        text.Length * CharWidth * TextScale * (style is UiTextStyle.Large or UiTextStyle.BodyLarge ? 2f : style == UiTextStyle.Serif ? 1.85f : 1f);

    public void PushClip(UiRect r) => ClipDepth++;

    public void PopClip() => ClipDepth--;

    public IEnumerable<CanvasCall> Named(string name) => Calls.Where(c => c.Name == name);

    public IEnumerable<CanvasCall> Texts => Calls.Where(c => c.Kind == CanvasCallKind.Text);

    public bool HasText(string text) => Texts.Any(c => c.Text == text);
}

// One scripted pointer. A click or drag step is set before a frame and cleared by EndFrame.
public sealed class InventoryFakeInput : IUiInput
{
    public Vector2 Pointer { get; set; } = new(-1000f, -1000f);

    public bool ReportPointer { get; set; } = true;

    public bool LeftClick { get; set; }

    public bool RightClick { get; set; }

    public bool StartDrag { get; set; }

    public bool ReleaseDrag { get; set; }

    public int ActiveDragPayload { get; private set; } = -1;

    public bool ModalActive { get; set; }

    UiPointer IUiInput.Pointer => new(ReportPointer ? Pointer : new Vector2(float.NaN, float.NaN), false, false, true);

    public bool Button(int id, UiRect r) => LeftClick && r.Contains(Pointer);

    public bool Hovered(int id, UiRect r) => r.Contains(Pointer);

    public bool RightClicked(int id, UiRect r) => RightClick && r.Contains(Pointer);

    public bool BeginDrag(int id, UiRect r, int payload)
    {
        if (StartDrag && ActiveDragPayload < 0 && r.Contains(Pointer))
        {
            ActiveDragPayload = payload;
            return true;
        }

        return false;
    }

    public bool TryEndDrag(out int payload, out Vector2 dropPosition)
    {
        if (ReleaseDrag && ActiveDragPayload >= 0)
        {
            payload = ActiveDragPayload;
            dropPosition = Pointer;
            ActiveDragPayload = -1;
            return true;
        }

        payload = -1;
        dropPosition = default;
        return false;
    }

    public void EndFrame()
    {
        LeftClick = false;
        RightClick = false;
        StartDrag = false;
        ReleaseDrag = false;
    }
}

public sealed class RecordingActions : IHudActions
{
    public List<InventoryHudAction> Picked { get; } = new();

    public int Closes { get; private set; }

    public void Inventory(InventoryHudAction action) => Picked.Add(action);

    public void CloseInventory() => Closes++;
}

// A view plus everything a frame needs. Bag item n is anchored on cell n (0..63) and covers the footprint
// its icon art declares (so a 2x3 blade covers six cells); wells hold Worn. The footprints come from the
// sprite table's item art, not from the shipped items.json, so these scenes do not depend on which kinds
// the content gives a footprint. HeldBack adds that many held-back rows (200..) to the snapshot.
public sealed class InventoryScene : IDisposable
{
    public const int Width = 1280;
    public const int Height = 720;

    public InventoryScene()
    {
        InventoryUiState.Reset();
        ClassicHud.Reset();
        Assert.True(UiSpriteTable.TryLoad(AssetPaths.UiDirectory, out UiSpriteTable? table, out string error), error);
        Table = table!;
        Canvas = new InventoryFakeCanvas(Table);
        Input = new InventoryFakeInput();
        Actions = new RecordingActions();
        Layout = UiLayout.Create(Width, Height, 0f);
        View = new HudView { InventoryOpen = true };
        View.FootprintOf = FootprintOf;
        Items = new InventoryItemSnapshot?[HudView.BackpackSlots];
        Worn = new InventoryItemSnapshot?[HudView.WornSlots];
        Equipped = new bool[HudView.BackpackSlots];
        Refresh();
    }

    public UiSpriteTable Table { get; }

    public InventoryFakeCanvas Canvas { get; private set; }

    public InventoryFakeInput Input { get; }

    public RecordingActions Actions { get; }

    public UiLayout Layout { get; }

    public HudView View { get; }

    // Indexed by anchor cell.
    public InventoryItemSnapshot?[] Items { get; }

    public InventoryItemSnapshot?[] Worn { get; }

    public bool[] Equipped { get; }

    // How many held-back rows the next Refresh puts in the snapshot.
    public int HeldBack { get; set; }

    // Footprints that win over the art's, by item kind.
    public Dictionary<string, ItemFootprint> Footprints { get; } = new();

    public static InventoryItemSnapshot Item(long id, string kind, int equipMask = 0, ItemRarity rarity = ItemRarity.Common, int stack = 1) =>
        new(id, kind, "Item " + id, "visual", stack, "A plain item.", ["Detail one"], null, HeldLightKind.None, equipMask, rarity, false);

    // The footprint of a kind: an override, else the art's, else 1x1.
    public ItemFootprint FootprintOf(string? kind)
    {
        if (kind is not null && Footprints.TryGetValue(kind, out ItemFootprint overridden))
        {
            return overridden;
        }

        return kind is not null && Table.TryGet("item." + kind, out UiSprite icon)
            ? new ItemFootprint((byte)icon.FootprintCols, (byte)icon.FootprintRows)
            : ItemFootprint.One;
    }

    // The rect of bag cell n; for a 1x1 item anchored there, the whole item.
    public UiRect Slot(int n) => InventoryWindow.SlotRect(Layout, Layout.Inventory, n);

    // The rect of the item anchored at anchor: its whole footprint.
    public UiRect ItemRect(int anchor) =>
        InventoryWindow.ItemRect(Layout, Layout.Inventory, anchor, FootprintOf(Items[anchor]?.ItemKind));

    public UiRect Well(int w) => InventoryWindow.WellRect(Layout, Layout.Inventory, w);

    public static Vector2 Centre(UiRect r) => new(r.X + (r.W * 0.5f), r.Y + (r.H * 0.5f));

    // Rebuilds the view and a NEW snapshot from Items, Worn, Equipped and HeldBack. The snapshot is
    // sparse (a row only where an item is anchored). The view places items in anchor order, first wins.
    public void Refresh()
    {
        List<InventorySlotSnapshot> slots = new();
        for (int n = 0; n < HudView.BackpackSlots; n++)
        {
            if (Items[n] is not null)
            {
                slots.Add(new InventorySlotSnapshot(n, Items[n], Equipped[n]));
            }
        }

        for (int n = 0; n < HeldBack; n++)
        {
            slots.Add(new InventorySlotSnapshot(CharacterPanelModel.HeldBackFirstSlot + n, Item(9000 + n, "gem.amber")));
        }

        List<InventorySlotSnapshot> equipment = new();
        InventorySlotSnapshot? main = null;
        for (int w = 0; w < HudView.WornSlots; w++)
        {
            InventorySlotSnapshot entry = new(EquipmentSlots.FirstSlotIndex + w, Worn[w]);
            if (w == 0)
            {
                main = entry;
            }
            else
            {
                equipment.Add(entry);
            }
        }

        View.Inventory = new PlayerInventorySnapshot("Tester", BagGrid.Columns, BagGrid.Rows, slots, main, equipment);
        View.ClearBag();
        for (int n = 0; n < HudView.BackpackSlots; n++)
        {
            if (Items[n] is not null)
            {
                View.TryPlaceBagItem(n, MakeView(Items[n], Equipped[n]));
            }
        }

        View.HeldBackCount = HeldBack;
        View.Worn = new HudItemView[HudView.WornSlots];
        for (int w = 0; w < View.Worn.Length; w++)
        {
            View.Worn[w] = MakeView(Worn[w], equipped: true);
        }

        View.Equipment = CharacterPanelModel.BuildSlotRows(View.Inventory);
        View.StatRows = CharacterPanelModel.BuildStatRows(View.Status);
    }

    private HudItemView MakeView(InventoryItemSnapshot? item, bool equipped)
    {
        if (item is not InventoryItemSnapshot snap)
        {
            return default;
        }

        bool has = Table.TryGet("item." + snap.ItemKind, out UiSprite icon);
        return new HudItemView(snap, has, icon, ItemTierStyle.TierOf(snap.Rarity), equipped);
    }

    // One frame in ClassicHud order (windows, then overlays). Fresh canvas each frame.
    public void Frame()
    {
        Canvas = new InventoryFakeCanvas(Table);
        CharacterWindow.Build(Canvas, Input, View, Layout, Table, Actions);
        InventoryWindow.Build(Canvas, Input, View, Layout, Table, Actions);
        ItemOverlays.Build(Canvas, Input, View, Layout, Table, Actions);
        Input.EndFrame();
    }

    public void Dispose() => InventoryUiState.Reset();
}
