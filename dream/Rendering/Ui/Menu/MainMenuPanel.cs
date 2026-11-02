namespace Dream.Rendering.Ui.Menu;

// The title screen: backdrop, the word DREAM, five buttons and a status line. Also the credits screen.
public static class MainMenuPanel
{
    public const int FirstId = 0x5000;
    public const string SinglePlayerLabel = "Single Player";
    public const string JoinServerLabel = "Join Server";
    public const string OptionsLabel = "Options";
    public const string CreditsLabel = "Credits";
    public const string ExitLabel = "Exit";
    public const float TitleTopUnits = 40f;
    public const float ButtonsTopUnits = 160f;

    public static readonly string[] CreditLines =
    [
        "DREAM",
        "",
        "A grim little world of ash and iron.",
        "Written, drawn and built from scratch:",
        "every sprite, sound and line of code is original.",
        "",
        "Made with .NET and OpenTK.",
        "",
        "Thank you for playing."
    ];

    // busy: a game is starting, the buttons are disabled. status (may be null) shows under the buttons;
    // statusIsError colours it as a failure.
    public static MenuAction Build(MenuWidgets w, bool busy, string? status, bool statusIsError)
    {
        w.DrawBackdrop();
        w.DrawTitle(TitleTopUnits);

        MenuAction result = MenuAction.None;
        (string Label, MenuAction Action)[] buttons =
        [
            (SinglePlayerLabel, MenuAction.SinglePlayer),
            (JoinServerLabel, MenuAction.JoinServer),
            (OptionsLabel, MenuAction.Options),
            (CreditsLabel, MenuAction.Credits),
            (ExitLabel, MenuAction.Exit)
        ];
        for (int i = 0; i < buttons.Length; i++)
        {
            if (w.Button(FirstId + i, w.ButtonRect(ButtonsTopUnits, i), buttons[i].Label, enabled: !busy))
            {
                result = buttons[i].Action;
            }
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            float y = w.OriginY + (ButtonsTopUnits + buttons.Length * MenuWidgets.ButtonPitchUnits + 8f) * w.Scale;
            UiTextStyle style = w.FitStyle(status, w.ViewportWidth * 0.9f);
            w.TextCentred(status, w.CentreX, y, style, statusIsError ? MenuWidgets.Error : MenuWidgets.Muted);
        }

        return result;
    }

    public static MenuAction BuildCredits(MenuWidgets w)
    {
        w.DrawBackdrop();
        w.Dim(120);
        float heightUnits = 40f + CreditLines.Length * 16f + 56f;
        UiRect panel = w.Centered(MathF.Max(8f, (MenuWidgets.DesignHeightUnits - heightUnits) * 0.5f), 300f, heightUnits);
        w.DrawPanel(panel);

        float lineH = 16f * w.Scale;
        float y = panel.Y + 22f * w.Scale;
        for (int i = 0; i < CreditLines.Length; i++)
        {
            string line = CreditLines[i];
            if (line.Length > 0)
            {
                bool heading = i == 0;
                UiTextStyle style = heading ? w.FitStyle(line, panel.W, true) : UiTextStyle.Body;
                w.TextCentred(line, w.CentreX, y, style, heading ? MenuWidgets.Gold : MenuWidgets.Bone);
            }

            y += lineH;
        }

        UiRect back = w.Inside(panel, (300f - MenuWidgets.ButtonWidthUnits) * 0.5f, heightUnits - 34f, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits);
        return w.Button(FirstId + 20, back, "Back") ? MenuAction.Back : MenuAction.None;
    }
}
