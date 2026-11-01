namespace Dream.Rendering.Ui.Panels;

// Actor name and level labels over the world: text only, no HP-bar art yet. Screen is the anchor
// (top centre of the label). Strings come from the server, so they are passed as spans and clipped.
public static class WorldLabelsPanel
{
    public const float MaxWidthUnits = 100f;

    private static readonly UiColor NameColor = new(0xF8, 0xF4, 0xE8, 255);
    private static readonly UiColor HealthColor = new(0xF5, 0xDD, 0xA5, 255);

    public static void Build(IUiCanvas c, HudView v, in UiLayout l)
    {
        IReadOnlyList<HudWorldLabel> labels = v.Labels;
        float maxWidth = MaxWidthUnits * l.Scale;
        float lineH = PanelText.Height(c, UiTextStyle.Body);
        for (int k = 0; k < labels.Count; k++)
        {
            HudWorldLabel label = labels[k];
            float x = label.Screen.X;
            float y = label.Screen.Y;
            if (!float.IsFinite(x) || !float.IsFinite(y) ||
                x < -maxWidth || x > l.ViewportWidth + maxWidth || y < -lineH * 2f || y > l.ViewportHeight)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(label.Name))
            {
                ReadOnlySpan<char> name = label.Name;
                name = name[..PanelText.FitLength(c, name, UiTextStyle.Body, maxWidth)];
                PanelText.HaloCentred(c, name, x, y, UiTextStyle.Body, NameColor);
                y += lineH + 1f;
            }

            if (!string.IsNullOrEmpty(label.Health))
            {
                ReadOnlySpan<char> health = label.Health;
                health = health[..PanelText.FitLength(c, health, UiTextStyle.Body, maxWidth)];
                PanelText.HaloCentred(c, health, x, y, UiTextStyle.Body, HealthColor);
            }
        }
    }
}
