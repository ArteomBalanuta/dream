using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using Dream.Simulation.Stats;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// What the inventory windows remember between frames: the hovered item, the drag, the open menu and
// the destroy confirm. One instance per thread (the render thread in the game; a test's own thread
// in tests, which call Reset first). Panels are static, so this is their only state.
public sealed class InventoryUiState
{
    [ThreadStatic]
    private static InventoryUiState? _current;

    public static InventoryUiState Current => _current ??= new InventoryUiState();

    // Forget everything. Tests call this at start and end.
    public static void Reset() => _current = null;

    // The item the pointer rests on this frame, as a widget index (0..63 the bag anchor of the item under
    // any cell it covers, 100..111 well).
    public bool HoverActive { get; private set; }

    public int HoverSource { get; private set; }

    public UiRect HoverRect { get; private set; }

    public bool DragActive { get; private set; }

    public bool DragCancelled { get; private set; }

    public int DragSource { get; private set; }

    public bool MenuOpen { get; private set; }

    public int MenuSource { get; private set; }

    public Vector2 MenuPosition { get; private set; }

    public List<CharacterMenuEntry> MenuEntries { get; } = new(8);

    // Where the menu rows and the confirm buttons were drawn last, in screen pixels. The capture
    // script clicks them; nothing in the panels reads them.
    internal List<UiRect> MenuRowRects { get; } = new(8);

    internal UiRect ConfirmDestroyRect { get; set; }

    internal UiRect ConfirmCancelRect { get; set; }

    public bool ConfirmOpen { get; private set; }

    public InventoryHudAction ConfirmAction { get; private set; }

    public string ConfirmName { get; private set; } = string.Empty;

    public bool ModalOpen => MenuOpen || ConfirmOpen;

    // True when this state last set IUiInput.ModalActive, so it only clears what it set.
    internal bool ModalOwned { get; set; }

    internal TooltipContent? Tooltip { get; set; }

    // True from the moment a menu or confirm opens until its first draw. The click that opened it
    // must not also hit its backdrop or a button under the pointer, whatever the router thinks.
    internal bool ModalFresh { get; set; }

    private bool _keySet;
    private object? _slotsKey;
    private object? _equipmentKey;

    // Called first each frame. A new inventory snapshot cancels a drag in progress (the slots may
    // not hold what was picked up any more) and closes the item menu. An open destroy confirm
    // survives unless its item is gone.
    internal void BeginFrame(HudView v)
    {
        HoverActive = false;
        object? slots = v.Inventory?.Slots;
        object? equipment = v.Inventory?.Equipment;
        bool changed = _keySet && (!ReferenceEquals(slots, _slotsKey) || !ReferenceEquals(equipment, _equipmentKey));
        _keySet = true;
        _slotsKey = slots;
        _equipmentKey = equipment;
        if (!changed)
        {
            return;
        }

        if (DragActive)
        {
            DragCancelled = true;
        }

        CloseMenu();
        if (ConfirmOpen
            && !(InventoryWindow.TryItem(v.Backpack, ConfirmAction.SlotIndex, out InventoryItemSnapshot item)
                && item.ItemInstanceId == ConfirmAction.ItemInstanceId))
        {
            CloseConfirm();
        }
    }

    internal void SetHover(int source, UiRect rect)
    {
        HoverActive = true;
        HoverSource = source;
        HoverRect = rect;
    }

    internal void StartDrag(int source, HudView v)
    {
        DragActive = true;
        DragCancelled = false;
        DragSource = source;
        CloseMenu();
    }

    internal bool IsDraggingSource(int source) => DragActive && DragSource == source;

    internal void ClearDrag()
    {
        DragActive = false;
        DragCancelled = false;
    }

