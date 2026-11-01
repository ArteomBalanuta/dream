using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Players;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

public class StatusBarPanelTests
{
    private static StatusBarRecordingCanvas Run(HudView v, UiLayout l, StatusBarFakeInput? input = null, StatusBarFakeActions? actions = null, bool recording = true)
    {
        StatusBarRecordingCanvas c = new(StatusBarTestData.Table, recording);
        StatusBarPanel.Build(c, input ?? new StatusBarFakeInput(), v, l, StatusBarTestData.Table, actions ?? new StatusBarFakeActions());
        return c;
    }

    private static UiLayout Layout1080() => UiLayout.Create(1920, 1080, 0f);

    private static UiRect GemRect(UiLayout l) => l.FromUnits(l.LeftGroup, 98f, -2f, 50f, 50f);

    // The bar draws a number as text, or as "hudnum" sprites when the art has them. This accepts either:
    // the text itself, or one numeral sprite per character (digits and the slash) in left to right order.
    private static bool ShowsNumber(StatusBarRecordingCanvas c, string number)
    {
        if (c.Texts.Any(t => t.Text == number))
        {
            return true;
        }

        string[] names = number.Select(ch => ch == '/' ? "hudnum.slash" : "hudnum." + ch).ToArray();
        List<string?> drawn = c.Calls.Where(x => x.Kind == StatusBarCallKind.Sprite && x.Name is not null && x.Name.StartsWith("hudnum.", StringComparison.Ordinal))
            .Select(x => x.Name).ToList();
        for (int start = 0; start + names.Length <= drawn.Count; start++)
        {
            if (drawn.Skip(start).Take(names.Length).SequenceEqual(names))
            {
                return true;
            }
        }

        return false;
    }

    // The status bar the way the surface feeds it: the drawn level is the health fraction.
    private static HudView ViewAtLevel(int hp, int maxHp)
    {
        HudView v = StatusBarTestData.View(hp, maxHp);
        v.HealthLevel = maxHp <= 0 ? 0f : Math.Clamp(hp / (float)maxHp, 0f, 1f);
        v.ManaLevel = StatusBarPanel.ManaGemFill;
        return v;
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(50, 100)]
    [InlineData(100, 100)]
    [InlineData(0, 0)]
    [InlineData(500, 100)]
    [InlineData(-5, 100)]
    public void HpGem_IsOneLiquidStoneAtTheDrawnLevel(int hp, int max)
    {
        UiLayout l = Layout1080();
        StatusBarRecordingCanvas c = Run(ViewAtLevel(hp, max), l);
        UiRect gem = GemRect(l);
        float fill = max <= 0 ? 0f : Math.Clamp(hp / (float)max, 0f, 1f);

        // One quad of the full stone: the canvas does the empty glass and the level. No separate empty
        // stone and no clipped copy.
        StatusBarCall call = Assert.Single(c.Sprites("gem.hp.full"));
        Assert.Equal(StatusBarCallKind.Gem, call.Kind);
        Assert.Equal(UiGemKind.Health, call.Gem.Kind);
        Assert.Equal(fill, call.Gem.Fill, 4);
        Assert.Equal(gem, call.Rect);
        Assert.Empty(c.Sprites("gem.hp.empty"));
        Assert.DoesNotContain(c.Calls, x => x.Kind == StatusBarCallKind.Clipped);
    }

    [Fact]
    public void HpGem_Half_ShowsTheHealthNumberOnTheGem()
    {
        UiLayout l = Layout1080();
        StatusBarRecordingCanvas c = Run(ViewAtLevel(50, 100), l);

        Assert.True(ShowsNumber(c, "50/100"));
    }

    [Fact]
    public void Exp_ZeroToNext_DrawsNoFillAndZeroPercent()
    {
        UiLayout l = Layout1080();
        StatusBarRecordingCanvas c = Run(StatusBarTestData.View(exp: 40, toNext: 0), l);

        Assert.DoesNotContain(c.Calls, x => x.Kind == StatusBarCallKind.Tile && x.Name == "exp.fill");
        Assert.Contains(c.Texts, t => t.Text == "0%");
        Assert.Single(c.Sprites("exp.trough"));
    }

    [Fact]
    public void Exp_Half_FillsHalfTheTroughWidthAndShowsPercentAndLevel()
    {
        UiLayout l = Layout1080();
        StatusBarRecordingCanvas c = Run(StatusBarTestData.View(exp: 50, toNext: 100, level: 12), l);

        UiRect span = l.FromUnits(l.CentreGroup, 22f + 4f, 5.5f, 191f, 5f);
        StatusBarCall fill = Assert.Single(c.Calls, x => x.Kind == StatusBarCallKind.Tile && x.Name == "exp.fill");
        Assert.Equal(MathF.Round(span.W * 0.5f), fill.Rect.W, 2);
        Assert.Equal(span.X, fill.Rect.X, 2);
        Assert.Contains(c.Texts, t => t.Text == "50%");
        Assert.True(ShowsNumber(c, "12"));
    }

