namespace Dream.Rendering.Ui.Menu;

// The Options screen: one line per MenuOptionRow, then Camera, Key Bindings and Back. Used by the main menu
// and the pause menu. backdrop is true on the title screen (the pause menu has the game behind it).
public static class OptionsPanel
{
    public const int FirstId = 0x5200;
    public const float PanelWidthUnits = 300f;
    public const float RowHeightUnits = 24f;
    public const float LabelWidthUnits = 120f;
    public const float TrackWidthUnits = 82f;
    public const string KeyBindingsLabel = "Key Bindings";
    public const string CameraLabel = "Camera";
    public const string BackLabel = "Back";

    // The panel's height in design units for a row count and a footer button count. The Options screen must
    // fit MenuWidgets.DesignHeightUnits, so a screen with many rows moves them to a sub-screen.
    public static float HeightUnits(int rowCount, int buttonCount) =>
        50f + rowCount * RowHeightUnits + 8f + buttonCount * MenuWidgets.ButtonPitchUnits + 8f;

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

        float heightUnits = HeightUnits(rows.Count, 3);
        float top = MathF.Max(6f, (MenuWidgets.DesignHeightUnits - heightUnits) * 0.5f);
        UiRect panel = w.Centered(top, PanelWidthUnits, heightUnits);
        w.DrawPanel(panel);

        UiSizedText titleStyle = w.FitText("OPTIONS", panel.W, MenuWidgets.PanelTitleUnits);
        w.TextCentred("OPTIONS", w.CentreX, panel.Y + 12f * w.Scale, in titleStyle, MenuWidgets.Gold);
        w.DrawDivider(w.Inside(panel, 30f, 34f, PanelWidthUnits - 60f, 6f));

        float rowsTop = 46f;
        BuildRows(w, rows, panel, FirstId + 4);

        float buttonsTop = rowsTop + rows.Count * RowHeightUnits + 8f;
        MenuAction result = MenuAction.None;
        UiRect camera = w.Inside(panel, (PanelWidthUnits - MenuWidgets.ButtonWidthUnits) * 0.5f, buttonsTop + MenuWidgets.ButtonPitchUnits, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits);
        if (w.Button(FirstId + 2, camera, CameraLabel))
        {
            result = MenuAction.Camera;
        }

        UiRect keys = w.Inside(panel, (PanelWidthUnits - MenuWidgets.ButtonWidthUnits) * 0.5f, buttonsTop, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits);
        if (w.Button(FirstId, keys, KeyBindingsLabel))
        {
            result = MenuAction.KeyBindings;
        }

        UiRect back = w.Inside(panel, (PanelWidthUnits - MenuWidgets.ButtonWidthUnits) * 0.5f, buttonsTop + 2f * MenuWidgets.ButtonPitchUnits, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits);
        if (w.Button(FirstId + 1, back, BackLabel))
        {
            result = MenuAction.Back;
        }

        return result;
    }

    // The rows of a panel, one RowHeightUnits apart from 46 units down; each uses four widget ids from firstId.
    internal static void BuildRows(MenuWidgets w, IReadOnlyList<MenuOptionRow> rows, UiRect panel, int firstId)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            UiRect row = w.Inside(panel, 14f, 46f + i * RowHeightUnits, PanelWidthUnits - 28f, RowHeightUnits);
            BuildRow(w, rows[i], firstId + i * 4, row);
        }
    }

    private static void BuildRow(MenuWidgets w, MenuOptionRow row, int id, UiRect rect)
    {
        if (row.Kind == MenuOptionKind.Header)
        {
            // The same size and colour as the row labels, so a section header does not shrink or turn orange.
            UiSizedText headerStyle = w.FitTextInBox(row.Label, LabelWidthUnits * w.Scale, MenuWidgets.OptionRowUnits, RowHeightUnits);
            w.TextLeftCentred(row.Label, rect, rect.X, in headerStyle, MenuWidgets.Bone);
            return;
        }

        bool enabled = row.Enabled;
        float labelWidth = LabelWidthUnits * w.Scale;
        UiSizedText style = w.FitTextInBox(row.Label, labelWidth, MenuWidgets.OptionRowUnits, RowHeightUnits);
        w.TextLeftCentred(row.Label, rect, rect.X, in style, enabled ? MenuWidgets.Bone : MenuWidgets.Disabled);

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
        UiSizedText valueStyle = w.FitTextInBox(valueText, rect.Right - valueX, MenuWidgets.OptionRowUnits, RowHeightUnits);
        w.TextLeftCentred(valueText, rect, valueX, in valueStyle, enabled ? MenuWidgets.Muted : MenuWidgets.Disabled);
    }
}