    // Opens the right-click menu of the item in slot index. Nothing opens for an empty slot or a
    // snapshot-less view.
    internal void OpenMenu(HudView v, int index, UiRect rect, Vector2 pointer)
    {
        if (v.Inventory is not PlayerInventorySnapshot snapshot)
        {
            return;
        }

        List<CharacterMenuEntry> entries;
        if (EquipmentSlots.TryFromSlotIndex(index, out EquipmentSlot slot))
        {
            if (!InventoryWindow.TryItem(v.Worn, (int)slot, out _))
            {
                return;
            }

            entries = CharacterPanelModel.EquipmentMenu(slot);
        }
        else
        {
            if (!InventoryWindow.TryItem(v.Backpack, index, out InventoryItemSnapshot item))
            {
                return;
            }

            entries = CharacterPanelModel.BackpackMenu(snapshot, index, item);
        }

        if (entries.Count == 0)
        {
            return;
        }

        MenuEntries.Clear();
        MenuEntries.AddRange(entries);
        MenuSource = index;
        MenuOpen = true;
        ModalFresh = true;
        MenuPosition = float.IsFinite(pointer.X) && float.IsFinite(pointer.Y)
            ? pointer
            : new Vector2(rect.X + (rect.W * 0.5f), rect.Y + (rect.H * 0.5f));
    }

    internal void CloseMenu()
    {
        MenuOpen = false;
        MenuEntries.Clear();
    }

    internal void OpenConfirm(InventoryHudAction action, string name)
    {
        ConfirmOpen = true;
        ModalFresh = true;
        ConfirmAction = action;
        ConfirmName = name ?? string.Empty;
        CloseMenu();
    }

    internal void CloseConfirm()
    {
        ConfirmOpen = false;
        ConfirmAction = default;
        ConfirmName = string.Empty;
    }

    internal void ResetAll()
    {
        HoverActive = false;
        ClearDrag();
        CloseMenu();
        CloseConfirm();
        _keySet = false;
        _slotsKey = null;
        _equipmentKey = null;
    }
}

// Wrapped, coloured tooltip lines of one item. Rebuilt only when the item or the wrap width
// changes, so a hover costs no allocation after the first frame.
internal sealed class TooltipContent
{
    public enum LineKind : byte
    {
        Name,
        Description,
        Detail,
        Tier,
        Bonus,
        Count
    }

    public readonly List<(string Text, LineKind Kind)> Lines = new(16);

    public long ItemInstanceId;
    public string? DisplayName;
    public string? Description;
    public object? DetailLines;
    public object? Modifiers;
    public int StackCount;
    public ItemRarity Rarity;
    public float WrapWidth;

    // The glyph size the lines were wrapped at. Without it, changing ui_scale while hovering kept the
    // lines broken for the old size: the text got bigger but the wrap points did not move.
    public float TextPx;

    public bool Matches(in InventoryItemSnapshot item, float wrapWidth, float textPx) =>
        ItemInstanceId == item.ItemInstanceId
        && ReferenceEquals(DisplayName, item.DisplayName)
        && ReferenceEquals(Description, item.Description)
        && ReferenceEquals(DetailLines, item.DetailLines)
        && ReferenceEquals(Modifiers, item.Modifiers)
        && StackCount == item.StackCount
        && Rarity == item.Rarity
        && WrapWidth == wrapWidth
        && TextPx == textPx;
}

// Tooltip, context menu, destroy confirm and drag ghost. Drawn after the windows and the bar so
// they sit on top; the menu and the confirm are modal.
public static class ItemOverlays
{
    // How far right of the pointer the item menu starts, in units: the width of the cursor's hand.
    internal const float MenuCursorGapUnits = 18f;

    public const int MaxWrappedLines = 6;
    public const int MaxDetailLines = 8;
    private const int MaxTextChars = 400;

    private static readonly UiColor White = UiColor.White;
    private static readonly UiColor Scrim = new(0, 0, 0, 120);
    private static readonly UiColor RowHover = new(255, 255, 255, 44);
    private static readonly UiColor GhostTint = new(255, 255, 255, 200);

    public static void Build(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, IHudActions a)
    {
        InventoryUiState st = InventoryUiState.Current;
        if (!v.InventoryOpen)
        {
            ReleaseModal(i, st);
            return;
        }

        bool dragging = st.DragActive && !st.DragCancelled && i.ActiveDragPayload >= 0;
        if (st.HoverActive && !dragging && !st.ModalOpen)
        {
            DrawTooltip(c, v, l, s, st);
        }

        if (st.MenuOpen)
        {
            DrawMenu(c, i, v, l, s, a, st);
        }

        if (st.ConfirmOpen && !st.MenuOpen)
        {
            DrawConfirm(c, i, l, s, a, st);
        }
        else if (!st.ModalOpen)
        {
            ReleaseModal(i, st);
        }

        if (dragging)
        {
            DrawGhost(c, i, v, l, s, st);
        }
    }

