using System.Runtime.CompilerServices;
using Dream.Rendering.Ui;
using Dream.Tests.Integration;
using Dream.Tests.Support;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui;

public class UiBatchTests
{
    private static readonly UiColor White = UiColor.White;

    private static UiBatch NewBatch(float unit = 1f)
    {
        UiBatch batch = new(BlockGlyphs.Create());
        batch.SetPageSize(UiPage.Window, 100, 200);
        batch.SetPageSize(UiPage.Hud, 400, 400);
        batch.SetPageSize(UiPage.Font, 8, 8);
        batch.BeginFrame(640, 480, unit);
        return batch;
    }

    private static UiSprite Spr(UiPage page, int x, int y, int w, int h, int sl = 0, int st = 0, int sr = 0, int sb = 0, UiTile tile = UiTile.None) =>
        new(page, x, y, w, h, 0, 0, sl, st, sr, sb, tile, 1, 1);

    [Fact]
    public void VertexIs28Bytes() => Assert.Equal(28, Unsafe.SizeOf<UiVertex>());

    [Fact]
    public void Sprite_WritesTopLeftOriginUvsWithoutVFlip()
    {
        UiBatch b = NewBatch();
        b.Sprite(Spr(UiPage.Window, 10, 20, 30, 40), new UiRect(100, 50, 60, 80), White);

        ReadOnlySpan<UiVertex> v = b.Vertices;
        Assert.Equal(4, v.Length);
        // top-left, top-right, bottom-right, bottom-left
        Assert.Equal((100f, 50f), (v[0].X, v[0].Y));
        Assert.Equal((160f, 130f), (v[2].X, v[2].Y));
        Assert.Equal((10f / 100f, 20f / 200f), (v[0].U, v[0].V));
        Assert.Equal((40f / 100f, 20f / 200f), (v[1].U, v[1].V));
        Assert.Equal((40f / 100f, 60f / 200f), (v[2].U, v[2].V));
        Assert.Equal((10f / 100f, 60f / 200f), (v[3].U, v[3].V));
        Assert.True(v[0].V < v[3].V, "the top of the sprite must have the smaller V");
    }

    [Fact]
    public void SpriteClipped_UsesTheNormalizedSubRect()
    {
        UiBatch b = NewBatch();
        // bottom half of the sprite, as an HP gem fill would
        b.SpriteClipped(Spr(UiPage.Window, 10, 20, 30, 40), new UiRect(0, 20, 30, 20), new UiRect(0, 0.5f, 1, 0.5f), White);

        ReadOnlySpan<UiVertex> v = b.Vertices;
        Assert.Equal(10f / 100f, v[0].U, 6);
        Assert.Equal(40f / 100f, v[2].U, 6);
        Assert.Equal(40f / 200f, v[0].V, 6);
        Assert.Equal(60f / 200f, v[2].V, 6);
    }

    [Fact]
    public void SpriteClipped_EmptyOrOutOfRangeSourceDrawsNothing()
    {
        UiBatch b = NewBatch();
        b.SpriteClipped(Spr(UiPage.Window, 0, 0, 10, 10), new UiRect(0, 0, 10, 10), new UiRect(0.5f, 0, 0, 1), White);
        b.SpriteClipped(Spr(UiPage.Window, 0, 0, 10, 10), new UiRect(0, 0, 10, 10), new UiRect(2, 0, 1, 1), White);
        Assert.Equal(0, b.QuadCount);
    }

    [Fact]
    public void UnknownPageSize_DrawsNothing()
    {
        UiBatch b = NewBatch();
        b.Sprite(Spr(UiPage.Cursors, 0, 0, 10, 10), new UiRect(0, 0, 10, 10), White);
        Assert.Equal(0, b.QuadCount);
    }

