namespace Dream.Rendering.Ui.Menu;

// The loading screen: the title screen's backdrop and title, a panel with the stage (or, when ready,
// the pulsing prompt) and a progress bar, and a tip underneath. Drawn by the title menu while a game
// starts and over the world's first frames until the player presses a key.
public static class LoadingScreenPanel
{
    public const string PromptText = "Press any key to continue...";

    public const float PanelTopUnits = 250f;
    public const float PanelWidthUnits = 300f;
    public const float PanelHeightUnits = 86f;

    // 6-10 grim one-liners, original text.
    public static readonly string[] Tips =
    [
        "The dark is patient. Keep a fire lit.",
        "Wolves remember every wound you leave behind.",
        "Ash keeps no footprints, but the hunters follow anyway.",
        "Iron dulls faster than courage. Carry a whetstone.",
        "Nothing out there is as hungry as the night itself.",
        "A cold camp is a grave with a roof of stars.",
        "Trust the torch, not the silence.",
        "The old roads were paved with those who rushed.",
        "Every flame you leave behind is a promise to return.",
        "Rest while the embers glow. They do not glow long."
    ];

    // Non-negative modulo over Tips; never throws.
    public static string PickTip(int seed)
    {
        int n = Tips.Length;
        return Tips[((seed % n) + n) % n];
    }

    public static void Build(MenuWidgets w, in LoadingScreenState state)
    {
        w.DrawBackdrop();
        w.DrawTitle(MainMenuPanel.TitleTopUnits);
        w.Dim(90);

        UiRect panel = w.Centered(PanelTopUnits, PanelWidthUnits, PanelHeightUnits);
        w.DrawPanel(panel);

        float progress = float.IsNaN(state.Progress) ? 0f : Math.Clamp(state.Progress, 0f, 1f);
        if (state.Ready)
        {
            progress = 1f;
        }

        string text = state.Ready ? PromptText : state.Stage ?? string.Empty;
        float textBox = panel.W * 0.86f;
        UiSizedText style = w.FitTextInBox(text, textBox, MenuWidgets.StatusUnits, 16f);
        UiColor color = MenuWidgets.Bone;
        if (state.Ready)
        {
            float pulse = 0.775f + 0.225f * MathF.Sin(float.IsFinite(state.Seconds) ? state.Seconds * 4f : 0f);
            color = new UiColor(MenuWidgets.Gold.R, MenuWidgets.Gold.G, MenuWidgets.Gold.B, (byte)Math.Clamp((int)MathF.Round(pulse * 255f), 0, 255));
        }

        if (text.Length > 0)
        {
            w.TextCentred(text, panel.X + panel.W * 0.5f, panel.Y + 22f * w.Scale, in style, color);
        }

        UiRect track = w.Inside(panel, 24f, 52f, PanelWidthUnits - 48f, 10f);
        w.ProgressBar(track, progress);

        if (!string.IsNullOrWhiteSpace(state.Tip))
        {
            UiSizedText tip = w.FitText(state.Tip, w.ViewportWidth * 0.9f, MenuWidgets.StatusUnits);
            w.TextCentred(state.Tip, w.CentreX, panel.Y + panel.H + 14f * w.Scale, in tip, MenuWidgets.Muted);
        }
    }
}
