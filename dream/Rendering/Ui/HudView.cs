using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui;

public enum ItemTier : byte
{
    Common,
    Uncommon,
    Rare
}

public readonly record struct HudItemView(InventoryItemSnapshot Item, bool HasIcon, UiSprite Icon, ItemTier Tier, bool Equipped);

// What a click on a world label does (M3). None for the actor labels; the ground-item and NPC labels carry
// the id the click acts on (an item instance id, or an NPC actor id).
public enum HudLabelClickKind : byte
{
    None,
    PickUpGround,
    InteractNpc
}

public readonly record struct HudLabelClick(HudLabelClickKind Kind, long Id);

// Click, Tier and Dim only matter to the ground-item and NPC labels: a clickable label is built from its
// Click, drawn in the colour of its Tier, and Dim greys it (an item another player holds a claim on).
public readonly record struct HudWorldLabel(
    Vector2 Screen,
    string Name,
    string Health,
    HudLabelClick Click = default,
    ItemTier Tier = ItemTier.Common,
    bool Dim = false);

// Everything the classic HUD reads for one frame. One instance is reused: HudViewBuilder refills it
// (rebuilding rows only when status or the inventory snapshot changes). Panels only read it; the
// setters are public so builders and tests in other assemblies can fill it.
public sealed class HudView
{
    // The bag is the 8x8 footprint board: 64 cells, and a bag item is keyed by its top-left anchor cell.
    public const int BackpackSlots = BagGrid.CellCount;
    public const int WornSlots = 12;

    // The kind of world thing under the pointer this frame (set by the scene each frame); drives the cursor.
    public HoverKind HoverCursorKind { get; set; }

    // True while the pointer is over a live enemy a click would attack. Kept for the existing callers: it is
    // HoverCursorKind == Enemy, and setting it sets that kind (or None).
    public bool PointerOverEnemy
    {
        get => HoverCursorKind == HoverKind.Enemy;
        set => HoverCursorKind = value ? HoverKind.Enemy : HoverKind.None;
    }

    // The hover info panel's visibility and fade this frame, and what it shows (null: nothing to show).
    public HoverPanelState HoverPanel { get; set; }

    public HoverInfo? HoverInfo { get; set; }

    // True while a camera rotation drag is held (set by the scene each frame).
    public bool CameraDragging { get; set; }

    private static readonly IReadOnlyList<FloatingCombatTextRenderState> NoCombatText = Array.Empty<FloatingCombatTextRenderState>();
    private static readonly IReadOnlyList<HudWorldLabel> NoLabels = Array.Empty<HudWorldLabel>();

    public HudStatusSnapshot Status { get; set; }

    public PlayerInventorySnapshot? Inventory { get; set; }

    public CharacterSlotRow[] Equipment { get; set; } = [];

    public CharacterStatRow[] StatRows { get; set; } = [];

    // The character window's rows (header, attributes, derived values); built from Status.
    // Built on first read after the status changes (so a frame that never opens the window, or a health
    // tick with it closed, allocates nothing); a caller that sets it keeps its rows.
    public CharacterSheetRow[] Sheet
    {
        get
        {
            if (!_sheetExplicit && (_sheet is null || !_sheetStatus.Equals(Status)))
            {
                _sheet = CharacterSheetModel.Build(Status);
                _sheetStatus = Status;
            }

            return _sheet ?? [];
        }
        set
        {
            _sheet = value;
            _sheetExplicit = true;
        }
    }

    private CharacterSheetRow[]? _sheet;
    private HudStatusSnapshot _sheetStatus;
    private bool _sheetExplicit;

    // The bag items, indexed by anchor cell (0..63, row * 8 + col). A cell with no item anchored on it
    // has Item.ItemInstanceId == 0 and HasIcon false; the other cells an item covers are empty here and
    // are found through BagCells.
    public HudItemView[] Backpack { get; set; } = new HudItemView[BackpackSlots];

    // For each of the 64 cells, the anchor of the item covering it or -1. The item at an anchor covers
    // BagFootprints[anchor] cells. Filled together with Backpack by HudViewBuilder (or TryPlaceBagItem);
    // a hostile snapshot that overlaps two items keeps the first and drops the second.
    public sbyte[] BagCells { get; set; } = NewBagCells();

    // The footprint of the item anchored at each cell; only read where BagCells[anchor] == anchor.
    public ItemFootprint[] BagFootprints { get; set; } = new ItemFootprint[BackpackSlots];