    [Fact]
    public void NineSlice_MakesNineQuadsWithScaledBordersAndExactUvs()
    {
        UiBatch b = NewBatch(unit: 2f);
        UiSprite s = Spr(UiPage.Window, 10, 20, 40, 60, sl: 4, st: 6, sr: 8, sb: 10);
        b.NineSlice(s, new UiRect(100, 100, 200, 300), White);

        Assert.Equal(9, b.QuadCount);
        ReadOnlySpan<UiVertex> v = b.Vertices;
        // first quad is the top-left corner: 4*2 by 6*2 pixels, UVs cover the 4x6 texel corner
        Assert.Equal((100f, 100f), (v[0].X, v[0].Y));
        Assert.Equal((108f, 112f), (v[2].X, v[2].Y));
        Assert.Equal((10f / 100f, 20f / 200f), (v[0].U, v[0].V));
        Assert.Equal((14f / 100f, 26f / 200f), (v[2].U, v[2].V));
        // last quad is the bottom-right corner: ends on the destination corner and the sprite corner
        Assert.Equal((300f, 400f), (v[34].X, v[34].Y));
        Assert.Equal((50f / 100f, 80f / 200f), (v[34].U, v[34].V));
        // the centre quad stretches between the borders
        Assert.Equal((108f, 112f), (v[16].X, v[16].Y));
        Assert.Equal((284f, 380f), (v[18].X, v[18].Y));
        // quads tile the destination with no gaps: total area equals the destination area
        float area = 0f;
        for (int q = 0; q < 9; q++)
        {
            area += (v[q * 4 + 2].X - v[q * 4].X) * (v[q * 4 + 2].Y - v[q * 4].Y);
        }

        Assert.Equal(200f * 300f, area, 1);
    }

    [Fact]
    public void NineSlice_DestinationSmallerThanBordersShrinksThemTogether()
    {
        UiBatch b = NewBatch(unit: 2f);
        b.NineSlice(Spr(UiPage.Window, 0, 0, 40, 40, sl: 10, st: 10, sr: 10, sb: 10), new UiRect(0, 0, 20, 20), White);

        // borders would be 20+20 px in a 20 px rect; they halve and the centre disappears
        Assert.Equal(4, b.QuadCount);
        ReadOnlySpan<UiVertex> v = b.Vertices;
        Assert.Equal(10f, v[2].X, 3);
        Assert.Equal(10f, v[2].Y, 3);
    }

    [Fact]
    public void NineSlice_WithoutSliceIsOneSprite()
    {
        UiBatch b = NewBatch();
        b.NineSlice(Spr(UiPage.Window, 0, 0, 40, 40), new UiRect(0, 0, 80, 80), White);
        Assert.Equal(1, b.QuadCount);
    }

    [Fact]
    public void TileX_RepeatsAtUnitScaleAndTruncatesTheLastTile()
    {
        UiBatch b = NewBatch(unit: 2f);
        UiSprite s = Spr(UiPage.Hud, 40, 8, 10, 16, tile: UiTile.X);
        // tile step is 20 px; 70 px of width is 3 full tiles and half a tile; height stretches
        b.Tile(s, new UiRect(100, 10, 70, 48), White);

        Assert.Equal(4, b.QuadCount);
        ReadOnlySpan<UiVertex> v = b.Vertices;
        Assert.Equal((100f, 10f), (v[0].X, v[0].Y));
        Assert.Equal((120f, 58f), (v[2].X, v[2].Y));
        Assert.Equal((40f / 400f, 8f / 400f), (v[0].U, v[0].V));
        Assert.Equal((50f / 400f, 24f / 400f), (v[2].U, v[2].V));
        // last tile: 10 px wide, half of the sprite width
        Assert.Equal(160f, v[12].X);
        Assert.Equal(170f, v[14].X);
        Assert.Equal(45f / 400f, v[14].U, 6);
    }

    [Fact]
    public void TileXY_FillsBothAxes()
    {
        UiBatch b = NewBatch();
        b.Tile(Spr(UiPage.Hud, 0, 0, 10, 10, tile: UiTile.XY), new UiRect(0, 0, 25, 25), White);
        Assert.Equal(9, b.QuadCount);
    }

    [Fact]
    public void Tile_ThatCannotFitIsDroppedWhole()
    {
        UiBatch b = NewBatch();
        b.Tile(Spr(UiPage.Hud, 0, 0, 1, 1, tile: UiTile.XY), new UiRect(0, 0, 640, 480), White);
        Assert.Equal(0, b.QuadCount);
        Assert.Equal(1, b.DroppedQuads);
    }

