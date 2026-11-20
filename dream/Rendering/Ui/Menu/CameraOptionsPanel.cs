namespace Dream.Rendering.Ui.Menu;

// The Camera screen, reached from Options: the tactical camera's rows and Back. The rows moved off the Options
// screen because they pushed it past the design height, hiding the audio rows and the footer buttons.
public static class CameraOptionsPanel
{
    public const int FirstId = 0x5400;

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

        float heightUnits = OptionsPanel.HeightUnits(rows.Count, 1);
        float top = MathF.Max(6f, (MenuWidgets.DesignHeightUnits - heightUnits) * 0.5f);
        UiRect panel = w.Centered(top, OptionsPanel.PanelWidthUnits, heightUnits);
        w.DrawPanel(panel);

        UiSizedText titleStyle = w.FitText("CAMERA", panel.W, MenuWidgets.PanelTitleUnits);
        w.TextCentred("CAMERA", w.CentreX, panel.Y + 12f * w.Scale, in titleStyle, MenuWidgets.Gold);
        w.DrawDivider(w.Inside(panel, 30f, 34f, OptionsPanel.PanelWidthUnits - 60f, 6f));

        OptionsPanel.BuildRows(w, rows, panel, FirstId + 4);

        float buttonsTop = 46f + rows.Count * OptionsPanel.RowHeightUnits + 8f;
        UiRect back = w.Inside(
            panel,
            (OptionsPanel.PanelWidthUnits - MenuWidgets.ButtonWidthUnits) * 0.5f,
            buttonsTop,
            MenuWidgets.ButtonWidthUnits,
            MenuWidgets.ButtonHeightUnits);
        return w.Button(FirstId, back, OptionsPanel.BackLabel) ? MenuAction.Back : MenuAction.None;
    }
}
