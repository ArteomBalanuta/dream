namespace Dream.Rendering.Ui.Menu;

// A read-only list of the controls. The bindings are fixed in the game, so this is a static table.
//
// BH-INPUT-5: it is the only binding reference the game has, so a row naming a key the game no longer
// resolves to that action is a wrong instruction, not a stale comment. It listed "Q / E" for the
// potions (E has been interact / pick up since the economy pass, and the mana potion is R) and
// "1 / 2" for the modes (the quickbar took the digits; QuickbarInput.RemapModeKeys moved the modes
// onto B and G), and it named none of the quickbar, walk, sprint, chat or online keys. The rows below
// are the keys the resolvers really read; InputKeyBindingsTableTests pins each one against the
// resolver's own constant, so a remap that does not come back here fails the tests.
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
        ("Shift", "Sprint"),
        ("Z", "Walk"),
        ("Q / R", "Health / mana potion"),
        ("E", "Interact / pick up"),
        ("1 - 6", "Quickbar"),
        ("B / G", "Build / combat mode"),
        ("V", "Inventory and character"),
        ("Enter", "Chat"),
        ("Tab", "Who is online"),
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