    private static void ReleaseModal(IUiInput i, InventoryUiState st)
    {
        if (st.ModalOwned)
        {
            i.ModalActive = false;
            st.ModalOwned = false;
        }

        if (!st.ModalOpen)
        {
            return;
        }

        st.CloseMenu();
        st.CloseConfirm();
    }

    // Modal buttons must be answered while ModalActive is set; set it right before they are built.
    private static void HoldModal(IUiInput i, InventoryUiState st)
    {
        i.ModalActive = true;
        st.ModalOwned = true;
    }

    // ---- tooltip ----------------------------------------------------------------------------

    // Panel top-left for a tooltip of w x h over footprint: centred on the footprint, above with
    // the panel bottom half a cell (10 u) into the footprint's top, flipped below (panel top =
    // footprint bottom) when that would leave the screen, then clamped to the screen.
    internal static Vector2 PlaceTooltip(UiRect footprint, float w, float h, float uiScale, float viewportW, float viewportH)
    {
        float x = footprint.X + ((footprint.W - w) * 0.5f);
        float y = footprint.Y + (InventoryWindow.CellUnits * 0.5f * uiScale) - h;
        if (y < 0f)
        {
            y = footprint.Bottom;
        }

        x = MathF.Max(0f, MathF.Min(x, viewportW - w));
        y = MathF.Max(0f, MathF.Min(y, viewportH - h));
        return new Vector2(x, y);
    }

    private static void DrawTooltip(IUiCanvas c, HudView v, in UiLayout l, UiSpriteTable s, InventoryUiState st)
    {
        HudItemView[] source = st.HoverSource >= EquipmentSlots.FirstSlotIndex ? v.Worn : v.Backpack;
        int index = st.HoverSource >= EquipmentSlots.FirstSlotIndex ? st.HoverSource - EquipmentSlots.FirstSlotIndex : st.HoverSource;
        if (!InventoryWindow.TryItem(source, index, out InventoryItemSnapshot item))
        {
            return;
        }

        float scale = l.Scale;
        float maxWidth = 140f * scale;
        // The tooltip's box follows its text, so its size is the canvas's own (UiLayout.TextScale).
        // The same size wraps, measures and draws: one of them taken elsewhere is a tooltip whose
        // lines are broken for a glyph size it is not drawn at.
        UiSizedText t = UiText.AtCanvasScale(c);
        TooltipContent content = BuildContent(c, st, item, maxWidth, in t);
        if (content.Lines.Count == 0)
        {
            return;
        }

        float pitch = LinePitch(c, scale);
        float widest = 0f;
        for (int n = 0; n < content.Lines.Count; n++)
        {
            widest = MathF.Max(widest, UiText.Measure(c, content.Lines[n].Text, in t));
        }

        float padX = 3.3f * scale;
        float w = widest + (2f * padX);
        float h = (6.8f * scale) + (content.Lines.Count * pitch) + (pitch * 0.5f) + (9f * scale);
        Vector2 pos = PlaceTooltip(st.HoverRect, w, h, scale, l.ViewportWidth, l.ViewportHeight);
        UiRect panel = new(pos.X, pos.Y, w, h);
        if (s.TryGet(UiSpriteNames.TipPanel, out UiSprite tip))
        {
            c.NineSlice(tip, panel, White);
        }
        else
        {
            c.Solid(panel, new UiColor(0, 0, 0, 204));
        }

        float y = panel.Y + (6.8f * scale);
        ItemTier tier = ItemTierStyle.TierOf(item.Rarity);
        for (int n = 0; n < content.Lines.Count; n++)
        {
            (string text, TooltipContent.LineKind kind) = content.Lines[n];
            UiColor color = kind switch
            {
                TooltipContent.LineKind.Name or TooltipContent.LineKind.Tier => ItemTierStyle.ColorOf(tier),
                TooltipContent.LineKind.Bonus => ItemTierStyle.Bonus,
                TooltipContent.LineKind.Description or TooltipContent.LineKind.Count => ItemTierStyle.Dim,
                _ => ItemTierStyle.Body
            };
            float x = panel.X + padX + MathF.Max(0f, (widest - UiText.Measure(c, text, in t)) * 0.5f);
            UiText.Put(c, text, new Vector2(x, y), in t, color, UiText.ShadowPx(in l));
            y += pitch;
            // The gap after the name block, which may be two lines.
            if (kind == TooltipContent.LineKind.Name
                && (n + 1 >= content.Lines.Count || content.Lines[n + 1].Kind != TooltipContent.LineKind.Name))
            {
                y += pitch * 0.5f;
            }
        }
    }

