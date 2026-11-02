using Dream.Application;
using Dream.Client.Menu;
using Dream.Configuration;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui.Menu;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Menu;

public class MenuFlowTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void Launch_ShowsTheMenuByDefault()
    {
        Assert.True(MenuLaunchPolicy.ShouldShowMainMenu(Env()));
    }

    [Theory]
    [InlineData("DREAM_SKIP_MENU", "1")]
    [InlineData("DREAM_SKIP_MENU", "true")]
    [InlineData("DREAM_AUTO_CONNECT", "1")]
    [InlineData("DREAM_AUTO_CONNECT", "yes")]
    [InlineData("DREAM_HUD_CAPTURE", "G:/out/capture.png")]
    public void Launch_SkipsTheMenuForScriptedRuns(string key, string value)
    {
        Assert.False(MenuLaunchPolicy.ShouldShowMainMenu(Env((key, value))));
    }

    [Fact]
    public void Launch_SkipsTheMenuForTheRemoteClient_ButNotForSkipMenuZero()
    {
        Assert.False(MenuLaunchPolicy.ShouldShowMainMenu(Env(), remoteClientRequested: true));
        Assert.True(MenuLaunchPolicy.ShouldShowMainMenu(Env(("DREAM_SKIP_MENU", "0"), ("DREAM_HUD_CAPTURE", " "))));
    }

    [Fact]
    public void ScreenState_NavigatesOptionsKeyBindingsAndCredits()
    {
        MenuScreenState state = new();

        Assert.True(state.Apply(MenuAction.Options));
        Assert.Equal(MenuScreen.Options, state.Current);
        Assert.True(state.Apply(MenuAction.KeyBindings));
        Assert.Equal(MenuScreen.KeyBindings, state.Current);
        Assert.True(state.Apply(MenuAction.Back));
        Assert.Equal(MenuScreen.Options, state.Current);
        Assert.True(state.Apply(MenuAction.Back));
        Assert.Equal(MenuScreen.Root, state.Current);
        Assert.True(state.Apply(MenuAction.Credits));
        Assert.Equal(MenuScreen.Credits, state.Current);
        Assert.True(state.Apply(MenuAction.Back));
        Assert.Equal(MenuScreen.Root, state.Current);
        Assert.False(state.Apply(MenuAction.SinglePlayer));
        Assert.False(state.Apply(MenuAction.Back));
        state.Apply(MenuAction.Options);
        state.Reset();
        Assert.Equal(MenuScreen.Root, state.Current);
    }

    [Fact]
    public void StandardRows_AreWiredToSettingsAndConfig()
    {
        GameSettings settings = new(new Vector2i(1440, 1024));
        DesktopClientConfig config = new();
        int capApplied = 0;
        int persisted = 0;
        List<MenuOptionRow> rows = MenuOptionRows.CreateStandard(settings, config, () => capApplied++, () => persisted++);

        MenuOptionRow scale = rows.Single(r => r.Label == "UI Scale");
        MenuOptionRow fps = rows.Single(r => r.Label == "Max FPS");
        MenuOptionRow damage = rows.Single(r => r.Label == "Damage Text Size");
        MenuOptionRow admin = rows.Single(r => r.Label.StartsWith("Admin HUD", StringComparison.Ordinal));

        scale.SetValue!(2.5f);
        Assert.Equal(2.5f, settings.UiScaleSetting);
        Assert.Equal(2.5f, config.UiScale);
        scale.SetValue!(0.75f);
        Assert.Equal(0f, settings.UiScaleSetting);
        Assert.Equal("Auto", scale.FormatValue(scale.GetValue!()));
        scale.Commit!();
        Assert.Equal(1, persisted);

        fps.SetValue!(MenuOptionRows.FrameCaps.Length - 1);
        Assert.Equal(FrameRateCap.Uncapped, config.MaxFramesPerSecond);
        Assert.Equal("uncapped", fps.FormatValue(fps.GetValue!()));
        fps.SetValue!(1f);
        Assert.Equal(60, config.MaxFramesPerSecond);
        Assert.Equal(2, capApplied);

        damage.SetValue!(1.5f);
        Assert.Equal(1.5f, settings.FloatingCombatText.SizeMultiplier);
        Assert.Equal(1.5f, config.FloatingCombatText.SizeMultiplier);
        Assert.Equal("1.50x", damage.FormatValue(1.5f));

        admin.SetToggle!(true);
        Assert.True(settings.AdminHudAvailable);
        Assert.True(config.AdminHud);
        admin.SetToggle!(false);
        Assert.False(config.AdminHud);
    }

    [Theory]
    [InlineData(144, 4)]
    [InlineData(0, 6)]
    [InlineData(30, 0)]
    [InlineData(100, 2)]
    [InlineData(500, 5)]
    public void FrameCapIndex_PicksTheNearestStep(int cap, int expected)
    {
        Assert.Equal(expected, (int)MenuOptionRows.FrameCapIndex(cap));
    }
}
