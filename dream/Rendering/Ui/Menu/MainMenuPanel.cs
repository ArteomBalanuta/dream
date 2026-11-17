namespace Dream.Rendering.Ui.Menu;

// The title screen: backdrop, the word DREAM, five buttons and a status line. Also the credits screen.
public static class MainMenuPanel
{
    public const int FirstId = 0x5000;
    public const string SinglePlayerLabel = "Single Player";
    public const string MultiplayerLabel = "Multiplayer";
    public const string JoinLabel = "Join";
    public const string HostLabel = "Host";
    public const string BackLabel = "Back";
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
            (MultiplayerLabel, MenuAction.Multiplayer),
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
            UiSizedText style = w.FitText(status, w.ViewportWidth * 0.9f, MenuWidgets.StatusUnits);
            w.TextCentred(status, w.CentreX, y, in style, statusIsError ? MenuWidgets.Error : MenuWidgets.Muted);
        }

        return result;
    }

    public const float MultiplayerPanelWidthUnits = 340f;
    private const float MpLabelWidthUnits = 100f;
    private const float MpRowUnits = 24f;
    private const float MpFieldXUnits = 14f + MpLabelWidthUnits + 6f;

    // The Multiplayer screen. Ids FirstId+40 .. FirstId+59. Writes form.Focused when a field is clicked.
    // Returns Join, Host, Back or None. busy disables fields and buttons. status shows under the panel,
    // coloured as an error when statusIsError.
    public static MenuAction BuildMultiplayer(MenuWidgets w, MultiplayerFormState form, bool busy, string? status, bool statusIsError)
    {
        w.DrawBackdrop();
        w.Dim(120);

        const float heightUnits = 318f;
        float top = MathF.Max(6f, (MenuWidgets.DesignHeightUnits - heightUnits) * 0.5f);
        UiRect panel = w.Centered(top, MultiplayerPanelWidthUnits, heightUnits);
        w.DrawPanel(panel);

        UiSizedText titleStyle = w.FitText("Multiplayer", panel.W, MenuWidgets.PanelTitleUnits);
        w.TextCentred("Multiplayer", w.CentreX, panel.Y + 12f * w.Scale, in titleStyle, MenuWidgets.Gold);
        w.DrawDivider(w.Inside(panel, 30f, 34f, MultiplayerPanelWidthUnits - 60f, 6f));

        MenuAction result = MenuAction.None;
        float buttonX = (MultiplayerPanelWidthUnits - MenuWidgets.ButtonWidthUnits) * 0.5f;

        FieldRow(w, panel, 44f, "Player name", FirstId + 40, form, MultiplayerField.PlayerName, form.PlayerName, busy);
        w.DrawDivider(w.Inside(panel, 30f, 72f, MultiplayerPanelWidthUnits - 60f, 6f));

        Header(w, panel, 82f, "Join a server");
        FieldRow(w, panel, 102f, "Address", FirstId + 41, form, MultiplayerField.Address, form.Address, busy);
        FieldRow(w, panel, 126f, "Port", FirstId + 42, form, MultiplayerField.JoinPort, form.JoinPort, busy);
        if (w.Button(FirstId + 44, w.Inside(panel, buttonX, 152f, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits), JoinLabel, enabled: !busy))
        {
            result = MenuAction.Join;
        }

        w.DrawDivider(w.Inside(panel, 30f, 178f, MultiplayerPanelWidthUnits - 60f, 6f));

        Header(w, panel, 188f, "Host a game");
        FieldRow(w, panel, 208f, "Port", FirstId + 43, form, MultiplayerField.HostPort, form.HostPort, busy);
        if (w.Button(FirstId + 45, w.Inside(panel, buttonX, 234f, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits), HostLabel, enabled: !busy))
        {
            result = MenuAction.Host;
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            UiSizedText style = w.FitText(status, panel.W - 24f * w.Scale, MenuWidgets.StatusUnits);
            w.TextCentred(status, w.CentreX, panel.Y + 262f * w.Scale, in style, statusIsError ? MenuWidgets.Error : MenuWidgets.Muted);
        }

        if (w.Button(FirstId + 46, w.Inside(panel, buttonX, 284f, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits), BackLabel, enabled: !busy))
        {
            result = MenuAction.Back;
        }

        return result;
    }

    private static void Header(MenuWidgets w, UiRect panel, float yUnits, string text)
    {
        UiRect row = w.Inside(panel, 14f, yUnits, MultiplayerPanelWidthUnits - 28f, 20f);
        UiSizedText style = w.FitTextInBox(text, row.W, MenuWidgets.ButtonLabelUnits, 20f);
        w.TextLeftCentred(text, row, row.X, in style, MenuWidgets.Gold);
    }

    private static void FieldRow(MenuWidgets w, UiRect panel, float yUnits, string label, int id, MultiplayerFormState form, MultiplayerField which, MenuTextField field, bool busy)
    {
        UiRect row = w.Inside(panel, 14f, yUnits, MultiplayerPanelWidthUnits - 28f, MpRowUnits);
        UiSizedText style = w.FitTextInBox(label, MpLabelWidthUnits * w.Scale, MenuWidgets.OptionRowUnits, MpRowUnits);
        w.TextLeftCentred(label, row, row.X, in style, busy ? MenuWidgets.Disabled : MenuWidgets.Muted);

        UiRect box = w.Inside(panel, MpFieldXUnits, yUnits + 2f, MultiplayerPanelWidthUnits - MpFieldXUnits - 14f, MpRowUnits - 4f);
        if (w.TextField(id, box, field, form.Focused == which, enabled: !busy))
        {
            form.Focused = which;
        }
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
                UiSizedText style = heading
                    ? w.FitTextInBox(line, panel.W, MenuWidgets.ButtonLabelUnits, 16f)
                    : w.FitTextInBox(line, panel.W, MenuWidgets.StatusUnits, 16f);
                w.TextCentred(line, w.CentreX, y, in style, heading ? MenuWidgets.Gold : MenuWidgets.Bone);
            }

            y += lineH;
        }

        UiRect back = w.Inside(panel, (300f - MenuWidgets.ButtonWidthUnits) * 0.5f, heightUnits - 34f, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits);
        return w.Button(FirstId + 20, back, "Back") ? MenuAction.Back : MenuAction.None;
    }
}