    [Fact]
    public void Clip_DropsQuadsOutsideAndKeepsStraddlingOnesUnderScissor()
    {
        UiBatch b = NewBatch();
        UiSprite s = Spr(UiPage.Window, 0, 0, 10, 10);
        b.PushClip(new UiRect(100, 100, 50, 50));
        b.Sprite(s, new UiRect(0, 0, 10, 10), White);
        b.Sprite(s, new UiRect(90, 90, 20, 20), White);
        b.PopClip();
        b.Sprite(s, new UiRect(0, 0, 10, 10), White);

        Assert.Equal(2, b.QuadCount);
        Assert.Equal(2, b.CommandCount);
        Assert.Equal(new UiClipRect(100, 100, 150, 150), b.Commands[0].Clip);
        Assert.Equal(new UiClipRect(0, 0, 640, 480), b.Commands[1].Clip);
    }

    [Fact]
    public void Clip_NestedIntersectsAndPopRestores()
    {
        UiBatch b = NewBatch();
        UiSprite s = Spr(UiPage.Window, 0, 0, 10, 10);
        b.PushClip(new UiRect(10, 10, 100, 100));
        b.PushClip(new UiRect(50, 0, 100, 60));
        b.Sprite(s, new UiRect(0, 0, 640, 480), White);
        b.PopClip();
        b.Sprite(s, new UiRect(0, 0, 640, 480), White);
        b.PopClip();
        b.PopClip(); // extra pop is harmless

        Assert.Equal(new UiClipRect(50, 10, 110, 60), b.Commands[0].Clip);
        Assert.Equal(new UiClipRect(10, 10, 110, 110), b.Commands[1].Clip);
    }

    [Fact]
    public void Clip_DeeperThanTheStackStaysBalanced()
    {
        UiBatch b = NewBatch();
        for (int i = 0; i < UiBatch.MaxClipDepth + 5; i++)
        {
            b.PushClip(new UiRect(0, 0, 640 - i, 480));
        }

        for (int i = 0; i < UiBatch.MaxClipDepth + 5; i++)
        {
            b.PopClip();
        }

        b.Sprite(Spr(UiPage.Window, 0, 0, 10, 10), new UiRect(600, 0, 10, 10), White);
        Assert.Equal(new UiClipRect(0, 0, 640, 480), b.Commands[0].Clip);
    }

    [Fact]
    public void EmptyClip_DrawsNothing()
    {
        UiBatch b = NewBatch();
        b.PushClip(new UiRect(10, 10, 0, 0));
        b.Solid(new UiRect(0, 0, 100, 100), White);
        Assert.Equal(0, b.QuadCount);
    }

    [Fact]
    public void Commands_MergeBySameTextureAndClipAndSplitOnChange()
    {
        UiBatch b = NewBatch();
        UiSprite w = Spr(UiPage.Window, 0, 0, 10, 10);
        UiSprite h = Spr(UiPage.Hud, 0, 0, 10, 10);
        b.Sprite(w, new UiRect(0, 0, 10, 10), White);
        b.Sprite(w, new UiRect(10, 0, 10, 10), White);
        b.Solid(new UiRect(0, 20, 10, 10), White); // merges into the open window run
        b.Sprite(h, new UiRect(20, 0, 10, 10), White);
        b.Sprite(w, new UiRect(30, 0, 10, 10), White);

        Assert.Equal(5, b.QuadCount);
        Assert.Equal(3, b.CommandCount);
        Assert.Equal((UiPage.Window, 0, 18), (b.Commands[0].Page, b.Commands[0].FirstIndex, b.Commands[0].IndexCount));
        Assert.Equal((UiPage.Hud, 18, 6), (b.Commands[1].Page, b.Commands[1].FirstIndex, b.Commands[1].IndexCount));
        Assert.Equal((UiPage.Window, 24, 6), (b.Commands[2].Page, b.Commands[2].FirstIndex, b.Commands[2].IndexCount));
    }

    [Fact]
    public void LeadingSolidRun_AdoptsThePageOfTheNextTexturedQuad()
    {
        UiBatch b = NewBatch();
        b.Solid(new UiRect(0, 0, 10, 10), White);
        b.Sprite(Spr(UiPage.Window, 0, 0, 10, 10), new UiRect(20, 0, 10, 10), White);

        Assert.Equal(1, b.CommandCount);
        Assert.Equal(UiPage.Window, b.Commands[0].Page);
        Assert.Equal(12, b.Commands[0].IndexCount);
    }