    // The 3 px leading was a hairline under 38 px glyphs, so it follows the scale like everything else.
    internal static float LinePitch(IUiCanvas c, float uiScale) =>
        MathF.Max(9f * uiScale, InventoryWindow.BodyHeight(c) + (3f * uiScale));

    private static TooltipContent BuildContent(
        IUiCanvas c, InventoryUiState st, in InventoryItemSnapshot item, float maxWidth, in UiSizedText t)
    {
        TooltipContent? cached = st.Tooltip;
        if (cached is not null && cached.Matches(item, maxWidth, t.Px))
        {
            return cached;
        }

        TooltipContent content = cached ?? new TooltipContent();
        st.Tooltip = content;
        content.ItemInstanceId = item.ItemInstanceId;
        content.DisplayName = item.DisplayName;
        content.Description = item.Description;
        content.DetailLines = item.DetailLines;
        content.Modifiers = item.Modifiers;
        content.StackCount = item.StackCount;
        content.Rarity = item.Rarity;
        content.WrapWidth = maxWidth;
        content.TextPx = t.Px;
        content.Lines.Clear();

        // The name wraps by width like the text under it (two lines at most); it is never cut mid-line.
        if (string.IsNullOrEmpty(item.DisplayName))
        {
            content.Lines.Add(("(unnamed)", TooltipContent.LineKind.Name));
        }
        else
        {
            Wrap(c, item.DisplayName, maxWidth, content.Lines, TooltipContent.LineKind.Name, 2, in t);
        }

        Wrap(c, item.Description, maxWidth, content.Lines, TooltipContent.LineKind.Description, MaxWrappedLines, in t);
        if (item.DetailLines is not null)
        {
            int added = 0;
            foreach (string line in item.DetailLines)
            {
                if (added++ >= MaxDetailLines)
                {
                    break;
                }

                Wrap(c, line, maxWidth, content.Lines, TooltipContent.LineKind.Detail, 2, in t);
            }
        }

        List<string> modifierLines = CharacterPanelModel.TooltipLines(item);
        for (int n = 0; n < modifierLines.Count; n++)
        {
            content.Lines.Add((modifierLines[n], n == 0 ? TooltipContent.LineKind.Tier : TooltipContent.LineKind.Bonus));
        }

        if (item.StackCount > 1)
        {
            content.Lines.Add(($"Count: {item.StackCount}", TooltipContent.LineKind.Count));
        }

        return content;
    }

    // Greedy word wrap on the canvas's own measure; a word wider than the box is cut by character.
    // Hostile text is cut to MaxTextChars and a line count.
    internal static void Wrap(
        IUiCanvas c, string? text, float maxWidth, List<(string Text, TooltipContent.LineKind Kind)> output,
        TooltipContent.LineKind kind, int maxLines, in UiSizedText t)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (text.Length > MaxTextChars)
        {
            text = text[..MaxTextChars];
        }

