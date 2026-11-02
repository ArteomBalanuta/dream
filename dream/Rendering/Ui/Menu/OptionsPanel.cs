namespace Dream.Rendering.Ui.Menu;

// The Options screen: one line per MenuOptionRow, then Key Bindings and Back. Used by the main menu
// and the pause menu. backdrop is true on the title screen (the pause menu has the game behind it).
public static class OptionsPanel
{
    public const int FirstId = 0x5200;
    public const float PanelWidthUnits = 300f;
    public const float RowHeightUnits = 24f;
    public const float LabelWidthUnits = 120f;
    public const float TrackWidthUnits = 82f;
    public const string KeyBindingsLabel = "Key Bindings";
    public const string BackLabel = "Back";

    public static MenuAction Build(MenuWidgets w, IReadOnlyList<MenuOptionRow> rows, bool backdrop)
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

        float heightUnits = 50f + rows.Count * RowHeightUnits + 8f + 2f * MenuWidgets.ButtonPitchUnits + 8f;
        float top = MathF.Max(6f, (MenuWidgets.DesignHeightUnits - heightUnits) * 0.5f);
        UiRect panel = w.Centered(top, PanelWidthUnits, heightUnits);
        w.DrawPanel(panel);

        UiTextStyle titleStyle = w.FitStyle("OPTIONS", panel.W);
        w.TextCentred("OPTIONS", w.CentreX, panel.Y + 12f * w.Scale, titleStyle, MenuWidgets.Gold);
        w.DrawDivider(w.Inside(panel, 30f, 34f, PanelWidthUnits - 60f, 6f));

        float rowsTop = 46f;
        for (int i = 0; i < rows.Count; i++)
        {
            UiRect row = w.Inside(panel, 14f, rowsTop + i * RowHeightUnits, PanelWidthUnits - 28f, RowHeightUnits);
            BuildRow(w, rows[i], FirstId + 4 + i * 4, row);
        }

        float buttonsTop = rowsTop + rows.Count * RowHeightUnits + 8f;
        MenuAction result = MenuAction.None;
        UiRect keys = w.Inside(panel, (PanelWidthUnits - MenuWidgets.ButtonWidthUnits) * 0.5f, buttonsTop, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits);
        if (w.Button(FirstId, keys, KeyBindingsLabel))
        {
            result = MenuAction.KeyBindings;
        }

        UiRect back = w.Inside(panel, (PanelWidthUnits - MenuWidgets.ButtonWidthUnits) * 0.5f, buttonsTop + MenuWidgets.ButtonPitchUnits, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits);
        if (w.Button(FirstId + 1, back, BackLabel))
        {
            result = MenuAction.Back;
        }

        return result;
    }

    private static void BuildRow(MenuWidgets w, MenuOptionRow row, int id, UiRect rect)
    {
        if (row.Kind == MenuOptionKind.Header)
        {
            UiTextStyle headerStyle = w.FitStyle(row.Label, rect.W, allowLarge: false);
            w.TextCentred(row.Label, rect.X + rect.W * 0.5f, rect.Y + (rect.H - w.TextHeight(headerStyle)) * 0.5f, headerStyle, MenuWidgets.Gold);
            return;
        }

        bool enabled = row.Enabled;
        float labelWidth = LabelWidthUnits * w.Scale;
        UiTextStyle style = w.FitStyle(row.Label, labelWidth);
        w.TextLeftCentred(row.Label, rect, rect.X, style, enabled ? MenuWidgets.Bone : MenuWidgets.Disabled);

        float controlX = LabelWidthUnits + 6f;
        if (row.Kind == MenuOptionKind.Toggle)
        {
            bool on = row.GetToggle!();
            UiRect button = w.Inside(rect, controlX, 1f, 56f, RowHeightUnits - 2f);
            if (w.Button(id, button, on ? "On" : "Off", enabled))
            {
                row.SetToggle!(!on);
                row.Commit?.Invoke();
            }

            return;
        }

        float current = row.GetValue!();
        UiRect track = w.Inside(rect, controlX, (RowHeightUnits - 6f) * 0.5f, TrackWidthUnits, 6f);
        UiRect hit = w.Inside(rect, controlX - 2f, 0f, TrackWidthUnits + 4f, RowHeightUnits);
        MenuSliderResult result = w.Slider(id, track, hit, row.ToUnit(current), enabled);
        float shown = current;
        if (result.Changed)
        {
            float next = row.FromUnit(result.Unit);
            shown = next;
            if (next != current)
            {
                row.SetValue!(next);
            }
        }

        if (result.Committed)
        {
            row.Commit?.Invoke();
        }

        string valueText = row.FormatValue(shown);
        float valueX = rect.X + (controlX + TrackWidthUnits + 6f) * w.Scale;
        UiTextStyle valueStyle = w.FitStyle(valueText, rect.Right - valueX);
        w.TextLeftCentred(valueText, rect, valueX, valueStyle, enabled ? MenuWidgets.Muted : MenuWidgets.Disabled);
    }
}