    [Fact]
    public void Commands_PreserveDrawOrderWithinTheVertexArray()
    {
        UiBatch b = NewBatch();
        b.Sprite(Spr(UiPage.Window, 0, 0, 10, 10), new UiRect(0, 0, 10, 10), White);
        b.Sprite(Spr(UiPage.Hud, 0, 0, 10, 10), new UiRect(50, 0, 10, 10), White);

        Assert.Equal(0f, b.Vertices[0].X);
        Assert.Equal(50f, b.Vertices[4].X);
        Assert.Equal(b.Commands[0].FirstIndex + b.Commands[0].IndexCount, b.Commands[1].FirstIndex);
    }

    [Fact]
    public void ColoursPackAsRgbaBytesAndModesAreSet()
    {
        UiBatch b = NewBatch();
        b.Solid(new UiRect(0, 0, 4, 4), new UiColor(1, 2, 3, 4));
        b.MaskGlyph(Spr(UiPage.Hud, 0, 0, 12, 16), new UiRect(10, 0, 12, 16), new UiColor(10, 20, 30, 255), new UiColor(40, 50, 60, 200));

        ReadOnlySpan<UiVertex> v = b.Vertices;
        Assert.Equal(0x04030201u, v[0].Tint);
        Assert.Equal(UiVertexMode.Solid, v[0].Mode);
        Assert.Equal(UiVertexMode.Mask, v[4].Mode);
        Assert.Equal(0xFF1E140Au, v[4].Tint);
        Assert.Equal(0xC83C3228u, v[4].Rim);
    }

    [Fact]
    public void Text_AdvancesByGlyphAndMeasureAgrees()
    {
        UiBatch b = NewBatch();
        float advance = b.Text("abc", new Vector2(10, 20), UiTextStyle.Body, White);

        Assert.Equal(21f, advance);
        Assert.Equal(21f, b.MeasureText("abc", UiTextStyle.Body));
        Assert.Equal(42f, b.MeasureText("abc", UiTextStyle.Large));
        Assert.Equal(3, b.QuadCount);
        Assert.Equal(UiPage.Font, b.Commands[0].Page);
        Assert.Equal(10f, b.Vertices[0].X);
        Assert.Equal(17f, b.Vertices[4].X);
        // glyph box offsets are relative to the line top
        Assert.Equal(22f, b.Vertices[0].Y);
    }

    [Fact]
    public void Text_SpaceAdvancesWithoutAQuadAndTextScaleApplies()
    {
        UiBatch b = NewBatch();
        b.TextScale = 2f;
        float advance = b.Text("a b", new Vector2(0, 0), UiTextStyle.Body, White);

        Assert.Equal(42f, advance);
        Assert.Equal(2, b.QuadCount);
        Assert.Equal(28f, b.Vertices[4].X);
    }

    [Fact]
    public void Text_NonLatinControlAndSurrogatesDrawQuestionMark()
    {
        UiBatch b = NewBatch();
        UiBatch reference = NewBatch();
        reference.Text("???", Vector2.Zero, UiTextStyle.Body, White);

        b.Text("中\n\U0001F600", Vector2.Zero, UiTextStyle.Body, White);

        Assert.Equal(3, b.QuadCount);
        Assert.Equal(reference.Vertices.ToArray(), b.Vertices.ToArray());
        Assert.Equal(reference.MeasureText("???", UiTextStyle.Body), b.MeasureText("中\n\U0001F600", UiTextStyle.Body));
    }

    [Fact]
    public void Text_PercentAndBracesAreLiteral()
    {
        UiBatch b = NewBatch();
        b.Text("100%s{0}", Vector2.Zero, UiTextStyle.Body, White);
        Assert.Equal(8, b.QuadCount);
    }

    [Fact]
    public void Overflow_DropsExtraQuadsAndCountsThem()
    {
        UiBatch b = NewBatch();
        for (int i = 0; i < UiBatch.MaxQuads + 10; i++)
        {
            b.Solid(new UiRect(0, 0, 1, 1), White);
        }

        Assert.Equal(UiBatch.MaxQuads, b.QuadCount);
        Assert.Equal(10, b.DroppedQuads);
    }

