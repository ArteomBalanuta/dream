using Dream.Assets;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Menu;
using Dream.Tests.Rendering.Ui.Panels;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Menu;

// The front-end panels on a recording canvas under the real router, driven frame by frame the way the
// menu renderer drives them (hover, press, release).
public sealed class MenuPanelTests
{
    private const int Width = 1440;
    private const int Height = 1024;

    private readonly UiSpriteTable _table;
    private readonly MenuSkin _skin;
    private readonly UiInputRouter _router = new();
    private readonly MenuWidgets _widgets = new();

    public MenuPanelTests()
    {
        Assert.True(UiSpriteTable.TryLoadPages(AssetPaths.UiDirectory, MenuAtlasLoader.Pages, out UiSpriteTable? table, out string error), error);
        _table = table!;
        _skin = MenuSkin.From(_table);
    }

    private InventoryFakeCanvas Step(Func<MenuWidgets, MenuAction> build, out MenuAction action, Vector2? at = null, bool left = false)
    {
        InventoryFakeCanvas canvas = new(_table);
        UiLayout layout = UiLayout.Create(Width, Height, 0f);
        _router.BeginFrame(new UiPointer(at ?? new Vector2(-1000f, -1000f), left, false, true));
        _widgets.Begin(canvas, _router, _skin, in layout);
        action = build(_widgets);
        _router.EndFrame();
        return canvas;
    }

    // Hover, press, release over a point; returns the action of the release frame.
    private MenuAction Click(Func<MenuWidgets, MenuAction> build, Vector2 at)
    {
        Step(build, out _, at);
        Step(build, out _, at, left: true);
        Step(build, out MenuAction action, at);
        return action;
    }

    private static Vector2 Centre(UiRect r) => new(r.X + r.W * 0.5f, r.Y + r.H * 0.5f);

    private static MenuAction Main(MenuWidgets w, bool busy = false) => MainMenuPanel.Build(w, busy, null, false);

    [Fact]
    public void MenuPage_HasTheArtAndTheSkinFindsIt()
    {
        Assert.True(_skin.HasMenuArt);
        Assert.NotNull(_skin.BackdropSprite);
        Assert.NotNull(_skin.TitleSprite);
        Assert.NotNull(_skin.SliderKnobSprite);
        Assert.Equal(UiPage.Menu, _table.Get(MenuSkin.Title).Page);
        Assert.True(_table.Get(MenuSkin.ButtonNormal).HasSlice);
    }

    [Fact]
    public void MainMenu_DrawsBackdropTitleAndFiveButtons()
    {
        InventoryFakeCanvas canvas = Step(w => Main(w), out MenuAction action);

        Assert.Equal(MenuAction.None, action);
        Assert.Single(canvas.Named(MenuSkin.Backdrop));
        Assert.Single(canvas.Named(MenuSkin.Title));
        Assert.Equal(5, canvas.Named(MenuSkin.ButtonNormal).Count());
        foreach (string label in new[] { "Single Player", "Multiplayer", "Options", "Credits", "Exit" })
        {
            Assert.True(canvas.HasText(label), label);
        }
    }

    [Theory]
    [InlineData(0, MenuAction.SinglePlayer)]
    [InlineData(1, MenuAction.Multiplayer)]
    [InlineData(2, MenuAction.Options)]
    [InlineData(3, MenuAction.Credits)]
    [InlineData(4, MenuAction.Exit)]
    public void MainMenu_ClickingAButtonReturnsItsAction(int index, MenuAction expected)
    {
        Step(w => Main(w), out _);
        UiRect button = _widgets.ButtonRect(MainMenuPanel.ButtonsTopUnits, index);

        Assert.Equal(expected, Click(w => Main(w), Centre(button)));
    }

    [Fact]
    public void MainMenu_BusyDisablesTheButtons()
    {
        Step(w => Main(w, busy: true), out _);
        UiRect button = _widgets.ButtonRect(MainMenuPanel.ButtonsTopUnits, 0);

        Assert.Equal(MenuAction.None, Click(w => Main(w, busy: true), Centre(button)));
        InventoryFakeCanvas canvas = Step(w => Main(w, busy: true), out _);
        Assert.Equal(5, canvas.Named(MenuSkin.ButtonDisabled).Count());
    }

    [Fact]
    public void MainMenu_ShowsTheStatusLine()
    {
        InventoryFakeCanvas canvas = Step(w => MainMenuPanel.Build(w, false, "Session ended: gone", true), out _);

        Assert.True(canvas.HasText("Session ended: gone"));
    }

    private static MultiplayerFormState NewForm() => new("Wanderer", "example.org", 7777, 7778);

