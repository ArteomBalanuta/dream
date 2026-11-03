using System.Globalization;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using Dream.Tests.Rendering.Ui.Panels;
using Dream.Tests.Support;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui;

// The stats window at several resolutions and ui_scale values, with the real font. The PNGs always go to
// artifacts/hud-preview/ (char-<scale>_WxH.png) so the sizes can be looked at.
public class CharacterWindowPreviewTests
{
    private sealed class NoActions : IHudActions
    {
        public void Inventory(InventoryHudAction action) { }

        public void CloseInventory() { }
    }

    private static HudStatusSnapshot Status() => new(
        CurrentHealth: 73, MaxHealth: 120, InteractionMode: default, MinDamage: 5, MaxDamage: 9, Strength: 14,
        AttackSpeed: 1.2f, Vitality: 14, Level: 7, Experience: 4210, ExperienceToNextLevel: 6500,
        BaseStrength: 12, BaseAttackSpeed: 1f, BaseVitality: 14, Armor: 10, CurrentMana: 30, MaxMana: 70,
        MagicMinDamage: 3, MagicMaxDamage: 12);

    public static IEnumerable<object[]> Cases()
    {
        yield return [1440, 1024, 0f];
        yield return [1440, 1024, 1f];
        yield return [1440, 1024, 1.5f];
        yield return [2560, 1440, 0f];
        yield return [2560, 1440, 2f];
        yield return [2560, 1440, 1f];
        yield return [1920, 1080, 0f];
        yield return [1280, 720, 0f];
        yield return [3840, 2160, 0f];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CharacterWindow_RendersAtEveryScale(int w, int h, float configured)
    {
        using InventoryScene scene = new();
        HudStatusSnapshot status = Status();
        scene.View.Status = status;
        scene.View.StatRows = CharacterPanelModel.BuildStatRows(status);
        scene.View.Sheet = CharacterSheetModel.Build(status);

        (UiGlyphTable glyphs, UiTexturePixels page) = RealFont.Get();
        string tag = configured <= 0f ? "auto" : configured.ToString("0.##", CultureInfo.InvariantCulture);
        UiSoftwareCompositor c = HudPreview.Render(
            $"char-{tag}", w, h,
            (b, t, lay) =>
            {
                UiInputRouter router = new();
                b.BeginFrame(in lay);
                router.BeginFrame(new UiPointer(Vector2.Zero, false, false, false));
                ClassicHud.Build(b, router, scene.View, in lay, t, new NoActions());
                router.EndFrame();
            },
            configured, glyphs, page, forceWrite: true);

        Assert.Equal((w, h), (c.Width, c.Height));

        // Acceptance: attribute text is never below the baked 13 px, is at least 9.5 units tall once the
        // scale is past 1.5 (it was 13 px, 4.3 u at scale 3), and still fits its row and its box.
        UiLayout layout = UiLayout.Create(w, h, configured);
        var t = CharacterWindow.TextFor(in layout, CharacterWindow.AttributeTextUnits);
        Assert.True(t.Px >= 13f);
        if (layout.Scale >= 1.5f)
        {
            Assert.True(t.Px / layout.Scale >= 9f, $"{t.Px}px at scale {layout.Scale}");
        }

        Assert.True(t.Px <= 12f * layout.Scale + 1f, $"{t.Px}px overflows a {12f * layout.Scale}px row");
        UiBatch batch = new(glyphs);
        batch.BeginFrame(in layout);
        batch.TextScale = t.Scale;
        float widest = batch.MeasureText("Vitality", t.Style) + batch.MeasureText("12 +12 = 24", t.Style);
        float inner = (152f - 14f - 2f - 4f) * layout.Scale - 6f;
        Assert.True(layout.Scale <= 1f || widest <= inner,$"widest attribute row {widest}px > {inner}px at scale {layout.Scale}");
        Assert.Equal(t.Px, batch.LineHeight(t.Style), 1);
    }

    [Theory]
    [InlineData(1f, 13f)]
    [InlineData(2f, 19f)]
    [InlineData(3f, 28f)]
    [InlineData(4f, 38f)]
    public void TextFor_GrowsWithTheScaleAndNeverBelowTheBake(float scale, float px)
    {
        UiLayout layout = UiLayout.Create(10000, 10000, scale);
        Assert.Equal(scale, layout.Scale);
        Assert.Equal(px, CharacterWindow.TextFor(in layout, CharacterWindow.AttributeTextUnits).Px);
    }
}
