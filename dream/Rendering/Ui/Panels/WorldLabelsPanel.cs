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

    public static void Build(IUiCanvas c, HudView v, in UiLayout l)
    {
        IReadOnlyList<HudWorldLabel> labels = v.Labels;
        float maxWidth = MaxWidthUnits * l.Scale;
        float lineH = PanelText.Height(c, UiTextStyle.Body);
        int shown = Math.Min(labels.Count, MaxLabels);
        Span<UiRect> placed = stackalloc UiRect[MaxLabels];
        int placedCount = 0;
        for (int k = 0; k < shown; k++)
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
            float height = (name.IsEmpty ? 0f : lineH + 1f) + (health.IsEmpty ? 0f : lineH);
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
                        box = box with { Y = o.Bottom + 1f };
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
            if (!name.IsEmpty)
            {
                PanelText.HaloCentred(c, name, x, y, UiTextStyle.Body, NameColor);
                y += lineH + 1f;
            }

            if (!health.IsEmpty)
            {
                PanelText.HaloCentred(c, health, x, y, UiTextStyle.Body, HealthColor);
            }
        }
    }
}