    // How many items the server holds back because the bag has no room for them.
    public int HeldBackCount { get; set; }

    // How big an item kind is on the board. The server rules use BagGrid.FootprintOf (the shipped
    // catalog); a test that does not want to depend on the shipped items swaps in its own.
    public Func<string?, ItemFootprint> FootprintOf { get; set; } = BagGrid.FootprintOf;

    // 12 worn slots in EquipmentSlot order (MainHand, OffHand, Head, Body, Hands, Feet, Legs, Neck,
    // RingLeft, RingRight, Back, Companion).
    public HudItemView[] Worn { get; set; } = new HudItemView[WornSlots];

    public Vector3 PlayerPosition { get; set; }

    public string? RejectionMessage { get; set; }

    public WorldTransferHudState WorldTransfer { get; set; }

    public LootFeedbackSystem? Loot { get; set; }

    // Only numbers the renderer could project; ScreenAnchor of each state is its pixel position.
    public IReadOnlyList<FloatingCombatTextRenderState> CombatText { get; set; } = NoCombatText;

    public IReadOnlyList<HudWorldLabel> Labels { get; set; } = NoLabels;

    // The vendor window's state (M3); null or closed means no vendor window.
    public VendorHudState? Vendor { get; set; }

    // The quickbar's state (M4); null draws the status bar exactly as before the quickbar.
    public QuickbarHudState? Quickbar { get; set; }

    public bool InventoryOpen { get; set; }

    // Gem animation, set once a frame by ClassicHudSurface.Draw (from a frame time capped at 0.1 s) and
    // only read by the panels, so the status bar allocates nothing to animate. HealthLevel and ManaLevel
    // are the eased fill of each gem, 0..1 (what is drawn, not the raw value). AnimationSeconds is the
    // running time the gem phase and glint sweep derive from. GemPulse is the low-health pulse, 0..1,
    // non-zero only under 25% health.
    public float HealthLevel { get; set; }

    public float ManaLevel { get; set; }

    public float AnimationSeconds { get; set; }

    public float GemPulse { get; set; }

    private static sbyte[] NewBagCells()
    {
        sbyte[] cells = new sbyte[BackpackSlots];
        Array.Fill(cells, (sbyte)-1);
        return cells;
    }

    // Empties the bag arrays and the held-back count.
    public void ClearBag()
    {
        if (Backpack.Length != BackpackSlots)
        {
            Backpack = new HudItemView[BackpackSlots];
        }

        if (BagCells.Length != BackpackSlots)
        {
            BagCells = NewBagCells();
        }

        if (BagFootprints.Length != BackpackSlots)
        {
            BagFootprints = new ItemFootprint[BackpackSlots];
        }

        Array.Clear(Backpack);
        Array.Fill(BagCells, (sbyte)-1);
        Array.Clear(BagFootprints);
        HeldBackCount = 0;
    }

    // The cells with an item on them, bit = cell.
    public ulong BagOccupancy()
    {
        ulong occupied = 0;
        sbyte[] cells = BagCells;
        for (int cell = 0; cell < cells.Length && cell < BagGrid.CellCount; cell++)
        {
            if (cells[cell] >= 0)
            {
                occupied |= 1UL << cell;
            }
        }

        return occupied;
    }

    // Puts a bag item on the board with its top-left cell on anchor. False (nothing changes) when the
    // anchor is out of range, the item would run off the board, or it would cover a cell an earlier
    // item already holds: the first item wins.
    public bool TryPlaceBagItem(int anchor, in HudItemView item)
    {
        ItemFootprint footprint = FootprintOf(item.Item.ItemKind);
        if ((uint)anchor >= (uint)Backpack.Length
            || (uint)anchor >= (uint)BagFootprints.Length
            || !BagGrid.TryMask(anchor, footprint, out ulong mask)
            || (BagOccupancy() & mask) != 0)
        {
            return false;
        }

        Backpack[anchor] = item;
        BagFootprints[anchor] = footprint;
        for (int cell = 0; cell < BagGrid.CellCount; cell++)
        {
            if ((mask & (1UL << cell)) != 0)
            {
                BagCells[cell] = (sbyte)anchor;
            }
        }

        return true;
    }

    // The anchor of the item that covers the cell, or -1 for an empty cell or a cell off the board.
    public int BagAnchorAt(int cell) =>
        (uint)cell < (uint)BagCells.Length && (uint)cell < BagGrid.CellCount ? BagCells[cell] : -1;
}
