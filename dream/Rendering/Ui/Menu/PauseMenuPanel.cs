namespace Dream.Rendering.Ui.Menu;

// The Esc menu over the running game: the world dims behind a plate with the choices.
public static class PauseMenuPanel
{
    public const int FirstId = 0x5100;
    public const string ResumeLabel = "Resume";
    public const string OptionsLabel = "Options";
    public const string DeveloperLabel = "Developer Settings";
    public const string ExitToMenuLabel = "Exit to Main Menu";
    public const string ExitGameLabel = "Exit Game";
    public const float PanelWidthUnits = 190f;

    // showDeveloper: the admin HUD is available, so the ImGui developer window can be opened.
    public static MenuAction Build(MenuWidgets w, bool showDeveloper)
    {
        w.Dim(150);

        List<(string Label, MenuAction Action)> buttons =
        [
            (ResumeLabel, MenuAction.Resume),
            (OptionsLabel, MenuAction.Options)
        ];
        if (showDeveloper)
        {
            buttons.Add((DeveloperLabel, MenuAction.DeveloperSettings));
        }

        buttons.Add((ExitToMenuLabel, MenuAction.ExitToMainMenu));
        buttons.Add((ExitGameLabel, MenuAction.ExitGame));

        float heightUnits = 54f + buttons.Count * MenuWidgets.ButtonPitchUnits + 8f;
        float top = MathF.Max(8f, (MenuWidgets.DesignHeightUnits - heightUnits) * 0.5f);
        UiRect panel = w.Centered(top, PanelWidthUnits, heightUnits);
        w.DrawPanel(panel);

        UiTextStyle titleStyle = w.FitStyle("PAUSED", panel.W);
        w.TextCentred("PAUSED", w.CentreX, panel.Y + 14f * w.Scale, titleStyle, MenuWidgets.Gold);
        w.DrawDivider(w.Inside(panel, 24f, 38f, PanelWidthUnits - 48f, 6f));

        MenuAction result = MenuAction.None;
        for (int i = 0; i < buttons.Count; i++)
        {
            UiRect r = w.Centered(top + 52f + i * MenuWidgets.ButtonPitchUnits, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits);
            if (w.Button(FirstId + i, r, buttons[i].Label))
            {
                result = buttons[i].Action;
            }
        }

        return result;
    }
}