    [Fact]
    public void Overflow_TooManyCommandsDropsInsteadOfThrowing()
    {
        UiBatch b = NewBatch();
        UiSprite w = Spr(UiPage.Window, 0, 0, 1, 1);
        UiSprite h = Spr(UiPage.Hud, 0, 0, 1, 1);
        for (int i = 0; i < UiBatch.MaxCommands + 5; i++)
        {
            b.Sprite(i % 2 == 0 ? w : h, new UiRect(0, 0, 1, 1), White);
        }

        Assert.Equal(UiBatch.MaxCommands, b.CommandCount);
        // Alternating pages: every other sprite past the cap would need a new command; the rest merge.
        Assert.Equal(3, b.DroppedQuads);
    }

    [Fact]
    public void QuadIndices_FormTwoTrianglesPerQuad()
    {
        ushort[] idx = UiBatch.CreateQuadIndices();
        Assert.Equal(UiBatch.MaxQuads * 6, idx.Length);
        Assert.Equal(new ushort[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }, idx[..12]);
        Assert.Equal((ushort)(UiBatch.MaxQuads * 4 - 1), idx[^1]);
    }

    [Fact]
    public void FullBatch_AllocatesNothingAfterWarmUp()
    {
        UiBatch b = NewBatch(unit: 2f);
        UiSprite w = Spr(UiPage.Window, 0, 0, 40, 40, sl: 4, st: 4, sr: 4, sb: 4);
        UiSprite t = Spr(UiPage.Hud, 0, 0, 10, 10, tile: UiTile.XY);
        UiSprite m = Spr(UiPage.Hud, 0, 0, 12, 16);
        string text = "Iron blade 100%";

        void Frame()
        {
            b.BeginFrame(640, 480, 2f);
            b.PushClip(new UiRect(0, 0, 600, 400));
            for (int i = 0; i < 60; i++)
            {
                b.NineSlice(w, new UiRect(i, i, 120, 90), White);
                b.Tile(t, new UiRect(0, 0, 100, 40), White);
                b.Sprite(w, new UiRect(i, 0, 10, 10), White);
                b.SpriteClipped(w, new UiRect(i, 0, 10, 5), new UiRect(0, 0, 1, 0.5f), White);
                b.MaskGlyph(m, new UiRect(i, 100, 12, 16), White, White);
                b.Solid(new UiRect(i, 200, 4, 4), White);
                b.Text(text, new Vector2(i, 300), UiTextStyle.Large, White);
                b.MeasureText(text, UiTextStyle.Body);
            }

            b.PopClip();
        }

        long allocated = Dream.Tests.Utilities.AllocationAssert.MeasureBytesPerIteration(Frame);

        Assert.True(b.QuadCount > 1000);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void UiNamespaceOutsideGlFolder_DoesNotReferenceOpenTkGraphics()
    {
        string root = Path.Combine(RepoRoot.Find(), "dream", "Rendering", "Ui");
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "Gl" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            Assert.DoesNotContain("OpenTK.Graphics", source);
        }
    }

    [Fact]
    public void RenderDispose_IncludesTheClassicHudStepBeforeTheFontTexture()
    {
        string lifetime = File.ReadAllText(Path.Combine(RepoRoot.Find(), "dream", "Rendering", "Core", "Render.Lifetime.cs"));
        int hud = lifetime.IndexOf("RunDisposeStep(DisposeClassicHud, ref failures)", StringComparison.Ordinal);
        int font = lifetime.IndexOf("RunDisposeStep(DeleteFontTexture, ref failures)", StringComparison.Ordinal);
        int imgui = lifetime.IndexOf("RunDisposeStep(DestroyImGuiContext, ref failures)", StringComparison.Ordinal);
        Assert.True(hud >= 0 && hud < font && font < imgui);

        string hudSource = File.ReadAllText(Path.Combine(RepoRoot.Find(), "dream", "Rendering", "Core", "Render.ClassicHud.cs"));
        Assert.Contains("atlas?.Dispose()", hudSource);
        Assert.Contains("gl?.Dispose()", hudSource);
    }
}
