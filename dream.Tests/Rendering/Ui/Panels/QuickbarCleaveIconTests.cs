using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Content;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

// Cleave has a real quickbar icon: its iconKey names a sprite in the shipped items atlas, so the tile
// draws the sprite and not the two letters that stand in for a skill without art.
public sealed class QuickbarCleaveIconTests
{
    private const string Key = "skill.cleave";

    [Fact]
    public void TheShippedSpriteTable_ContainsTheCleaveSprite_AsOneByOneCell()
    {
        UiSpriteTable table = StatusBarTestData.Table;

        Assert.True(table.TryGet(Key, out UiSprite sprite));
        Assert.True(sprite.W > 0 && sprite.H > 0);
        // A 1x1 item cell (20 u) with a 6 u margin all round, like the potions beside it.
        Assert.True(table.TryGet("item.potion.health", out UiSprite potion));
        Assert.Equal(potion.W, sprite.W);
        Assert.Equal(potion.H, sprite.H);
    }

    [Fact]
    public void TheCleaveSkill_NamesThatSprite_AsItsIcon()
    {
        Assert.True(ContentCatalog.Shipped.TryGetSkill(Key, out SkillDefinition cleave));
        Assert.Equal(Key, cleave.IconKey);

        QuickbarHudState bar = new();
        Assert.Equal(Key, bar.IconNameOf(0));
        Assert.True(StatusBarTestData.Table.TryGet(bar.IconNameOf(0), out _));
    }

    [Fact]
    public void TheCleaveTile_HasAnIcon_AndDrawsNoLetters()
    {
        UiLayout l = UiLayout.Create(1920, 1080, 0f);
        QuickbarHudState bar = new();
        HudView view = StatusBarTestData.View();
        view.Quickbar = bar;
        view.ManaLevel = 1f;
        view.HealthLevel = 1f;
        StatusBarRecordingCanvas c = new(StatusBarTestData.Table, recording: true);

        StatusBarPanel.Build(c, new StatusBarFakeInput(), view, l, StatusBarTestData.Table, new StatusBarFakeActions());

        Assert.DoesNotContain(c.Texts, t => t.Text == "Cl");
        // The other two skills have no art yet and keep their letters.
        Assert.Contains(c.Texts, t => t.Text == "Bo");
        Assert.Contains(c.Texts, t => t.Text == "Me");
    }
}