    private static MenuAction Mp(MenuWidgets w, MultiplayerFormState form, bool busy = false) =>
        MainMenuPanel.BuildMultiplayer(w, form, busy, null, false);

    // Rects of the Multiplayer controls from the layout the panel uses: centred in the 480 u design area.
    private UiRect MpRect(float xUnits, float yUnits, float wUnits, float hUnits)
    {
        const float height = 318f;
        UiRect panel = _widgets.Centered((MenuWidgets.DesignHeightUnits - height) * 0.5f, MainMenuPanel.MultiplayerPanelWidthUnits, height);
        return _widgets.Inside(panel, xUnits, yUnits, wUnits, hUnits);
    }

    private UiRect MpButton(float yUnits) =>
        MpRect((MainMenuPanel.MultiplayerPanelWidthUnits - MenuWidgets.ButtonWidthUnits) * 0.5f, yUnits, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits);

    // The field column starts at 14 + 100 + 6 units and ends 14 units short of the panel edge.
    private UiRect MpField(float rowYUnits) => MpRect(120f, rowYUnits + 2f, 340f - 120f - 14f, 20f);

    [Fact]
    public void Multiplayer_DrawsTheFieldsAndThreeButtons()
    {
        MultiplayerFormState form = NewForm();
        InventoryFakeCanvas canvas = Step(w => Mp(w, form), out MenuAction action);

        Assert.Equal(MenuAction.None, action);
        Assert.Equal(3, canvas.Named(MenuSkin.ButtonNormal).Count());
        foreach (string text in new[] { "Multiplayer", "Player name", "Join a server", "Address", "Port", "Host a game", "Join", "Host", "Back", "Wanderer", "example.org", "7777", "7778" })
        {
            Assert.True(canvas.HasText(text), text);
        }
    }

    [Theory]
    [InlineData(152f, MenuAction.Join)]
    [InlineData(234f, MenuAction.Host)]
    [InlineData(284f, MenuAction.Back)]
    public void Multiplayer_ClickingAButtonReturnsItsAction(float yUnits, MenuAction expected)
    {
        MultiplayerFormState form = NewForm();
        Step(w => Mp(w, form), out _);

        Assert.Equal(expected, Click(w => Mp(w, form), Centre(MpButton(yUnits))));
    }

    [Theory]
    [InlineData(44f, MultiplayerField.PlayerName)]
    [InlineData(102f, MultiplayerField.Address)]
    [InlineData(126f, MultiplayerField.JoinPort)]
    [InlineData(208f, MultiplayerField.HostPort)]
    public void Multiplayer_ClickingAFieldFocusesIt(float rowYUnits, MultiplayerField expected)
    {
        MultiplayerFormState form = NewForm();
        form.Focused = expected == MultiplayerField.Address ? MultiplayerField.HostPort : MultiplayerField.Address;
        Step(w => Mp(w, form), out _);

        Assert.Equal(MenuAction.None, Click(w => Mp(w, form), Centre(MpField(rowYUnits))));
        Assert.Equal(expected, form.Focused);
    }

    [Fact]
    public void Multiplayer_BusyDisablesFieldsAndButtons()
    {
        MultiplayerFormState form = NewForm();
        Step(w => Mp(w, form, busy: true), out _);

        Assert.Equal(MenuAction.None, Click(w => Mp(w, form, busy: true), Centre(MpButton(152f))));
        Assert.Equal(MenuAction.None, Click(w => Mp(w, form, busy: true), Centre(MpButton(284f))));
        Click(w => Mp(w, form, busy: true), Centre(MpField(44f)));
        Assert.Equal(MultiplayerField.Address, form.Focused);
        InventoryFakeCanvas canvas = Step(w => Mp(w, form, busy: true), out _);
        Assert.Equal(3, canvas.Named(MenuSkin.ButtonDisabled).Count());
    }

    [Fact]
    public void Multiplayer_ShowsTheStatusLine()
    {
        MultiplayerFormState form = NewForm();
        InventoryFakeCanvas canvas = Step(w => MainMenuPanel.BuildMultiplayer(w, form, false, "Could not connect", true), out _);

        Assert.True(canvas.HasText("Could not connect"));
    }