    [Fact]
    public void Exp_OverFull_ClampsToTheTrough()
    {
        UiLayout l = Layout1080();
        StatusBarRecordingCanvas c = Run(StatusBarTestData.View(exp: 900, toNext: 100), l);

        UiRect span = l.FromUnits(l.CentreGroup, 22f + 4f, 5.5f, 191f, 5f);
        StatusBarCall fill = Assert.Single(c.Calls, x => x.Kind == StatusBarCallKind.Tile && x.Name == "exp.fill");
        Assert.Equal(MathF.Round(span.W), fill.Rect.W, 2);
    }

    [Fact]
    public void Mode_CombatShowsTheAttackIconAndBuildingShowsTheDisabledTile()
    {
        UiLayout l = Layout1080();
        StatusBarRecordingCanvas combat = Run(StatusBarTestData.View(mode: PlayerInteractionMode.Combat), l);
        StatusBarRecordingCanvas building = Run(StatusBarTestData.View(mode: PlayerInteractionMode.Building), l);

        Assert.Single(combat.Sprites("skill.tile.normal"));
        Assert.Single(combat.Sprites("skill.icon.default"));
        Assert.Contains(combat.Texts, t => t.Text == "Combat");
        Assert.Empty(building.Sprites("skill.tile.normal"));
        Assert.Single(building.Sprites("skill.tile.disabled"));
        Assert.Empty(building.Sprites("skill.icon.default"));
        Assert.Contains(building.Texts, t => t.Text == "Building");
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(1440, 1024)]
    public void ModeLabel_StandsAboveTheBar_NotOnTheExpTrough(int w, int h)
    {
        UiLayout l = UiLayout.Create(w, h, 0f);
        StatusBarRecordingCanvas c = Run(StatusBarTestData.View(mode: PlayerInteractionMode.Building), l);

        StatusBarCall label = c.Texts.First(t => t.Text == "Building");
        float lineHeight = 13f;
        Assert.True(label.TextPos.Y + lineHeight <= l.CentreGroup.Y, $"label bottom {label.TextPos.Y + lineHeight} is inside the bar (top {l.CentreGroup.Y})");
        Assert.True(label.TextPos.Y >= 0f);
    }

