namespace Dream.Rendering.Ui.Menu;

// A read-only list of the controls. The bindings are fixed in the game, so this is a static table.
public static class KeyBindingsPanel
{
    public const int FirstId = 0x5300;
    public const float PanelWidthUnits = 300f;
    public const float RowHeightUnits = 17f;
    public const float EffectColumnUnits = 92f;

    public static readonly (string Input, string Effect)[] Entries =
    [
        ("Left click", "Move / attack"),
        ("Right click", "Combat / build"),
        ("Space", "Jump"),
        ("Q / E", "Use potions"),
        ("V", "Inventory and character"),
        ("1 / 2", "Combat / build mode"),
        ("Esc", "Menu"),
        ("F3", "Admin HUD (when enabled)")
    ];

    public static MenuAction Build(MenuWidgets w, bool backdrop)
    {
        if (backdrop)
        {
            w.DrawBackdrop();
            w.Dim(110);
        }
        else
        {
            w.Dim(170);
        }

        float heightUnits = 50f + Entries.Length * RowHeightUnits + 12f + MenuWidgets.ButtonPitchUnits + 8f;
        float top = MathF.Max(6f, (MenuWidgets.DesignHeightUnits - heightUnits) * 0.5f);
        UiRect panel = w.Centered(top, PanelWidthUnits, heightUnits);
        w.DrawPanel(panel);

        UiSizedText titleStyle = w.FitText("KEY BINDINGS", panel.W, MenuWidgets.PanelTitleUnits);
        w.TextCentred("KEY BINDINGS", w.CentreX, panel.Y + 12f * w.Scale, in titleStyle, MenuWidgets.Gold);
        w.DrawDivider(w.Inside(panel, 30f, 34f, PanelWidthUnits - 60f, 6f));

        for (int i = 0; i < Entries.Length; i++)
        {
            UiRect row = w.Inside(panel, 18f, 46f + i * RowHeightUnits, PanelWidthUnits - 36f, RowHeightUnits);
            float effectX = row.X + EffectColumnUnits * w.Scale;
            UiSizedText input = w.FitTextInBox(Entries[i].Input, (EffectColumnUnits - 4f) * w.Scale, MenuWidgets.OptionRowUnits, RowHeightUnits);
            UiSizedText effect = w.FitTextInBox(Entries[i].Effect, row.Right - effectX, MenuWidgets.OptionRowUnits, RowHeightUnits);
            w.TextLeftCentred(Entries[i].Input, row, row.X, in input, MenuWidgets.Gold);
            w.TextLeftCentred(Entries[i].Effect, row, effectX, in effect, MenuWidgets.Bone);
        }

        UiRect back = w.Inside(
            panel,
            (PanelWidthUnits - MenuWidgets.ButtonWidthUnits) * 0.5f,
            heightUnits - MenuWidgets.ButtonHeightUnits - 10f,
            MenuWidgets.ButtonWidthUnits,
            MenuWidgets.ButtonHeightUnits);
        return w.Button(FirstId, back, OptionsPanel.BackLabel) ? MenuAction.Back : MenuAction.None;
    }
}