    [Fact]
    public void Credits_ListTheLinesAndBack()
    {
        InventoryFakeCanvas canvas = Step(MainMenuPanel.BuildCredits, out MenuAction action);

        Assert.Equal(MenuAction.None, action);
        Assert.True(canvas.HasText("DREAM"));
        Assert.True(canvas.HasText("Thank you for playing."));
        Assert.True(canvas.HasText("Back"));
    }

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 5)]
    public void PauseMenu_ListsDeveloperSettingsOnlyForTheAdminHud(bool admin, int expectedButtons)
    {
        InventoryFakeCanvas canvas = Step(w => PauseMenuPanel.Build(w, admin), out _);

        Assert.Equal(expectedButtons, canvas.Named(MenuSkin.ButtonNormal).Count());
        Assert.Equal(admin, canvas.HasText(PauseMenuPanel.DeveloperLabel));
        foreach (string label in new[] { "Resume", "Options", "Exit to Main Menu", "Exit Game" })
        {
            Assert.True(canvas.HasText(label), label);
        }
    }

    [Fact]
    public void PauseMenu_ClicksReturnResumeAndExitActions()
    {
        Step(w => PauseMenuPanel.Build(w, false), out _);
        float top = (MenuWidgets.DesignHeightUnits - (54f + 4 * MenuWidgets.ButtonPitchUnits + 8f)) * 0.5f;
        UiRect Row(int i) => _widgets.Centered(top + 52f + i * MenuWidgets.ButtonPitchUnits, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits);

        Assert.Equal(MenuAction.Resume, Click(w => PauseMenuPanel.Build(w, false), Centre(Row(0))));
        Assert.Equal(MenuAction.Options, Click(w => PauseMenuPanel.Build(w, false), Centre(Row(1))));
        Assert.Equal(MenuAction.ExitToMainMenu, Click(w => PauseMenuPanel.Build(w, false), Centre(Row(2))));
        Assert.Equal(MenuAction.ExitGame, Click(w => PauseMenuPanel.Build(w, false), Centre(Row(3))));
    }

    [Fact]
    public void KeyBindings_ListEveryEntry()
    {
        InventoryFakeCanvas canvas = Step(w => KeyBindingsPanel.Build(w, backdrop: false), out _);

        foreach ((string input, string effect) in KeyBindingsPanel.Entries)
        {
            Assert.True(canvas.HasText(input), input);
            Assert.True(canvas.HasText(effect), effect);
        }
    }

    private sealed class Probe
    {
        public float Value = 0.5f;
        public bool Toggle;
        public int SetCalls;
        public int Commits;
        public int ToggleCommits;
    }

    private static List<MenuOptionRow> Rows(Probe p) =>
    [
        MenuOptionRow.Header("Sound"),
        MenuOptionRow.Slider("Volume", 0f, 1f, 0.1f, () => p.Value, v => { p.Value = v; p.SetCalls++; }, v => $"{v * 100f:0}%", () => p.Commits++),
        MenuOptionRow.Toggle("Muted", () => p.Toggle, v => p.Toggle = v, () => p.ToggleCommits++),
        MenuOptionRow.Toggle("Locked", () => true, _ => { }, isEnabled: () => false)
    ];

    // The pointer position of a row's control, computed from the panel the same way it lays them out.
    private (UiRect Track, UiRect Toggle) ControlRects(int rowIndex, int rowCount)
    {
        float heightUnits = 50f + rowCount * OptionsPanel.RowHeightUnits + 8f + 2f * MenuWidgets.ButtonPitchUnits + 8f;
        float top = MathF.Max(6f, (MenuWidgets.DesignHeightUnits - heightUnits) * 0.5f);
        UiRect panel = _widgets.Centered(top, OptionsPanel.PanelWidthUnits, heightUnits);
        UiRect row = _widgets.Inside(panel, 14f, 46f + rowIndex * OptionsPanel.RowHeightUnits, OptionsPanel.PanelWidthUnits - 28f, OptionsPanel.RowHeightUnits);
        float controlX = OptionsPanel.LabelWidthUnits + 6f;
        return (
            _widgets.Inside(row, controlX, (OptionsPanel.RowHeightUnits - 6f) * 0.5f, OptionsPanel.TrackWidthUnits, 6f),
            _widgets.Inside(row, controlX, 1f, 56f, OptionsPanel.RowHeightUnits - 2f));
    }

    [Fact]
    public void Options_DrawsLabelsValuesAndFooterButtons()
    {
        Probe p = new();
        InventoryFakeCanvas canvas = Step(w => OptionsPanel.Build(w, Rows(p), backdrop: true), out _);

        foreach (string text in new[] { "Sound", "Volume", "50%", "Muted", "Off", "Key Bindings", "Back" })
        {
            Assert.True(canvas.HasText(text), text);
        }
    }

    [Fact]
    public void Options_DraggingASliderSetsTheValueAndCommitsOnRelease()
    {
        Probe p = new();
        List<MenuOptionRow> rows = Rows(p);
        Func<MenuWidgets, MenuAction> build = w => OptionsPanel.Build(w, rows, backdrop: false);
        Step(build, out _);
        UiRect track = ControlRects(1, rows.Count).Track;
        Vector2 start = new(track.X + track.W * 0.5f, track.Y + track.H * 0.5f);
        Vector2 end = new(track.X + track.W * 0.9f, start.Y);

        Step(build, out _, start);
        Step(build, out _, start, left: true);
        Assert.Equal(0, p.Commits);
        Step(build, out _, end, left: true);
        Assert.Equal(0.9f, p.Value, 3);
        Step(build, out _, end);
        Assert.Equal(1, p.Commits);
        Assert.Equal(0.9f, p.Value, 3);
    }

    [Fact]
    public void Options_ASliderPressedOutsideItDoesNotMove()
    {
        Probe p = new();
        List<MenuOptionRow> rows = Rows(p);
        Func<MenuWidgets, MenuAction> build = w => OptionsPanel.Build(w, rows, backdrop: false);
        Step(build, out _);
        UiRect track = ControlRects(1, rows.Count).Track;

        Step(build, out _, new Vector2(track.X - 200f, track.Y), left: true);
        Step(build, out _, new Vector2(track.X + track.W * 0.8f, track.Y + track.H * 0.5f), left: true);
        Step(build, out _);

        Assert.Equal(0.5f, p.Value);
        Assert.Equal(0, p.SetCalls);
        Assert.Equal(0, p.Commits);
    }

    [Fact]
    public void Options_ClickingAToggleFlipsItAndCommits()
    {
        Probe p = new();
        List<MenuOptionRow> rows = Rows(p);
        Func<MenuWidgets, MenuAction> build = w => OptionsPanel.Build(w, rows, backdrop: false);
        Step(build, out _);
        Vector2 at = Centre(ControlRects(2, rows.Count).Toggle);

        Click(build, at);

        Assert.True(p.Toggle);
        Assert.Equal(1, p.ToggleCommits);
    }

    [Fact]
    public void Options_ADisabledToggleIgnoresClicks()
    {
        Probe p = new();
        int sets = 0;
        List<MenuOptionRow> rows = [MenuOptionRow.Toggle("Locked", () => false, _ => sets++, isEnabled: () => false)];
        Func<MenuWidgets, MenuAction> build = w => OptionsPanel.Build(w, rows, backdrop: false);
        Step(build, out _);
        Vector2 at = Centre(ControlRects(0, 1).Toggle);

        Click(build, at);

        Assert.Equal(0, sets);
    }

    [Fact]
    public void Options_FooterButtonsReturnKeyBindingsAndBack()
    {
        Probe p = new();
        List<MenuOptionRow> rows = Rows(p);
        Func<MenuWidgets, MenuAction> build = w => OptionsPanel.Build(w, rows, backdrop: false);
        Step(build, out _);
        float heightUnits = 50f + rows.Count * OptionsPanel.RowHeightUnits + 8f + 2f * MenuWidgets.ButtonPitchUnits + 8f;
        float top = MathF.Max(6f, (MenuWidgets.DesignHeightUnits - heightUnits) * 0.5f);
        UiRect panel = _widgets.Centered(top, OptionsPanel.PanelWidthUnits, heightUnits);
        float buttonsTop = 46f + rows.Count * OptionsPanel.RowHeightUnits + 8f;
        float x = (OptionsPanel.PanelWidthUnits - MenuWidgets.ButtonWidthUnits) * 0.5f;
        UiRect keys = _widgets.Inside(panel, x, buttonsTop, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits);
        UiRect back = _widgets.Inside(panel, x, buttonsTop + MenuWidgets.ButtonPitchUnits, MenuWidgets.ButtonWidthUnits, MenuWidgets.ButtonHeightUnits);

        Assert.Equal(MenuAction.KeyBindings, Click(build, Centre(keys)));
        Assert.Equal(MenuAction.Back, Click(build, Centre(back)));
    }

    [Fact]
    public void Panels_FallBackToWindowPlatesWhenTheMenuArtIsMissing()
    {
        UiSpriteTable windowOnly = new();
        UiSpriteTable full = _table;
        Assert.True(UiSpriteTable.TryLoadPages(AssetPaths.UiDirectory, [UiPage.Window], out UiSpriteTable? table, out string error), error);
        windowOnly = table!;
        MenuSkin skin = MenuSkin.From(windowOnly);
        InventoryFakeCanvas canvas = new(full);
        UiLayout layout = UiLayout.Create(Width, Height, 0f);
        _router.BeginFrame(new UiPointer(new Vector2(-1000f, -1000f), false, false, true));
        _widgets.Begin(canvas, _router, skin, in layout);

        MainMenuPanel.Build(_widgets, false, null, false);
        _router.EndFrame();

        Assert.False(skin.HasMenuArt);
        Assert.Empty(canvas.Named(MenuSkin.Title));
        Assert.Equal(5, canvas.Named(UiSpriteNames.ButtonNormal).Count());
        Assert.True(canvas.HasText("Single Player"));
    }
}