        int lines = 0;
        int start = 0;
        while (start < text.Length && lines < maxLines)
        {
            while (start < text.Length && (text[start] == ' ' || text[start] == '\n' || text[start] == '\r'))
            {
                start++;
            }

            if (start >= text.Length)
            {
                break;
            }

            int end = start;
            int lastSpace = -1;
            for (int j = start; j < text.Length; j++)
            {
                char ch = text[j];
                if (ch == '\n' || ch == '\r')
                {
                    end = j;
                    break;
                }

                if (ch == ' ')
                {
                    lastSpace = j;
                }

                if (UiText.Measure(c, text.AsSpan(start, j - start + 1), in t) > maxWidth)
                {
                    end = lastSpace > start ? lastSpace : (j > start ? j : j + 1);
                    break;
                }

                end = j + 1;
            }

            output.Add((text.Substring(start, end - start).TrimEnd(), kind));
            lines++;
            start = end;
        }
    }

    // ---- ghost ------------------------------------------------------------------------------

    private static void DrawGhost(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, InventoryUiState st)
    {
        Vector2 p = i.Pointer.Position;
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
        {
            return;
        }

        HudItemView[] source = st.DragSource >= EquipmentSlots.FirstSlotIndex ? v.Worn : v.Backpack;
        int index = st.DragSource >= EquipmentSlots.FirstSlotIndex ? st.DragSource - EquipmentSlots.FirstSlotIndex : st.DragSource;
        if (!InventoryWindow.TryItem(source, index, out _) || !source[index].HasIcon)
        {
            return;
        }

        if (!InventoryWindow.TryDragFootprint(v, st.DragSource, out ItemFootprint footprint))
        {
            return;
        }

        // The ghost covers the item's whole footprint and is centred on the pointer; the drop lands where
        // the same rule puts it (InventoryWindow.TryDropAnchor).
        HudItemView hv = source[index];
        float width = footprint.Cols * InventoryWindow.CellUnits * l.Scale;
        float height = footprint.Rows * InventoryWindow.CellUnits * l.Scale;
        UiRect box = new(p.X - (width * 0.5f), p.Y - (height * 0.5f), width, height);
        c.Sprite(ItemTierStyle.IconFor(hv.Icon, hv.Tier), InventoryWindow.IconRect(hv.Icon, box, l.Scale), GhostTint);
    }

    // ---- context menu -----------------------------------------------------------------------

    private static void DrawMenu(
        IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, IHudActions a, InventoryUiState st)
    {
        List<CharacterMenuEntry> entries = st.MenuEntries;
        float scale = l.Scale;
        float padX = 6f * scale;
        float rowH = MathF.Max(12f * scale, InventoryWindow.BodyHeight(c) + (6f * scale));
        float widest = 0f;
        for (int n = 0; n < entries.Count; n++)
        {
            widest = MathF.Max(widest, c.MeasureText(entries[n].Label, UiTextStyle.Body));
        }

        float edge = 2f * scale;
        float w = widest + (2f * padX) + (2f * edge);
        float h = (entries.Count * rowH) + (2f * edge);
        // The menu opens to the right of the pointer, clear of the gauntlet cursor, which would
        // otherwise sit on the first row's text.
        float x = MathF.Max(0f, MathF.Min(st.MenuPosition.X + (MenuCursorGapUnits * scale), l.ViewportWidth - w));
        float y = MathF.Max(0f, MathF.Min(st.MenuPosition.Y, l.ViewportHeight - h));
        UiRect panel = new(x, y, w, h);

        bool fresh = st.ModalFresh;
        st.ModalFresh = false;
        HoldModal(i, st);
        UiRect screen = new(0f, 0f, l.ViewportWidth, l.ViewportHeight);
        bool backdropClicked = !fresh && (i.Button(InventoryWindow.IdMenuBackdrop, screen) || i.RightClicked(InventoryWindow.IdMenuBackdrop, screen));

        if (s.TryGet(UiSpriteNames.TipPanel, out UiSprite tip))
        {
            c.NineSlice(tip, panel, White);
        }
        else
        {
            c.Solid(panel, new UiColor(0, 0, 0, 220));
        }

        int picked = -1;
        st.MenuRowRects.Clear();
        for (int n = 0; n < entries.Count; n++)
        {
            UiRect row = new(x + edge, y + edge + (n * rowH), w - (2f * edge), rowH);
            st.MenuRowRects.Add(row);
            int id = InventoryWindow.IdMenuEntry + n;
            if (i.Hovered(id, row))
            {
                c.Solid(row, RowHover);
            }

            CharacterMenuEntry entry = entries[n];
            UiColor color = entry.RequiresConfirm ? ItemTierStyle.Danger : ItemTierStyle.Body;
            c.Text(entry.Label, new Vector2(row.X + padX, row.Y + MathF.Floor((rowH - InventoryWindow.BodyHeight(c)) * 0.5f)), UiTextStyle.Body, color);
            if (i.Button(id, row) && picked < 0 && !fresh)
            {
                picked = n;
            }
        }

        if (picked >= 0)
        {
            CharacterMenuEntry entry = entries[picked];
            string name = string.Empty;
            if (entry.RequiresConfirm)
            {
                name = InventoryWindow.TryItem(v.Backpack, entry.Action.SlotIndex, out InventoryItemSnapshot item) ? item.DisplayName ?? string.Empty : string.Empty;
                st.OpenConfirm(entry.Action, name);
            }
            else
            {
                st.CloseMenu();
                a.Inventory(entry.Action);
            }
        }
        else if (backdropClicked)
        {
            st.CloseMenu();
        }
    }

    // ---- destroy confirm --------------------------------------------------------------------

    private static void DrawConfirm(IUiCanvas c, IUiInput i, in UiLayout l, UiSpriteTable s, IHudActions a, InventoryUiState st)
    {
        float scale = l.Scale;
        float pad = 8f * scale;
        float pitch = LinePitch(c, scale);

        Span<char> mem = stackalloc char[96];
        UiTextBuilder b = new(mem);
        b.Append("Destroy ");
        b.Append(st.ConfirmName.AsSpan(0, Math.Min(st.ConfirmName.Length, 40)));
        b.Append("?");
        ReadOnlySpan<char> line1 = b.Text;
        ReadOnlySpan<char> line2 = "This cannot be undone.";

        float btnW = 44f * scale;
        float btnH = 32f * scale;
        float gap = 6f * scale;
        float contentW = MathF.Max(MathF.Max(c.MeasureText(line1, UiTextStyle.Body), c.MeasureText(line2, UiTextStyle.Body)), (2f * btnW) + gap);
        float w = contentW + (2f * pad);
        float h = (2f * pad) + (2f * pitch) + gap + btnH;
        float x = MathF.Round((l.ViewportWidth - w) * 0.5f);
        float y = MathF.Round((l.ViewportHeight - h) * 0.5f);
        UiRect panel = new(x, y, w, h);
        UiRect screen = new(0f, 0f, l.ViewportWidth, l.ViewportHeight);

        bool fresh = st.ModalFresh;
        st.ModalFresh = false;
        HoldModal(i, st);
        bool outside = !fresh && i.Button(InventoryWindow.IdConfirmBackdrop, screen);
        c.Solid(screen, Scrim);
        if (s.TryGet(UiSpriteNames.TipPanel, out UiSprite tip))
        {
            c.NineSlice(tip, panel, White);
        }
        else
        {
            c.Solid(panel, new UiColor(0, 0, 0, 230));
        }

        float t1 = x + ((w - c.MeasureText(line1, UiTextStyle.Body)) * 0.5f);
        float t2 = x + ((w - c.MeasureText(line2, UiTextStyle.Body)) * 0.5f);
        c.Text(line1, new Vector2(t1, y + pad), UiTextStyle.Body, ItemTierStyle.Title);
        c.Text(line2, new Vector2(t2, y + pad + pitch), UiTextStyle.Body, ItemTierStyle.Dim);

        float rowY = y + h - pad - btnH;
        float rowX = x + ((w - ((2f * btnW) + gap)) * 0.5f);
        UiRect destroy = new(rowX, rowY, btnW, btnH);
        UiRect cancel = new(rowX + btnW + gap, rowY, btnW, btnH);
        st.ConfirmDestroyRect = destroy;
        st.ConfirmCancelRect = cancel;
        bool destroyClicked = DrawDialogButton(c, i, s, InventoryWindow.IdConfirmDestroy, destroy, "Destroy", ItemTierStyle.Danger) && !fresh;
        bool cancelClicked = DrawDialogButton(c, i, s, InventoryWindow.IdConfirmCancel, cancel, "Cancel", ItemTierStyle.Body) && !fresh;

        if (destroyClicked && st.ConfirmAction.ItemInstanceId != 0)
        {
            InventoryHudAction action = st.ConfirmAction;
            st.CloseConfirm();
            a.Inventory(action);
        }
        else if (cancelClicked || outside)
        {
            st.CloseConfirm();
        }
    }

    private static bool DrawDialogButton(IUiCanvas c, IUiInput i, UiSpriteTable s, int id, UiRect r, string label, UiColor color)
    {
        bool hovered = i.Hovered(id, r);
        if (s.TryGet(hovered ? UiSpriteNames.Plate44Hover : UiSpriteNames.Plate44Normal, out UiSprite plate))
        {
            c.Sprite(plate, r, White);
        }
        else
        {
            c.Solid(r, hovered ? new UiColor(90, 90, 96, 255) : new UiColor(64, 64, 70, 255));
        }

        InventoryWindow.DrawTextCentered(c, r, label, color);
        return i.Button(id, r);
    }
}
