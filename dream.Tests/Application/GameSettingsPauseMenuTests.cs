using Dream.Application;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Application;

public class GameSettingsPauseMenuTests
{
    private static GameSettings Create() => new(new Vector2i(1440, 1024));

    [Fact]
    public void Esc_OpensTheMenuAndBlocksGameplay_AndEscAgainResumes()
    {
        GameSettings settings = Create();

        settings.ToggleSettings();

        Assert.True(settings.PauseMenuVisible);
        Assert.False(settings.DeveloperSettingsVisible);
        Assert.True(settings.SettingsVisible);
        Assert.False(settings.CurrentRuntimeState.AllowGameplayInput);

        settings.ToggleSettings();

        Assert.False(settings.SettingsVisible);
        Assert.True(settings.CurrentRuntimeState.AllowGameplayInput);
    }

    [Fact]
    public void DeveloperSettings_ReplaceTheMenu_AndEscClosesThemOutright()
    {
        GameSettings settings = Create();
        settings.ToggleSettings();

        settings.ToggleDeveloperSettings();

        Assert.True(settings.DeveloperSettingsVisible);
        Assert.False(settings.PauseMenuVisible);
        Assert.True(settings.SettingsVisible);
        Assert.Equal(RuntimeMode.SettingsMenu, settings.CurrentRuntimeState.Mode);

        settings.ToggleSettings();

        Assert.False(settings.DeveloperSettingsVisible);
        Assert.False(settings.PauseMenuVisible);
        Assert.Equal(RuntimeMode.Gameplay, settings.CurrentRuntimeState.Mode);
    }

    [Fact]
    public void Resume_ClosesOnlyThePauseMenu()
    {
        GameSettings settings = Create();
        settings.ToggleSettings();

        settings.ClosePauseMenu();

        Assert.False(settings.SettingsVisible);
        Assert.Equal(RuntimeMode.Gameplay, settings.CurrentRuntimeState.Mode);
        settings.ClosePauseMenu();
        Assert.False(settings.SettingsVisible);
    }

    [Fact]
    public void OpeningAnOverlay_ClosesTheInventory()
    {
        GameSettings settings = Create();
        settings.ToggleInventory();

        settings.ToggleDeveloperSettings();

        Assert.False(settings.InventoryVisible);
        Assert.True(settings.DeveloperSettingsVisible);
    }

    [Fact]
    public void ResetOverlays_ClearsEveryOverlayForTheNextSession()
    {
        GameSettings settings = Create();
        settings.ToggleInventory();
        settings.ToggleSettings();

        settings.ResetOverlays();

        Assert.False(settings.SettingsVisible);
        Assert.False(settings.InventoryVisible);
        Assert.Equal(RuntimeMode.Gameplay, settings.CurrentRuntimeState.Mode);
    }

    [Fact]
    public void SetAdminHudAvailable_ClosesTheDeveloperWindowWhenTurnedOff()
    {
        GameSettings settings = Create();
        settings.SetAdminHudAvailable(true);
        settings.ToggleDeveloperSettings();

        settings.SetAdminHudAvailable(false);

        Assert.False(settings.AdminHudAvailable);
        Assert.False(settings.DeveloperSettingsVisible);
        Assert.Equal(RuntimeMode.Gameplay, settings.CurrentRuntimeState.Mode);
    }
}
