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

public readonly record struct HudWorldLabel(Vector2 Screen, string Name, string Health);

// Everything the classic HUD reads for one frame. One instance is reused: HudViewBuilder refills it
// (rebuilding rows only when status or the inventory snapshot changes). Panels only read it; the
// setters are public so builders and tests in other assemblies can fill it.
public sealed class HudView
{
    public const int BackpackSlots = 16;
    public const int WornSlots = 4;

    private static readonly IReadOnlyList<FloatingCombatTextRenderState> NoCombatText = Array.Empty<FloatingCombatTextRenderState>();
    private static readonly IReadOnlyList<Vector2> NoScreenPoints = Array.Empty<Vector2>();
    private static readonly IReadOnlyList<HudWorldLabel> NoLabels = Array.Empty<HudWorldLabel>();

    public HudStatusSnapshot Status { get; set; }

    public PlayerInventorySnapshot? Inventory { get; set; }

    public CharacterSlotRow[] Equipment { get; set; } = [];

    public CharacterStatRow[] StatRows { get; set; } = [];

    // 16 backpack slots. An empty slot has Item.ItemInstanceId == 0 and HasIcon false.
    public HudItemView[] Backpack { get; set; } = new HudItemView[BackpackSlots];

    // 4 worn slots in equipment order (MainHand, OffHand, Head, Body).
    public HudItemView[] Worn { get; set; } = new HudItemView[WornSlots];

    public Vector3 PlayerPosition { get; set; }

    public string? RejectionMessage { get; set; }

    public WorldTransferHudState WorldTransfer { get; set; }

    public LootFeedbackSystem? Loot { get; set; }

    public IReadOnlyList<FloatingCombatTextRenderState> CombatText { get; set; } = NoCombatText;

    public IReadOnlyList<HudWorldLabel> Labels { get; set; } = NoLabels;

    // Additive after C0: the screen anchor (pixels) of CombatText[i]. The surface projects the
    // text and keeps only what is in front of the camera, so both lists have the same length.
    public IReadOnlyList<Vector2> CombatTextScreen { get; set; } = NoScreenPoints;

    public bool InventoryOpen { get; set; }
}
