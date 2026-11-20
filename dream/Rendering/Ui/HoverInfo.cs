namespace Dream.Rendering.Ui;

// The kinds of world thing the pointer can rest on. None means the pointer is over nothing hoverable.
public enum HoverKind : byte
{
    None,
    Enemy,
    Npc,
    GroundItem,
    RemotePlayer,
    Interactable
}

// Identity of a hoverable thing. Id is the kind's own stable key: the enemy's combat actor id, the NPC's
// actor id, the ground item's instance id, the remote player's actor id. Never a list index.
public readonly record struct HoverTargetId(HoverKind Kind, long Id)
{
    public static readonly HoverTargetId None = default;

    public bool IsNone => Kind == HoverKind.None;
}

// How the info panel colours the title and the frame accent.
public enum HoverDisposition : byte
{
    Neutral,
    Hostile,
    Friendly,
    Party,
    Item
}

public enum HoverBarKind : byte
{
    Health,
    Mana,
    Stamina
}

// One bar in the info panel. Text is the caption drawn on it ("80 / 100"), cached by the provider.
public readonly record struct HoverBar(HoverBarKind Kind, int Current, int Max, string Text);

// One "label: value" row in the info panel.
public readonly record struct HoverLine(string Label, string Value);

// What the info panel shows for the hovered thing, whatever its kind. One instance is filled in place every
// frame by the hover providers and only read by the HUD, so the HUD never sees an entity type. Fixed
// capacity: no allocation per frame. Strings may come from the server; the panel clips them.
public sealed class HoverInfo
{
    public const int MaxBars = 2;
    public const int MaxLines = 4;

    public HoverTargetId Target;
    public HoverDisposition Disposition;
    public string Title = string.Empty;
    public string Subtitle = string.Empty;
    public ItemTier Tier;
    public int BarCount;
    public readonly HoverBar[] Bars = new HoverBar[MaxBars];
    public int LineCount;
    public readonly HoverLine[] Lines = new HoverLine[MaxLines];
    public string Hint = string.Empty;

    // The thing died or vanished while shown: the panel keeps its last values with health at 0 for a moment.
    public bool Defeated;

    public void Clear()
    {
        Target = HoverTargetId.None;
        Disposition = HoverDisposition.Neutral;
        Title = string.Empty;
        Subtitle = string.Empty;
        Tier = ItemTier.Common;
        BarCount = 0;
        Array.Clear(Bars);
        LineCount = 0;
        Array.Clear(Lines);
        Hint = string.Empty;
        Defeated = false;
    }
}

// Whether the info panel draws this frame, and how opaque. Alpha 0 draws nothing.
public readonly record struct HoverPanelState(bool Visible, float Alpha, HoverKind Kind);