    [Fact]
    public void Coordinates_ShowTheFlooredXAndZOnThePlate()
    {
        UiLayout l = Layout1080();
        HudView v = StatusBarTestData.View();
        v.PlayerPosition = new Vector3(187.9f, 33f, -3.2f);
        StatusBarRecordingCanvas c = Run(v, l);

        StatusBarCall text = Assert.Single(c.Texts, t => t.Text == "187 -4");
        UiRect plate = Assert.Single(c.Sprites("hud.coordsplate")).Rect;
        Assert.True(plate.Contains(text.TextPos));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Coordinates_NonFinitePositionDrawsNoCoordinateText(float bad)
    {
        HudView v = StatusBarTestData.View();
        v.PlayerPosition = new Vector3(bad, 0f, 0f);
        StatusBarRecordingCanvas c = Run(v, Layout1080());

        UiRect plate = Assert.Single(c.Sprites("hud.coordsplate")).Rect;
        Assert.DoesNotContain(c.Texts, t => plate.Contains(t.TextPos));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(2560, 1080)]
    public void NoManaNumberIsEverDrawn_AndTheStoneIsOneLiquidQuadAtTheManaConstant(int w, int h)
    {
        UiLayout l = UiLayout.Create(w, h, 0f);
        HudView v = ViewAtLevel(33, 100);
        v.Status = v.Status with { Experience = 10 };
        StatusBarRecordingCanvas c = Run(v, l);

        UiRect mp = l.FromUnits(l.RightGroup, 492f - 441f, -2f, 50f, 50f);
        StatusBarCall gem = Assert.Single(c.Sprites("gem.mp.full"));
        Assert.Equal(StatusBarCallKind.Gem, gem.Kind);
        Assert.Equal(UiGemKind.Mana, gem.Gem.Kind);
        Assert.Equal(StatusBarPanel.ManaGemFill, gem.Gem.Fill, 4);
        Assert.Equal(0f, gem.Gem.Pulse);
        Assert.Equal(mp, gem.Rect);
        Assert.Empty(c.Sprites("gem.mp.empty"));
        Assert.Empty(c.Sprites("gem.mp.mask"));
        Assert.DoesNotContain(c.Texts, t => mp.Contains(t.TextPos));
        // No numeral sprite sits on the right group either.
        Assert.DoesNotContain(c.Calls, x => x.Name is not null && x.Name.StartsWith("hudnum.", StringComparison.Ordinal) && x.Rect.X >= l.RightGroup.X);
        // Every text the bar draws is on the left group or the centre group; none on the right group.
        Assert.DoesNotContain(c.Texts, t => t.TextPos.X >= l.RightGroup.X);
    }

    [Fact]
    public void InertSockets_DrawNoIconAndNoLetterAndNoGaugeFill()
    {
        UiLayout l = Layout1080();
        StatusBarRecordingCanvas c = Run(StatusBarTestData.View(), l);

        foreach (string absent in new[] { "icon.scroll", "icon.party", "icon.pvp", "icon.banner", "icon.lens", "hud.ministat.on", "hud.ministat.off",
                     "qwe.letter.q", "qwe.letter.w", "qwe.letter.e", "gauge.left.fill", "gauge.right.fill", "gem.hp.mask", "gem.hp.spec" })
        {
            Assert.Empty(c.Sprites(absent));
        }

        Assert.Equal(3, c.Sprites("hud.slab.normal").Count());
        Assert.Equal(2, c.Sprites("gauge.empty").Count());
        Assert.Equal(3, c.Sprites("iconbtn.frame.normal").Count());
        Assert.Equal(3, c.Sprites("qwe.notch").Count());

        // No text lands on a slab, a Q/W/E socket, or the banner and lens frames.
        List<UiRect> sockets = c.Sprites("hud.slab.normal").Select(x => x.Rect)
            .Concat(c.Sprites("qwe.notch").Select(x => x.Rect))
            .Concat(c.Sprites("iconbtn.frame.normal").Select(x => x.Rect))
            .ToList();
        foreach (StatusBarCall t in c.Texts)
        {
            Assert.DoesNotContain(sockets, r => r.Contains(t.TextPos));
        }

        foreach (string n in new[] { "qwe.slot.q.normal", "qwe.slot.w.normal", "qwe.slot.e.normal" })
        {
            UiRect r = Assert.Single(c.Sprites(n)).Rect;
            Assert.DoesNotContain(c.Texts, t => r.Contains(t.TextPos));
        }
    }

    [Fact]
    public void Satchel_ClosesAnOpenOverlayAndIsInertWhenClosed()
    {
        UiLayout l = Layout1080();
        StatusBarFakeInput input = new() { ClickedId = StatusBarPanel.SatchelButtonId, HoveredId = StatusBarPanel.SatchelButtonId };

        StatusBarFakeActions closed = new();
        StatusBarRecordingCanvas cClosed = Run(StatusBarTestData.View(), l, input, closed);
        Assert.Equal(0, closed.CloseCount);
        Assert.Equal(3, cClosed.Sprites("iconbtn.frame.normal").Count());

        HudView open = StatusBarTestData.View();
        open.InventoryOpen = true;
        StatusBarFakeActions opened = new();
        StatusBarRecordingCanvas cOpen = Run(open, l, input, opened);
        Assert.Equal(1, opened.CloseCount);
        Assert.Equal(0, opened.InventoryCount);
        Assert.Single(cOpen.Sprites("iconbtn.frame.hover"));
        Assert.Single(cOpen.Sprites("icon.satchel"));

        StatusBarRecordingCanvas cPressed = Run(open, l, new StatusBarFakeInput(), new StatusBarFakeActions());
        Assert.Single(cPressed.Sprites("iconbtn.frame.pressed"));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1080)]
    [InlineData(1280, 720)]
    [InlineData(1440, 1024)]
    public void Groups_AnchorLeftCentreAndRightAndGapsAreTiled(int w, int h)
    {
        UiLayout l = UiLayout.Create(w, h, 0f);
        StatusBarRecordingCanvas c = Run(StatusBarTestData.View(), l);

        Assert.Equal(l.LeftGroup, Assert.Single(c.Sprites("hud.base.left")).Rect);
        Assert.Equal(l.CentreGroup, Assert.Single(c.Sprites("hud.base.centre")).Rect);
        Assert.Equal(l.RightGroup, Assert.Single(c.Sprites("hud.base.right")).Rect);
        Assert.Equal(0f, l.LeftGroup.X);
        Assert.Equal(w, l.RightGroup.Right, 2);
        Assert.Equal(h, l.Bar.Bottom, 2);
        Assert.InRange(MathF.Abs(w * 0.5f - (l.CentreGroup.X + l.CentreGroup.W * 0.5f)), 0f, 0.5f);

        List<UiRect> tiles = c.Calls.Where(x => x.Kind == StatusBarCallKind.Tile && x.Name == "hud.gapfill").Select(x => x.Rect).ToList();
        int expected = (l.LeftGap.W > 0f ? 1 : 0) + (l.RightGap.W > 0f ? 1 : 0);
        Assert.Equal(expected, tiles.Count);
        // Every tested viewport is wider than the 640 u bar, so both gaps are real.
        Assert.Equal(2, tiles.Count);
        Assert.True(l.LeftGap.W > 0f && l.RightGap.W > 0f);
        Assert.Equal(l.LeftGap, tiles[0]);
        Assert.Equal(l.RightGap, tiles[1]);

        // The gem sits inside the left group, the mana gem inside the right group, both full height.
        UiRect hpGem = Assert.Single(c.Sprites("gem.hp.full")).Rect;
        UiRect mpGem = Assert.Single(c.Sprites("gem.mp.full")).Rect;
        Assert.True(hpGem.X >= l.LeftGroup.X && hpGem.Right <= l.LeftGroup.Right);
        Assert.True(mpGem.X >= l.RightGroup.X && mpGem.Right <= l.RightGroup.Right + 0.01f);
        Assert.Equal(hpGem.W, mpGem.W, 3);
        // Symmetric about the viewport centre.
        Assert.Equal(w - hpGem.Right, mpGem.X, 2);
    }

