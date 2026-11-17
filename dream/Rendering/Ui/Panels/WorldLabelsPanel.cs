using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// Actor name and level labels over the world: text only, no HP-bar art yet. Screen is the anchor
// (top centre of the label). Strings come from the server, so they are passed as spans and clipped.
public static class WorldLabelsPanel
{
    public const float MaxWidthUnits = 100f;

    // Labels draw first, under every other panel, so they must never use up the batch: at most this
    // many, and none once less than ReserveQuads is left for the bar, windows, notices and numbers.
    public const int MaxLabels = 24;
    public const int ReserveQuads = 4096;

    private static readonly UiColor NameColor = new(0xF8, 0xF4, 0xE8, 255);
    private static readonly UiColor HealthColor = new(0xF5, 0xDD, 0xA5, 255);

    // Widget ids of the clickable labels (a label is IdLabel + its place in the list).
    internal const int IdLabel = InventoryWindow.IdBase + 0x700;

    private static readonly UiColor HoverFill = new(0xF5, 0xDD, 0xA5, 46);

    // Without input every label is plain text.
    public static void Build(IUiCanvas c, HudView v, in UiLayout l) => Build(c, null, v, l, null);

    // A label with a click (ground item, NPC) is also a button: a click sends its pick to the actions.
    public static void Build(IUiCanvas c, IUiInput? i, HudView v, in UiLayout l, IHudActions? a)
    {
        bool live = i is not null && a is not null && !InventoryUiState.Current.ModalOpen;
        // Labels are built first, under the bar and the windows, and neither a window frame nor the
        // bar's art registers a widget of its own everywhere it covers, so a click on an opaque but
        // widget-less part of the HUD would reach a label behind it. While the pointer is over the HUD
        // the labels take no hover or click. This is the same rule the click-to-move gate uses, so a
        // point is either world or HUD for both: the status bar counts whether the inventory is open or
        // not (with it closed the satchel and the quickbar tiles register nothing, so a ground item or
        // an NPC projecting into the bottom strip was otherwise clickable through the bar's art).
        if (live && ClassicHudHitTest.Over(in l, v.InventoryOpen, i!.Pointer.Position))
        {
            live = false;
        }

        IReadOnlyList<HudWorldLabel> labels = v.Labels;
        float maxWidth = MaxWidthUnits * l.Scale;
        float lineH = PanelText.Height(c, UiTextStyle.Body);
        // The leading between the two lines, the halo and the hit box padding were a baked 1 or 2 px:
        // at ui_scale 4 the halo under 38 px glyphs read as a scratch and the lines sat on each other.
        float hair = UiText.ShadowPx(in l);
        Span<UiRect> placed = stackalloc UiRect[MaxLabels];
        int placedCount = 0;
        for (int k = 0; k < labels.Count && placedCount < MaxLabels; k++)
        {
            if (c.QuadHeadroom < ReserveQuads)
            {
                break;
            }

            HudWorldLabel label = labels[k];
            float x = label.Screen.X;
            float y = label.Screen.Y;
            if (!float.IsFinite(x) || !float.IsFinite(y) ||
                x < -maxWidth || x > l.ViewportWidth + maxWidth || y < -lineH * 2f || y > l.ViewportHeight)
            {
                continue;
            }

            ReadOnlySpan<char> name = string.IsNullOrEmpty(label.Name) ? default : label.Name.AsSpan();
            name = name[..PanelText.FitLength(c, name, UiTextStyle.Body, maxWidth)];
            ReadOnlySpan<char> health = string.IsNullOrEmpty(label.Health) ? default : label.Health.AsSpan();
            health = health[..PanelText.FitLength(c, health, UiTextStyle.Body, maxWidth)];
            float width = MathF.Max(c.MeasureText(name, UiTextStyle.Body), c.MeasureText(health, UiTextStyle.Body));
            float height = (name.IsEmpty ? 0f : lineH + hair) + (health.IsEmpty ? 0f : lineH);
            if (height <= 0f)
            {
                continue;
            }

            // Two enemies standing one behind the other project to nearly the same spot: a label that
            // lands on an earlier one moves down below it (at most once per placed label).
            UiRect box = new(x - (width * 0.5f), y, width, height);
            for (int pass = 0; pass < placedCount; pass++)
            {
                bool moved = false;
                for (int p = 0; p < placedCount; p++)
                {
                    UiRect o = placed[p];
                    if (box.X < o.Right && o.X < box.Right && box.Y < o.Bottom && o.Y < box.Bottom)
                    {
                        box = box with { Y = o.Bottom + hair };
                        moved = true;
                    }
                }

                if (!moved)
                {
                    break;
                }
            }

            placed[placedCount++] = box;
            y = box.Y;
            UiColor nameColor = NameColor;
            if (label.Click.Kind != HudLabelClickKind.None)
            {
                nameColor = label.Dim
                    ? ItemTierStyle.Dim
                    : label.Click.Kind == HudLabelClickKind.PickUpGround ? ItemTierStyle.ColorOf(label.Tier) : NameColor;
                if (live)
                {
                    UiRect hit = new(box.X - (2f * hair), box.Y - hair, box.W + (4f * hair), box.H + (2f * hair));
                    int id = IdLabel + placedCount;
                    if (i!.Hovered(id, hit))
                    {
                        c.Solid(hit, HoverFill);
                    }

                    if (i.Button(id, hit))
                    {
                        if (label.Click.Kind == HudLabelClickKind.PickUpGround)
                        {
                            a!.PickUpGround(label.Click.Id);
                        }
                        else if (label.Click.Id is >= int.MinValue and <= int.MaxValue)
                        {
                            a!.InteractNpc((int)label.Click.Id);
                        }
                    }
                }
            }

            if (!name.IsEmpty)
            {
                PanelText.HaloCentred(c, name, x, y, UiTextStyle.Body, nameColor, hair);
                y += lineH + hair;
            }

            if (!health.IsEmpty)
            {
                PanelText.HaloCentred(c, health, x, y, UiTextStyle.Body, HealthColor, hair);
            }
        }
    }
}
