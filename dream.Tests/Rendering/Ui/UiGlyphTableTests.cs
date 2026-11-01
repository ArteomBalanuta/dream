using Dream.Rendering.Ui;
using Dream.Tests.Support;
using Xunit;

namespace Dream.Tests.Rendering.Ui;

public class UiGlyphTableTests
{
    private static UiGlyph G(float advance) => new(0, 0, 5, 9, 0, 0, 1, 1, advance);

    [Fact]
    public void Table_ReturnsStoredGlyphsAndFallsBackToQuestionMark()
    {
        UiGlyphTable t = new(13f, 26f);
        t.Set(UiTextStyle.Body, 'a', G(7));
        t.Set(UiTextStyle.Body, '?', G(9));

        Assert.True(t.TryGet(UiTextStyle.Body, 'a', out UiGlyph a));
        Assert.Equal(7f, a.Advance);
        Assert.True(t.TryGet(UiTextStyle.Body, 'z', out UiGlyph z));
        Assert.Equal(9f, z.Advance);
        Assert.True(t.TryGet(UiTextStyle.Body, '中', out UiGlyph cjk));
        Assert.Equal(9f, cjk.Advance);
        Assert.False(t.Has(UiTextStyle.Body, 'z'));
        Assert.Equal(13f, t.LineHeight(UiTextStyle.Body));
        Assert.Equal(26f, t.LineHeight(UiTextStyle.Large));
    }

    [Fact]
    public void EmptyTable_HasNothingToDraw()
    {
        UiGlyphTable t = new(13f, 26f);
        Assert.False(t.TryGet(UiTextStyle.Large, 'a', out _));
    }

    [Fact]
    public void Set_RejectsCodepointsOutsideTheTable()
    {
        UiGlyphTable t = new(13f, 26f);
        Assert.Throws<ArgumentOutOfRangeException>(() => t.Set(UiTextStyle.Body, 'Ā', G(1)));
    }

    // Proves FindGlyph-style glyph access and the 26 px bake work in this ImGui.NET version, against
    // the real native library (no GL needed).
    [Fact]
    public void RealFont_ProvidesAsciiGlyphsAtTwoSizes()
    {
        (UiGlyphTable glyphs, UiTexturePixels page) = RealFont.Get();

        Assert.True(page.Width > 0 && page.Height > 0);
        for (char c = ' '; c <= '~'; c++)
        {
            Assert.True(glyphs.Has(UiTextStyle.Body, c), $"body '{c}'");
            Assert.True(glyphs.Has(UiTextStyle.Large, c), $"large '{c}'");
        }

        Assert.True(glyphs.TryGet(UiTextStyle.Body, 'W', out UiGlyph bodyW));
        Assert.True(glyphs.TryGet(UiTextStyle.Large, 'W', out UiGlyph largeW));
        Assert.True(bodyW.Advance is >= 4f and <= 13f, $"body W {bodyW}");
        Assert.True(largeW.Advance > bodyW.Advance * 1.5f, "the large bake is about twice the body size");
        Assert.True(glyphs.LineHeight(UiTextStyle.Large) > glyphs.LineHeight(UiTextStyle.Body) * 1.5f);
        Assert.InRange(bodyW.U0, 0f, 1f);
        Assert.InRange(bodyW.V1, 0f, 1f);
        Assert.True(bodyW.U1 > bodyW.U0 && bodyW.V1 > bodyW.V0);
    }

    [Fact]
    public void RealFont_GlyphUvsPointAtInkOnTheFontPage()
    {
        (UiGlyphTable glyphs, UiTexturePixels page) = RealFont.Get();
        Assert.True(glyphs.TryGet(UiTextStyle.Body, 'I', out UiGlyph g));

        int x0 = (int)MathF.Round(g.U0 * page.Width);
        int x1 = (int)MathF.Round(g.U1 * page.Width);
        int y0 = (int)MathF.Round(g.V0 * page.Height);
        int y1 = (int)MathF.Round(g.V1 * page.Height);
        int inked = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                if (page.Rgba[(y * page.Width + x) * 4 + 3] > 128)
                {
                    inked++;
                }
            }
        }

        Assert.True(inked > 4, $"expected ink inside the 'I' rect, found {inked} texels");
    }

    [Fact]
    public void RealFont_LatinOneAccentsAreEitherPresentOrDrawQuestionMark()
    {
        (UiGlyphTable glyphs, _) = RealFont.Get();
        Assert.True(glyphs.TryGet(UiTextStyle.Body, 'é', out UiGlyph accent));
        Assert.True(glyphs.TryGet(UiTextStyle.Body, '?', out UiGlyph question));
        if (!glyphs.Has(UiTextStyle.Body, 'é'))
        {
            Assert.Equal(question, accent);
        }
    }

    private static readonly UiGlyph Sane = new(0f, -9f, 6f, 1f, 0.1f, 0.1f, 0.2f, 0.2f, 7f);

    [Fact]
    public void GlyphFault_AcceptsASaneGlyph()
    {
        Assert.Null(UiFontSetup.GlyphFault(Sane));
    }

    [Theory]
    [InlineData("x1equalsx0")]
    [InlineData("x1below")]
    [InlineData("uOver")]
    [InlineData("vNegative")]
    [InlineData("nan")]
    [InlineData("advanceZero")]
    [InlineData("advanceNegative")]
    public void GlyphFault_RejectsAnInsaneQuestionMark(string kind)
    {
        UiGlyph g = kind switch
        {
            "x1equalsx0" => Sane with { X1 = Sane.X0 },
            "x1below" => Sane with { X1 = -1f },
            "uOver" => Sane with { U1 = 1.5f },
            "vNegative" => Sane with { V0 = -0.1f },
            "nan" => Sane with { U0 = float.NaN },
            "advanceZero" => Sane with { Advance = 0f },
            _ => Sane with { Advance = -2f }
        };

        Assert.NotNull(UiFontSetup.GlyphFault(g));
    }

    [Fact]
    public void TheLoadedImGuiIsTheAuditedVersion_AndTheRealFontPassesTheGuard()
    {
        lock (ImGuiTestGate.Gate)
        {
            Assert.Equal(UiFontSetup.AuditedImGuiVersion, ImGuiNET.ImGui.GetVersion());
        }

        (UiGlyphTable glyphs, _) = RealFont.Get();
        Assert.True(glyphs.TryGet(UiTextStyle.Body, '?', out UiGlyph q));
        Assert.Null(UiFontSetup.GlyphFault(q));
    }
}