    [Fact]
    public void Dragons_DrawLastAboveTheBar()
    {
        UiLayout l = Layout1080();
        StatusBarRecordingCanvas c = Run(StatusBarTestData.View(), l);

        int left = c.Calls.FindIndex(x => x.Name == "hud.dragon.left");
        int right = c.Calls.FindIndex(x => x.Name == "hud.dragon.right");
        int firstText = c.Calls.FindIndex(x => x.Kind == StatusBarCallKind.Text);
        // The bar's numerals are Hud-page sprites that follow the dragons; everything else is under them.
        int lastArt = c.Calls.FindLastIndex(x => x.Kind != StatusBarCallKind.Text && x.Name != "hud.dragon.left" && x.Name != "hud.dragon.right"
            && !(x.Name?.StartsWith("hudnum.", StringComparison.Ordinal) ?? false));
        Assert.True(left >= 0 && right > left);
        // Above all the bar's art; the text follows in one run (no text overlaps a dragon).
        Assert.True(lastArt < left);
        Assert.True(firstText > right);
        // The dragons stand 6 u over the plate: their bottom edge is 6 u below the top of the bar.
        Assert.Equal(l.Bar.Y + 6f * l.Scale, c.Calls[left].Rect.Bottom, 2);
        Assert.Equal(0f, c.Calls[left].Rect.X);
        Assert.Equal(l.ViewportWidth, c.Calls[right].Rect.Right, 2);
    }

    [Fact]
    public void Build_AllocatesNothingAfterWarmUp()
    {
        UiLayout l = Layout1080();
        HudView v = StatusBarTestData.View(40, 100, 30, 100);
        v.InventoryOpen = true;
        v.PlayerPosition = new Vector3(12.5f, 3f, 88f);
        StatusBarFakeInput input = new();
        StatusBarFakeActions actions = new();
        StatusBarRecordingCanvas c = new(StatusBarTestData.Table, recording: false);

        for (int i = 0; i < 3; i++)
        {
            StatusBarPanel.Build(c, input, v, l, StatusBarTestData.Table, actions);
        }

        Dream.Tests.Utilities.AllocationAssert.AtMost(0, () =>
        {
            StatusBarPanel.Build(c, input, v, l, StatusBarTestData.Table, actions);
        });

        Assert.True(c.Count > 100);
    }

    [Theory]
    [InlineData(0, '/', 0, "0/0")]
    [InlineData(1234, '/', 99999, "1234/99999")]
    [InlineData(-5, ' ', -2147483648, "-5 -2147483648")]
    public void PanelTextPair_MatchesTheInterpolatedText(int first, char separator, int second, string expected)
    {
        Span<char> buf = stackalloc char[48];

        Assert.True(PanelText.TryWritePair(buf, first, separator, second, out int n));
        Assert.Equal(expected, buf[..n].ToString());
    }

    [Fact]
    public void PanelText_ReportsATooSmallBufferInsteadOfWritingPastIt()
    {
        Assert.False(PanelText.TryWritePair(new char[3], 12, '/', 34, out int n));
        Assert.Equal(0, n);
        Assert.False(PanelText.TryWritePair(new char[2], 12, '/', 3, out _));
        Assert.False(PanelText.TryWritePercent(new char[2], 12, out n));
        Assert.Equal(0, n);
        Assert.True(PanelText.TryWritePercent(new char[3], 12, out n));
        Assert.Equal(3, n);
    }
}
