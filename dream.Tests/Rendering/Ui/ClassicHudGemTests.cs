using System.Text.Json.Nodes;
using Dream.Assets;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Tests.Rendering.Ui.Panels;
using Dream.Tests.Support;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Dream.Tests.Rendering.Ui;

// Classic HUD iteration 2, WS3: the liquid gem runtime. The Gem and Additive vertex modes in UiBatch, their
// mirror in the software compositor, the eased levels and timing in ClassicHudSurface, and the bar panel's
// use of them (bezel, gem, talons, spill, numerals, ManaGemFill). Everything here works with today's art
// (the old top-down gradient masks, no setting, spill or numerals) and with a table that adds the new art.
public sealed class ClassicHudGemTests
{
    private static readonly UiColor White = UiColor.White;

    // ---- fixtures ----

    private static UiSpriteTable? s_real;

    private static UiSpriteTable RealTable()
    {
        if (s_real is null)
        {
            Assert.True(UiSpriteTable.TryLoad(AssetPaths.UiDirectory, out UiSpriteTable? table, out string error), error);
            s_real = table;
        }

        return s_real!;
    }

    private static readonly (UiPage Page, string Json, string Png)[] PageFiles =
    [
        (UiPage.Window, "window/window.json", "window/window.png"),
        (UiPage.Hud, "hud/hud.json", "hud/hud.png"),
        (UiPage.Cursors, "hud/cursors.json", "hud/cursors.png"),
        (UiPage.ItemsCommon, "items/items.json", "items/items.png"),
        (UiPage.Digits, "fx/dmg_digits_x4.json", "fx/dmg_digits_x4.png"),
    ];

    // The real tables with the iteration 2 bar sprites added on the Hud page (bezels, spill, numerals) and
    // optionally the gem masks and glints removed. Rects are inside the real image; the pixels they point at
    // belong to other sprites, so this is for layout and command tests, not for pixels.
    private static UiSpriteTable ModifiedTable(bool addNewArt, bool removeMaskAndGlints = false)
    {
        UiSpriteTable table = new();
        foreach ((UiPage page, string jsonRel, string pngRel) in PageFiles)
        {
            string json = File.ReadAllText(Path.Combine(AssetPaths.UiDirectory, jsonRel));
            ImageInfo info = Image.Identify(Path.Combine(AssetPaths.UiDirectory, pngRel));
            if (page == UiPage.Hud)
            {
                JsonObject root = JsonNode.Parse(json)!.AsObject();
                JsonObject sprites = root["sprites"]!.AsObject();
                if (removeMaskAndGlints)
                {
                    foreach (string name in new[] { "gem.hp.mask", "gem.hp.spec", "gem.mp.mask", "gem.mp.spec" })
                    {
                        sprites.Remove(name);
                    }
                }

                if (addNewArt)
                {
                    for (int d = 0; d < 10; d++)
                    {
                        sprites.Add("hudnum." + d, Rect(36 * d, 1000, 32, 48));
                    }

                    sprites.Add("hudnum.slash", Rect(360, 1000, 32, 48));
                    sprites.Add("gem.setting.left", Rect(400, 1000, 248, 224));
                    sprites.Add("gem.setting.right", Rect(650, 1000, 248, 224));
                    sprites.Add("gem.hp.spill", Rect(900, 1000, 512, 256));
                    sprites.Add("gem.mp.spill", Rect(0, 1100, 512, 256));
                }

                json = root.ToJsonString();
            }

            table.AddPage(page, json, info.Width, info.Height);
        }

        return table;
    }

    private static JsonObject Rect(int x, int y, int w, int h) => new()
    {
        ["x"] = x,
        ["y"] = y,
        ["w"] = w,
        ["h"] = h,
        ["pivot"] = new JsonArray(0, 0),
        ["slice"] = null,
        ["tile"] = "none",
        ["meta"] = new JsonObject(),
    };

    // A 64 x 48 Hud page with a 16 x 16 gem family per kind: full (200,40,40), empty (20,20,200), a level map,
    // glints. levelMapArt adds the two setting sprites (the marker of the baked maps) and flips the mask to the
    // baked convention (R 1 at the top). glints paints the glint sprite opaque white instead of clear.
    private static (UiSpriteTable Table, UiTexturePixels Page) SyntheticGems(bool levelMapArt, bool glints = false)
    {
        const int w = 64;
        const int h = 48;
        byte[] px = new byte[w * h * 4];
        for (int kind = 0; kind < 2; kind++)
        {
            int oy = kind * 16;
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    Put(px, w, x, oy + y, 200, 40, 40, 255);
                    Put(px, w, 16 + x, oy + y, 20, 20, 200, 255);
                    byte ramp = (byte)Math.Round(255.0 * y / 15.0);
                    byte level = levelMapArt ? (byte)(255 - ramp) : ramp;
                    Put(px, w, 32 + x, oy + y, level, levelMapArt ? (byte)0 : level, levelMapArt ? (byte)0 : level, 255);
                    if (glints)
                    {
                        Put(px, w, 48 + x, oy + y, 255, 255, 255, 255);
                    }
                }
            }
        }

        UiSpriteTable table = new();
        string family(string k, int oy) =>
            $"\"gem.{k}.full\":{{\"x\":0,\"y\":{oy},\"w\":16,\"h\":16}},\"gem.{k}.empty\":{{\"x\":16,\"y\":{oy},\"w\":16,\"h\":16}},"
            + $"\"gem.{k}.mask\":{{\"x\":32,\"y\":{oy},\"w\":16,\"h\":16}},\"gem.{k}.spec\":{{\"x\":48,\"y\":{oy},\"w\":16,\"h\":16}}";
        string markers = levelMapArt
            ? ",\"gem.setting.left\":{\"x\":0,\"y\":40,\"w\":4,\"h\":4},\"gem.setting.right\":{\"x\":8,\"y\":40,\"w\":4,\"h\":4}"
            : string.Empty;
        table.AddPage(UiPage.Hud, $"{{\"size\":[{w},{h}],\"sprites\":{{{family("hp", 0)},{family("mp", 16)}{markers}}}}}", w, h);
        return (table, new UiTexturePixels(w, h, px));
    }

    private static void Put(byte[] px, int width, int x, int y, byte r, byte g, byte b, byte a)
    {
        int i = (y * width + x) * 4;
        px[i] = r;
        px[i + 1] = g;
        px[i + 2] = b;
        px[i + 3] = a;
    }

    private static UiBatch NewBatch(UiSpriteTable table, int w = 200, int h = 200)
    {
        UiBatch batch = new(BlockGlyphs.Create());
        batch.SetSpriteTable(table);
        batch.BeginFrame(w, h, 1f);
        return batch;
    }

    // Draws one gem into a fresh compositor (the synthetic page) and returns it.
    private static UiSoftwareCompositor Render(UiSpriteTable table, UiTexturePixels page, Action<UiBatch> draw, int size = 200)
    {
        UiBatch batch = NewBatch(table, size, size);
        draw(batch);
        UiSoftwareCompositor compositor = new(size, size);
        compositor.SetPage(UiPage.Hud, page);
        compositor.Draw(batch);
        return compositor;
    }

    private static readonly UiRect GemRectPx = new(20, 20, 160, 160);

    // A pixel at a local position (0..1) inside GemRectPx.
    private static Rgba32 At(UiSoftwareCompositor c, float lx, float ly) =>
        c.GetRgba((int)(GemRectPx.X + lx * GemRectPx.W), (int)(GemRectPx.Y + ly * GemRectPx.H));

    private static void AssertColor(Rgba32 actual, int r, int g, int b, int tolerance = 3, string? label = null)
    {
        Assert.True(
            Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance,
            $"{label}: expected ({r},{g},{b}) +/- {tolerance} but got ({actual.R},{actual.G},{actual.B})");
    }

    private static readonly (int R, int G, int B) Full = (200, 40, 40);
    private static readonly (int R, int G, int B) Empty = (20, 20, 200);

    private static void DrawGem(UiBatch b, UiSpriteTable t, float fill, float phase = 0f, float pulse = 0f, float sweep = 0f, UiGemKind kind = UiGemKind.Health)
    {
        UiSprite full = t.Get(kind == UiGemKind.Health ? "gem.hp.full" : "gem.mp.full");
        b.Gem(full, GemRectPx, kind, fill, phase, pulse, sweep, White);
    }

    // ---- vertex modes and the batch ----

    [Fact]
    public void TheNewVertexModes_AreThreeAndFour_InTheSameWordAsTheOldOnes()
    {
        Assert.Equal(0u, UiVertexMode.Color);
        Assert.Equal(1u, UiVertexMode.Mask);
        Assert.Equal(2u, UiVertexMode.Solid);
        Assert.Equal(3u, UiVertexMode.Gem);
        Assert.Equal(4u, UiVertexMode.Additive);
        Assert.Equal(0xFFu, UiVertexMode.BaseMask);
        Assert.Equal(UiVertexMode.Gem, UiVertexMode.Base(UiVertexMode.Gem | (1u << UiVertexMode.GemKindShift) | UiVertexMode.GemLegacyMask));
    }

    [Theory]
    [InlineData(UiGemKind.Health, false)]
    [InlineData(UiGemKind.Mana, false)]
    [InlineData(UiGemKind.Health, true)]
    [InlineData(UiGemKind.Mana, true)]
    public void Gem_WritesOneGemQuad_WithTheLevelPhasePulseAndSweepInTheRimBytes(UiGemKind kind, bool levelMapArt)
    {
        (UiSpriteTable table, _) = SyntheticGems(levelMapArt);
        UiBatch b = NewBatch(table);

        DrawGem(b, table, 0.5f, 0.25f, 1f, 0.75f, kind);

        Assert.Equal(1, b.QuadCount);
        UiVertex v = b.Vertices[0];
        Assert.Equal(UiVertexMode.Gem, UiVertexMode.Base(v.Mode));
        Assert.Equal((uint)kind, (v.Mode >> UiVertexMode.GemKindShift) & 1u);
        Assert.Equal(!levelMapArt, (v.Mode & UiVertexMode.GemLegacyMask) != 0);
        // R fill, G phase, B pulse, A sweep, one byte each.
        Assert.Equal(128u, v.Rim & 0xFF);
        Assert.Equal(64u, (v.Rim >> 8) & 0xFF);
        Assert.Equal(255u, (v.Rim >> 16) & 0xFF);
        Assert.Equal(191u, (v.Rim >> 24) & 0xFF);
        // The UVs cover the full stone and every vertex of the quad carries the same word.
        UiSprite full = table.Get(kind == UiGemKind.Health ? "gem.hp.full" : "gem.mp.full");
        Assert.Equal(full.X / 64f, v.U, 6);
        Assert.Equal(full.Y / 48f, v.V, 6);
        Assert.Equal((full.X + full.W) / 64f, b.Vertices[2].U, 6);
        for (int i = 1; i < 4; i++)
        {
            Assert.Equal((v.Mode, v.Rim, v.Tint), (b.Vertices[i].Mode, b.Vertices[i].Rim, b.Vertices[i].Tint));
        }
    }

    [Fact]
    public void Gem_ClampsTheRimValues_AndTreatsNaNAsZero()
    {
        (UiSpriteTable table, _) = SyntheticGems(levelMapArt: true);
        UiBatch b = NewBatch(table);

        DrawGem(b, table, 7f, -3f, float.NaN, float.PositiveInfinity);

        uint rim = b.Vertices[0].Rim;
        Assert.Equal((255u, 0u, 0u, 255u), (rim & 0xFF, (rim >> 8) & 0xFF, (rim >> 16) & 0xFF, (rim >> 24) & 0xFF));
    }

    [Fact]
    public void Gem_OffsetsAreTheSpriteDeltasOverThePageSize_ForBothKinds()
    {
        UiSpriteTable table = RealTable();
        UiBatch b = NewBatch(table);
        table.TryGetPageSize(UiPage.Hud, out int pw, out int ph);

        Assert.True(b.IsGemReady(UiGemKind.Health));
        Assert.True(b.IsGemReady(UiGemKind.Mana));
        ReadOnlySpan<float> offsets = b.GemOffsets;
        Assert.Equal(12, offsets.Length);
        (string Full, string[] Others)[] families =
        [
            ("gem.hp.full", ["gem.hp.empty", "gem.hp.mask", "gem.hp.spec"]),
            ("gem.mp.full", ["gem.mp.empty", "gem.mp.mask", "gem.mp.spec"]),
        ];
        for (int k = 0; k < 2; k++)
        {
            UiSprite full = table.Get(families[k].Full);
            for (int i = 0; i < 3; i++)
            {
                UiSprite other = table.Get(families[k].Others[i]);
                Assert.Equal((other.X - full.X) / (float)pw, offsets[(k * 3 + i) * 2], 7);
                Assert.Equal((other.Y - full.Y) / (float)ph, offsets[(k * 3 + i) * 2 + 1], 7);
            }

            Assert.Equal(full.X / (float)pw, b.GemRects[k * 4], 7);
            Assert.Equal(full.W / (float)pw, b.GemRects[k * 4 + 2], 7);
        }
    }

    [Fact]
    public void Gem_WhenTheFamilyIsIncomplete_DrawsThePlainSprite()
    {
        UiSpriteTable table = ModifiedTable(addNewArt: false, removeMaskAndGlints: true);
        UiBatch b = NewBatch(table);

        Assert.False(b.IsGemReady(UiGemKind.Health));
        Assert.False(UiBatch.TryGetGemFamily(table, UiGemKind.Health, out _, out _, out _, out _));
        b.Gem(table.Get("gem.hp.full"), new UiRect(0, 0, 50, 50), UiGemKind.Health, 0.5f, 0f, 0f, 0f, White);

        Assert.Equal(1, b.QuadCount);
        Assert.Equal(UiVertexMode.Color, b.Vertices[0].Mode);
    }

    [Fact]
    public void Gem_GivenSomeOtherSprite_DrawsThatSpritePlain()
    {
        (UiSpriteTable table, _) = SyntheticGems(levelMapArt: false);
        UiBatch b = NewBatch(table);

        // Not the table's gem.hp.full: the offsets would point at the wrong neighbours.
        b.Gem(table.Get("gem.hp.empty"), GemRectPx, UiGemKind.Health, 0.5f, 0f, 0f, 0f, White);

        Assert.Equal(UiVertexMode.Color, b.Vertices[0].Mode);
    }

    [Fact]
    public void Additive_WritesTheAdditiveModeQuad_WithTheTint()
    {
        (UiSpriteTable table, _) = SyntheticGems(levelMapArt: false);
        UiBatch b = NewBatch(table);

        b.Additive(table.Get("gem.hp.spec"), new UiRect(10, 10, 32, 16), new UiColor(255, 255, 255, 128));

        Assert.Equal(1, b.QuadCount);
        UiVertex v = b.Vertices[0];
        Assert.Equal(UiVertexMode.Additive, v.Mode);
        Assert.Equal(UiBatch.Pack(new UiColor(255, 255, 255, 128)), v.Tint);
        Assert.Equal(0u, v.Rim);
    }

    [Fact]
    public void GemAdditiveAndPlainHudQuads_MergeIntoOneDrawCommand()
    {
        (UiSpriteTable table, _) = SyntheticGems(levelMapArt: true);
        UiBatch b = NewBatch(table);
        UiSprite plate = table.Get("gem.hp.empty");

        b.Sprite(plate, new UiRect(0, 0, 40, 40), White);
        DrawGem(b, table, 0.4f);
        b.Additive(table.Get("gem.hp.spec"), new UiRect(0, 0, 90, 40), new UiColor(255, 255, 255, 90));
        b.Sprite(plate, new UiRect(100, 0, 40, 40), White);
        DrawGem(b, table, 1f, kind: UiGemKind.Mana);

        Assert.Equal(5, b.QuadCount);
        UiDrawCommand cmd = Assert.Single(b.Commands.ToArray());
        Assert.Equal(UiPage.Hud, cmd.Page);
        Assert.Equal(5 * 6, cmd.IndexCount);
    }

    [Fact]
    public void ABarWithTheGems_StaysInsideTheDrawBudget_AndTheGemQuadsAreInTheHudCommand()
    {
        foreach (bool newArt in new[] { false, true })
        {
            UiSpriteTable table = newArt ? ModifiedTable(addNewArt: true) : RealTable();
            foreach ((int w, int h) in new[] { (1280, 720), (1920, 1080), (1440, 1024) })
            {
                UiLayout layout = UiLayout.Create(w, h, 0f);
                UiBatch batch = new(BlockGlyphs.Create());
                batch.SetSpriteTable(table);
                batch.BeginFrame(in layout);
                HudView view = BarView(0.6f);
                UiInputRouter router = new();
                router.BeginFrame(new UiPointer(Vector2.Zero, false, false, false));
                ClassicHud.Build(batch, router, view, in layout, table, new NoHudActions());
                router.EndFrame();

                int gemQuads = 0;
                int additiveQuads = 0;
                ReadOnlySpan<UiVertex> vertices = batch.Vertices;
                for (int q = 0; q < batch.QuadCount; q++)
                {
                    uint mode = UiVertexMode.Base(vertices[q * 4].Mode);
                    if (mode == UiVertexMode.Gem || mode == UiVertexMode.Additive)
                    {
                        gemQuads += mode == UiVertexMode.Gem ? 1 : 0;
                        additiveQuads += mode == UiVertexMode.Additive ? 1 : 0;
                        UiDrawCommand owner = batch.Commands.ToArray().Single(c => q * 6 >= c.FirstIndex && q * 6 < c.FirstIndex + c.IndexCount);
                        Assert.Equal(UiPage.Hud, owner.Page);
                    }
                }

                // Both gems are one quad each; the two spills only exist with the new art.
                Assert.Equal(2, gemQuads);
                Assert.Equal(newArt ? 2 : 0, additiveQuads);
                Assert.True(
                    batch.CommandCount <= ClassicHudDrawCommandTests.PlayBudget,
                    $"{batch.CommandCount} commands at {w}x{h} (new art {newArt}), budget {ClassicHudDrawCommandTests.PlayBudget}");
                Assert.Equal(0, batch.DroppedQuads);
            }
        }
    }

    private sealed class NoHudActions : IHudActions
    {
        public void Inventory(InventoryHudAction action) { }

        public void CloseInventory() { }
    }

    private static HudView BarView(float level, int hp = 73, int maxHp = 120)
    {
        HudView v = new()
        {
            Status = new HudStatusSnapshot(hp, maxHp, Dream.Simulation.Players.PlayerInteractionMode.Combat, 5, 9, 12, 1.2f, 14, 7, 40, 100, 10, 1f, 10, 3, false, 0),
            HealthLevel = level,
            ManaLevel = StatusBarPanel.ManaGemFill,
            AnimationSeconds = 2.2f,
        };
        return v;
    }

    // ---- compositor parity ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compositor_FillZero_IsAllGlass_FillOne_IsAllLiquid(bool levelMapArt)
    {
        (UiSpriteTable table, UiTexturePixels page) = SyntheticGems(levelMapArt);

        UiSoftwareCompositor empty = Render(table, page, b => DrawGem(b, table, 0f));
        UiSoftwareCompositor full = Render(table, page, b => DrawGem(b, table, 1f));

        foreach (float ly in new[] { 0.06f, 0.3f, 0.5f, 0.7f, 0.94f })
        {
            AssertColor(At(empty, 0.5f, ly), Empty.R, Empty.G, Empty.B, label: $"fill 0 at {ly}");
            AssertColor(At(full, 0.5f, ly), Full.R, Full.G, Full.B, label: $"fill 1 at {ly}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compositor_FillHalf_IsLiquidBelowTheLevelAndGlassAbove_LikeTheOldClip(bool levelMapArt)
    {
        (UiSpriteTable table, UiTexturePixels page) = SyntheticGems(levelMapArt);

        UiSoftwareCompositor half = Render(table, page, b => DrawGem(b, table, 0.5f));

        // The same picture for the old gradient mask and for a baked level map: the liquid fills from the
        // bottom, as the clipped full stone did.
        AssertColor(At(half, 0.5f, 0.8f), Full.R, Full.G, Full.B, label: "below the level");
        AssertColor(At(half, 0.5f, 0.95f), Full.R, Full.G, Full.B, label: "bottom");
        AssertColor(At(half, 0.5f, 0.2f), Empty.R, Empty.G, Empty.B, label: "above the level");
        AssertColor(At(half, 0.5f, 0.05f), Empty.R, Empty.G, Empty.B, label: "top");
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    public void Compositor_TheLiquidSurface_SitsAtTheFillLevelAndRises(float fill)
    {
        (UiSpriteTable table, UiTexturePixels page) = SyntheticGems(levelMapArt: true);

        UiSoftwareCompositor c = Render(table, page, b => DrawGem(b, table, fill));

        // Local y of the surface is 1 - fill (the top is 0).
        float surface = 1f - fill;
        AssertColor(At(c, 0.5f, surface + 0.12f), Full.R, Full.G, Full.B, label: "just below the surface");
        AssertColor(At(c, 0.5f, surface - 0.12f), Empty.R, Empty.G, Empty.B, label: "just above the surface");
    }

    [Fact]
    public void Compositor_AGradientMask_GivesAStraightMeniscusLine_ThatTheBakedMapCanSwitchOff()
    {
        (UiSpriteTable legacyTable, UiTexturePixels legacyPage) = SyntheticGems(levelMapArt: false);
        (UiSpriteTable bakedTable, UiTexturePixels bakedPage) = SyntheticGems(levelMapArt: true);

        UiSoftwareCompositor legacy = Render(legacyTable, legacyPage, b => DrawGem(b, legacyTable, 0.5f));
        UiSoftwareCompositor baked = Render(bakedTable, bakedPage, b => DrawGem(b, bakedTable, 0.5f));

        // At the surface row the old gradient mask has no weight channel, so the line is full strength and
        // the liquid colour is lifted; the baked map here carries weight 0 and stays as it is.
        Rgba32 legacyLine = At(legacy, 0.5f, 0.5f);
        Rgba32 bakedLine = At(baked, 0.5f, 0.5f);
        Assert.True(legacyLine.R > bakedLine.R || legacyLine.G > bakedLine.G, $"legacy {legacyLine} baked {bakedLine}");
    }

    [Fact]
    public void Compositor_ThePulse_BrightensTheLiquidOnly()
    {
        (UiSpriteTable table, UiTexturePixels page) = SyntheticGems(levelMapArt: true);

        UiSoftwareCompositor calm = Render(table, page, b => DrawGem(b, table, 0.5f));
        UiSoftwareCompositor pulsing = Render(table, page, b => DrawGem(b, table, 0.5f, pulse: 0.5f));

        // 1 + 0.35 * 0.5 on the liquid; the glass above is untouched.
        AssertColor(At(pulsing, 0.5f, 0.85f), (int)(Full.R * 1.175f), (int)(Full.G * 1.175f), (int)(Full.B * 1.175f), tolerance: 3, label: "liquid");
        AssertColor(At(pulsing, 0.5f, 0.15f), Empty.R, Empty.G, Empty.B, label: "glass");
        Assert.True(At(pulsing, 0.5f, 0.85f).R > At(calm, 0.5f, 0.85f).R);
    }

    [Fact]
    public void Compositor_TheGlintSweep_LightsTheGlintsOnlyWhereTheBandIs()
    {
        (UiSpriteTable table, UiTexturePixels page) = SyntheticGems(levelMapArt: true, glints: true);

        UiSoftwareCompositor idle = Render(table, page, b => DrawGem(b, table, 1f, sweep: 0f));
        UiSoftwareCompositor sweeping = Render(table, page, b => DrawGem(b, table, 1f, sweep: 0.5f));

        // Band centre at sweep 0.5 is the diagonal x + y = 1: the middle of the stone is lit to white.
        AssertColor(At(idle, 0.5f, 0.5f), Full.R, Full.G, Full.B, label: "idle centre");
        Assert.True(At(sweeping, 0.5f, 0.5f).G > 200, $"centre {At(sweeping, 0.5f, 0.5f)}");
        // A corner far from the band is unchanged.
        AssertColor(At(sweeping, 0.06f, 0.06f), Full.R, Full.G, Full.B, label: "far corner");
        AssertColor(At(sweeping, 0.94f, 0.94f), Full.R, Full.G, Full.B, label: "far corner 2");
    }

    [Fact]
    public void Compositor_Additive_AddsToWhatIsBelow_AndClampsLikeTheFramebuffer()
    {
        const int w = 8;
        byte[] px = new byte[w * w * 4];
        for (int y = 0; y < w; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Put(px, w, x, y, 100, 50, 0, 255);
            }
        }

        UiSpriteTable table = new();
        table.AddPage(UiPage.Hud, "{\"size\":[8,8],\"sprites\":{\"light\":{\"x\":0,\"y\":0,\"w\":8,\"h\":8}}}", w, w);
        UiBatch b = NewBatch(table, 16, 16);
        UiSprite light = table.Get("light");
        UiRect rect = new(0, 0, 16, 16);
        b.Additive(light, rect, White);
        b.Additive(light, rect, White);
        b.Additive(light, rect, new UiColor(255, 255, 255, 128));
        b.Additive(light, rect, White);
        UiSoftwareCompositor c = new(16, 16);
        c.SetPage(UiPage.Hud, new UiTexturePixels(w, w, px));

        // Four draws, then it clamps: 100+100+50+100 -> 255 in red, 50+50+25+50 = 175 in green, blue stays 0.
        c.Draw(b);

        Rgba32 p = c.GetRgba(8, 8);
        Assert.Equal((255, 175, 0), (p.R, p.G, p.B));
    }

    [Fact]
    public void Compositor_Additive_ScalesByTheTintAlpha()
    {
        UiSpriteTable table = new();
        table.AddPage(UiPage.Hud, "{\"size\":[2,2],\"sprites\":{\"light\":{\"x\":0,\"y\":0,\"w\":2,\"h\":2}}}", 2, 2);
        byte[] px = new byte[2 * 2 * 4];
        for (int i = 0; i < 4; i++)
        {
            Put(px, 2, i % 2, i / 2, 255, 255, 255, 255);
        }

        UiBatch b = NewBatch(table, 4, 4);
        b.Additive(table.Get("light"), new UiRect(0, 0, 4, 4), new UiColor(255, 255, 255, 64));
        UiSoftwareCompositor c = new(4, 4);
        c.SetPage(UiPage.Hud, new UiTexturePixels(2, 2, px));
        c.Draw(b);

        // 64/255 added to 0.
        Assert.Equal(64, c.GetRgba(2, 2).R);
    }

    // ---- real art ----

    private static UiTexturePixels? s_hudPage;

    private static UiTexturePixels HudPage() =>
        s_hudPage ??= UiTexturePixels.Load(Path.Combine(AssetPaths.UiDirectory, UiSpriteTable.PagePngPath(UiPage.Hud)!));

    private static UiSoftwareCompositor RenderRealGem(float fill, UiGemKind kind = UiGemKind.Health)
    {
        UiSpriteTable table = RealTable();
        UiBatch batch = NewBatch(table, 128, 128);
        UiSprite full = table.Get(kind == UiGemKind.Health ? "gem.hp.full" : "gem.mp.full");
        batch.Gem(full, new UiRect(14, 14, 100, 100), kind, fill, 0f, 0f, 0f, White);
        UiSoftwareCompositor c = new(128, 128);
        c.SetPage(UiPage.Hud, HudPage());
        c.ClearBackdrop();
        c.Draw(batch);
        return c;
    }

    private static Rgba32 AtReal(UiSoftwareCompositor c, float lx, float ly) => c.GetRgba((int)(14 + lx * 100), (int)(14 + ly * 100));

    [Theory]
    [InlineData(UiGemKind.Health)]
    [InlineData(UiGemKind.Mana)]
    public void RealArt_TheLiquidRisesWithTheFill_FromTheBottom(UiGemKind kind)
    {
        UiSoftwareCompositor empty = RenderRealGem(0f, kind);
        UiSoftwareCompositor full = RenderRealGem(1f, kind);

        // The pixels where the full and the empty stone differ clearly are the ones that show the level.
        List<(int X, int Y)> area = [];
        for (int y = 14; y < 114; y++)
        {
            for (int x = 14; x < 114; x++)
            {
                Rgba32 a = empty.GetRgba(x, y);
                Rgba32 b = full.GetRgba(x, y);
                if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 90)
                {
                    area.Add((x, y));
                }
            }
        }

        Assert.True(area.Count > 2000, $"only {area.Count} distinguishable pixels");
        double previous = -1;
        foreach (float fill in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            UiSoftwareCompositor c = RenderRealGem(fill, kind);
            int liquid = area.Count(p =>
            {
                Rgba32 px = c.GetRgba(p.X, p.Y);
                return Distance(px, full.GetRgba(p.X, p.Y)) < Distance(px, empty.GetRgba(p.X, p.Y));
            });
            double covered = liquid / (double)area.Count;
            Assert.True(covered > previous, $"coverage {covered} at {fill} did not rise above {previous}");
            previous = covered;

            // With the old straight-gradient masks the surface is a horizontal line at 1 - level (the level
            // is stretched a little past 0..1 at both ends): everything well below it is liquid, everything
            // well above is glass, exactly where the clipped stone put them.
            if (UiBatch.TryGetGemFamily(RealTable(), kind, out _, out _, out _, out _) && NewBatch(RealTable()).GemMasksAreLegacy)
            {
                float surface = 1f - (-0.03f + 1.06f * fill);
                int judged = 0;
                int wrong = 0;
                foreach ((int x, int y) in area)
                {
                    float local = (y + 0.5f - 14f) / 100f;
                    if (MathF.Abs(local - surface) < 0.05f)
                    {
                        continue;
                    }

                    judged++;
                    Rgba32 px = c.GetRgba(x, y);
                    bool isLiquid = Distance(px, full.GetRgba(x, y)) < Distance(px, empty.GetRgba(x, y));
                    wrong += isLiquid == local > surface ? 0 : 1;
                }

                Assert.True(wrong <= judged * 0.02, $"{wrong} of {judged} pixels are on the wrong side of the surface at fill {fill}");
            }
        }

        // The bottom fills first: at a quarter the lowest area pixels are liquid and the highest are not.
        UiSoftwareCompositor quarter = RenderRealGem(0.25f, kind);
        (int X, int Y) lowest = area.OrderByDescending(p => p.Y).First();
        (int X, int Y) highest = area.OrderBy(p => p.Y).First();
        Assert.True(Distance(quarter.GetRgba(lowest.X, lowest.Y), full.GetRgba(lowest.X, lowest.Y)) < 40);
        Assert.True(Distance(quarter.GetRgba(highest.X, highest.Y), empty.GetRgba(highest.X, highest.Y)) < 40);
    }

    private static int Distance(Rgba32 a, Rgba32 b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    [Fact]
    public void RealArt_TheMaskOrientation_MatchesWhichArtGenerationTheBatchAssumes()
    {
        // The batch reads the mask as the baked level map (R 0 at the bottom of the stone, 1 at the top) only
        // when the art ships the gem setting; otherwise as the old top-down gradient (R 0 at the top). This
        // fails when the art changes generation without the runtime following, in either direction.
        UiSpriteTable table = RealTable();
        UiBatch batch = NewBatch(table);
        UiTexturePixels page = HudPage();
        foreach (string maskName in new[] { "gem.hp.mask", "gem.mp.mask" })
        {
            UiSprite mask = table.Get(maskName);
            int cx = mask.X + mask.W / 2;
            int top = page.Rgba[((mask.Y + (int)(mask.H * 0.12f)) * page.Width + cx) * 4];
            int bottom = page.Rgba[((mask.Y + (int)(mask.H * 0.88f)) * page.Width + cx) * 4];
            bool topDown = top < bottom;
            Assert.True(
                topDown == batch.GemMasksAreLegacy,
                $"{maskName}: R is {top} near the top and {bottom} near the bottom, but the batch assumes "
                + (batch.GemMasksAreLegacy ? "the old top-down gradient" : "baked level maps (R 1 at the top)"));
        }
    }

    [Fact]
    public void TheBatchReadsTheMasksAsBakedMaps_OnlyOnceTheGemSettingShips()
    {
        UiSpriteTable table = RealTable();

        // The marker the integrator relies on: the gem setting sprites ship with the baked level maps.
        bool shipsSetting = table.Contains(UiSpriteNames.GemSettingLeft) && table.Contains(UiSpriteNames.GemSettingRight);
        Assert.Equal(!shipsSetting, NewBatch(table).GemMasksAreLegacy);
        Assert.True(NewBatch(ModifiedTable(addNewArt: false)).GemMasksAreLegacy);
        Assert.False(NewBatch(ModifiedTable(addNewArt: true)).GemMasksAreLegacy);
    }

    [Fact]
    public void BarPreview_ThreeLevelsOfBothGems_RenderWithTheRealArt()
    {
        // The bar at fill 0, .5 and 1 through the panel and the compositor (PNGs in artifacts/hud-preview when
        // DREAM_HUD_PREVIEW=1). The liquid shows in the gem's lower half first and the mana gem follows its level.
        UiLayout layout = UiLayout.Create(1280, 720, 0f);
        UiRect hp = layout.FromUnits(layout.LeftGroup, 98f, -2f, 50f, 50f);
        UiRect mp = layout.FromUnits(layout.RightGroup, 492f - 441f, -2f, 50f, 50f);
        Rgba32[] lower = new Rgba32[3];
        Rgba32[] upper = new Rgba32[3];
        Rgba32[] mana = new Rgba32[3];
        float[] fills = [0f, 0.5f, 1f];
        for (int i = 0; i < fills.Length; i++)
        {
            float fill = fills[i];
            UiSoftwareCompositor c = HudPreview.Render(
                "gem_fill_" + (int)(fill * 100),
                1280,
                720,
                (batch, table, l) =>
                {
                    HudView v = BarView(fill);
                    v.ManaLevel = fill;
                    UiInputRouter router = new();
                    router.BeginFrame(new UiPointer(Vector2.Zero, false, false, false));
                    StatusBarPanel.Build(batch, router, v, in l, table, new NoHudActions());
                    router.EndFrame();
                });
            lower[i] = c.GetRgba((int)(hp.X + hp.W * 0.5f), (int)(hp.Y + hp.H * 0.62f));
            upper[i] = c.GetRgba((int)(hp.X + hp.W * 0.5f), (int)(hp.Y + hp.H * 0.3f));
            mana[i] = c.GetRgba((int)(mp.X + mp.W * 0.5f), (int)(mp.Y + mp.H * 0.62f));
        }

        // Empty: both samples are glass. Half: the lower sample is liquid (as at full), the upper still glass.
        // Full: both liquid. The MP gem follows its own level the same way.
        Assert.Equal(upper[0], upper[1]);
        Assert.Equal(lower[2], lower[1]);
        Assert.NotEqual(lower[0], lower[1]);
        Assert.NotEqual(upper[1], upper[2]);
        Assert.Equal(mana[2], mana[1]);
        Assert.NotEqual(mana[0], mana[1]);
    }

    // ---- the panel ----

    private static StatusBarRecordingCanvas Run(UiSpriteTable table, HudView v, UiLayout l)
    {
        StatusBarRecordingCanvas c = new(table);
        StatusBarPanel.Build(c, new StatusBarFakeInput(), v, l, table, new StatusBarFakeActions());
        return c;
    }

    [Fact]
    public void ManaGemFill_IsOneConstant_AndIsOne()
    {
        Assert.Equal(1f, StatusBarPanel.ManaGemFill);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void TheNewBarRects_TalonAndDragonGrow_AndTheRightSideMirrors(int w, int h)
    {
        UiSpriteTable table = RealTable();
        UiLayout l = UiLayout.Create(w, h, 0f);
        StatusBarRecordingCanvas c = Run(table, BarView(0.5f), l);

        Assert.Equal(l.FromUnits(l.LeftGroup, 128f, -3f, 71f, 52f), Assert.Single(c.Sprites("hud.talon.left")).Rect);
        Assert.Equal(l.FromUnits(l.LeftGroup, 0f, -52f, 118f, 58f), Assert.Single(c.Sprites("hud.dragon.left")).Rect);
        // Mirrored across the 640 u bar: 640 - 128 - 71 = 441 and 640 - 118 = 522.
        Assert.Equal(l.FromUnits(l.RightGroup, 441f - 441f, -3f, 71f, 52f), Assert.Single(c.Sprites("hud.talon.right")).Rect);
        Assert.Equal(l.FromUnits(l.RightGroup, 522f - 441f, -52f, 118f, 58f), Assert.Single(c.Sprites("hud.dragon.right")).Rect);
        Assert.Equal(l.ViewportWidth, c.Sprites("hud.dragon.right").Single().Rect.Right, 2);
        // The talon overlaps the gem's right edge: it bites into the stone.
        UiRect gem = Assert.Single(c.Sprites("gem.hp.full")).Rect;
        UiRect talon = Assert.Single(c.Sprites("hud.talon.left")).Rect;
        Assert.True(talon.X < gem.Right - 10f * l.Scale, "the talon should reach well into the gem");
        Assert.True(talon.Right > gem.Right);
    }

    [Fact]
    public void TheTalonsDrawOverTheGem_AndTheSpillOverTheTalons_WithTheNewArt()
    {
        UiSpriteTable table = ModifiedTable(addNewArt: true);
        UiLayout l = UiLayout.Create(1920, 1080, 0f);
        StatusBarRecordingCanvas c = Run(table, BarView(0.5f), l);

        foreach ((string setting, string gem, string talon, string spill) in new[]
        {
            ("gem.setting.left", "gem.hp.full", "hud.talon.left", "gem.hp.spill"),
            ("gem.setting.right", "gem.mp.full", "hud.talon.right", "gem.mp.spill"),
        })
        {
            int iSetting = c.Calls.FindIndex(x => x.Name == setting);
            int iGem = c.Calls.FindIndex(x => x.Name == gem);
            int iTalon = c.Calls.FindIndex(x => x.Name == talon);
            int iSpill = c.Calls.FindIndex(x => x.Name == spill);
            Assert.True(iSetting >= 0 && iSetting < iGem, $"{setting} under {gem}");
            Assert.True(iGem < iTalon, $"{talon} over {gem}");
            Assert.True(iTalon < iSpill, $"{spill} lights the talons");
            Assert.Equal(StatusBarCallKind.Additive, c.Calls[iSpill].Kind);
            // The bezel and the spill are centred on the gem.
            UiRect g = c.Calls[iGem].Rect;
            Assert.Equal(g.X + g.W * 0.5f, c.Calls[iSetting].Rect.X + c.Calls[iSetting].Rect.W * 0.5f, 2);
            Assert.Equal(g.Y + g.H * 0.5f, c.Calls[iSpill].Rect.Y + c.Calls[iSpill].Rect.H * 0.5f, 2);
            Assert.Equal(62f * l.Scale, c.Calls[iSetting].Rect.W, 2);
            Assert.Equal(56f * l.Scale, c.Calls[iSetting].Rect.H, 2);
            Assert.Equal(128f * l.Scale, c.Calls[iSpill].Rect.W, 2);
            Assert.Equal(64f * l.Scale, c.Calls[iSpill].Rect.H, 2);
        }
    }

    [Fact]
    public void WithoutTheNewArt_NoSettingOrSpillIsDrawn_AndTheGemsStillDraw()
    {
        UiLayout l = UiLayout.Create(1920, 1080, 0f);
        StatusBarRecordingCanvas c = Run(RealTable(), BarView(0.5f), l);

        foreach (string absent in new[] { "gem.setting.left", "gem.setting.right", "gem.hp.spill", "gem.mp.spill" })
        {
            Assert.Empty(c.Sprites(absent));
        }

        Assert.Equal(2, c.Gems.Count());
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 128)]
    [InlineData(1f, 255)]
    public void TheSpillAlpha_ScalesWithTheLevel(float level, int alpha)
    {
        UiSpriteTable table = ModifiedTable(addNewArt: true);
        HudView v = BarView(level);
        v.ManaLevel = 1f;
        StatusBarRecordingCanvas c = Run(table, v, UiLayout.Create(1920, 1080, 0f));

        List<StatusBarCall> spill = c.Sprites("gem.hp.spill").ToList();
        if (alpha == 0)
        {
            Assert.Empty(spill);
            return;
        }

        Assert.Equal(alpha, (int)Assert.Single(spill).Color.A);
        // The mana gem is full, so its spill is full.
        Assert.Equal(255, Assert.Single(c.Sprites("gem.mp.spill")).Color.A);
    }

    [Fact]
    public void WithoutTheMaskAndGlints_TheGemFallsBackToTheClippedStone()
    {
        UiSpriteTable table = ModifiedTable(addNewArt: false, removeMaskAndGlints: true);
        UiLayout l = UiLayout.Create(1920, 1080, 0f);
        StatusBarRecordingCanvas c = Run(table, BarView(0.5f), l);

        Assert.Empty(c.Gems);
        Assert.Single(c.Sprites("gem.hp.empty"));
        StatusBarCall clip = Assert.Single(c.Sprites("gem.hp.full"));
        Assert.Equal(StatusBarCallKind.Clipped, clip.Kind);
        Assert.Equal(new UiRect(0f, 0.5f, 1f, 0.5f), clip.Src);
        // The mana gem is full: the empty stone, then the whole full stone.
        Assert.Single(c.Sprites("gem.mp.empty"));
        Assert.Equal(new UiRect(0f, 0f, 1f, 1f), Assert.Single(c.Sprites("gem.mp.full")).Src);
    }

    [Fact]
    public void TheGemQuads_GetTheViewsAnimation_AndTheManaGemNeverPulses()
    {
        UiLayout l = UiLayout.Create(1920, 1080, 0f);
        HudView v = BarView(0.2f);
        v.GemPulse = 0.8f;
        v.AnimationSeconds = 3.3f;
        StatusBarRecordingCanvas c = Run(RealTable(), v, l);

        StatusBarCall hp = Assert.Single(c.Gems, g => g.Gem.Kind == UiGemKind.Health);
        StatusBarCall mp = Assert.Single(c.Gems, g => g.Gem.Kind == UiGemKind.Mana);
        Assert.Equal(0.2f, hp.Gem.Fill, 5);
        Assert.Equal(0.8f, hp.Gem.Pulse, 5);
        Assert.Equal(GemAnimation.Phase(3.3f), hp.Gem.Phase, 5);
        Assert.Equal(GemAnimation.Sweep(3.3f, 0f), hp.Gem.Sweep, 5);
        Assert.Equal(StatusBarPanel.ManaGemFill, mp.Gem.Fill, 5);
        Assert.Equal(0f, mp.Gem.Pulse);
        Assert.Equal(GemAnimation.Sweep(3.3f, GemAnimation.SweepPeriodSeconds * 0.5f), mp.Gem.Sweep, 5);
    }

    // ---- numerals ----

    private static bool IsNumeral(StatusBarCall x) => x.Name is not null && x.Name.StartsWith("hudnum.", StringComparison.Ordinal);

    [Fact]
    public void WithTheNumeralArt_HealthAndLevelAreSprites_NotText()
    {
        UiSpriteTable table = ModifiedTable(addNewArt: true);
        UiLayout l = UiLayout.Create(1920, 1080, 0f);
        StatusBarRecordingCanvas c = Run(table, BarView(73f / 120f), l);

        string[] numerals = c.Calls.Where(IsNumeral).Select(x => x.Name!).ToArray();
        // "73/120" then the level "7".
        Assert.Equal(["hudnum.7", "hudnum.3", "hudnum.slash", "hudnum.1", "hudnum.2", "hudnum.0", "hudnum.7"], numerals);
        Assert.DoesNotContain(c.Texts, t => t.Text == "73/120");
        Assert.DoesNotContain(c.Texts, t => t.Text == "7");

        // The health numerals sit inside the gem's width and above its bottom edge, left to right.
        UiRect gem = Assert.Single(c.Sprites("gem.hp.full")).Rect;
        List<StatusBarCall> health = c.Calls.Where(IsNumeral).Take(6).ToList();
        Assert.All(health, n => Assert.True(n.Rect.X >= gem.X - 0.5f && n.Rect.Right <= gem.Right + 0.5f && n.Rect.Bottom <= gem.Bottom, $"{n.Name} {n.Rect} vs {gem}"));
        Assert.True(health.Zip(health.Skip(1)).All(p => p.First.Rect.Right <= p.Second.Rect.X + 0.01f));

        // The level sits inside the EXP cup.
        UiRect cup = l.FromUnits(l.CentreGroup, 420f - 199f, 3f, 12f, 12f);
        StatusBarCall level = c.Calls.Last(IsNumeral);
        Assert.True(level.Rect.X >= cup.X && level.Rect.Right <= cup.Right && level.Rect.Y >= cup.Y && level.Rect.Bottom <= cup.Bottom, $"{level.Rect} vs {cup}");
    }

    [Fact]
    public void TheNumerals_DrawAfterTheDragons_AndBeforeAllTheText()
    {
        UiSpriteTable table = ModifiedTable(addNewArt: true);
        StatusBarRecordingCanvas c = Run(table, BarView(0.6f), UiLayout.Create(1920, 1080, 0f));

        int dragon = c.Calls.FindLastIndex(x => x.Name == "hud.dragon.right");
        int firstNumeral = c.Calls.FindIndex(IsNumeral);
        int lastNumeral = c.Calls.FindLastIndex(IsNumeral);
        int firstText = c.Calls.FindIndex(x => x.Kind == StatusBarCallKind.Text);
        Assert.True(dragon < firstNumeral && lastNumeral < firstText);
    }

    [Fact]
    public void ANumberTheArtHasNoGlyphFor_FallsBackToText()
    {
        UiSpriteTable table = ModifiedTable(addNewArt: true);
        UiLayout l = UiLayout.Create(1920, 1080, 0f);

        // Negative health has a minus sign, which the numeral art lacks: the whole number is text.
        StatusBarRecordingCanvas c = Run(table, BarView(0f, hp: -5, maxHp: 100), l);

        Assert.Contains(c.Texts, t => t.Text == "-5/100");
        // The level still has its sprite.
        Assert.Single(c.Calls.Where(IsNumeral));
    }

    [Fact]
    public void WithoutTheNumeralArt_HealthAndLevelAreText_AsToday()
    {
        UiLayout l = UiLayout.Create(1920, 1080, 0f);
        StatusBarRecordingCanvas c = Run(RealTable(), BarView(73f / 120f), l);

        Assert.Contains(c.Texts, t => t.Text == "73/120");
        Assert.Contains(c.Texts, t => t.Text == "7");
        Assert.DoesNotContain(c.Calls, IsNumeral);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoManaNumberIsEverDrawn_WithOrWithoutTheNumeralArt(bool newArt)
    {
        UiSpriteTable table = newArt ? ModifiedTable(addNewArt: true) : RealTable();
        foreach ((int w, int h) in new[] { (1280, 720), (1920, 1080), (2560, 1080) })
        {
            UiLayout l = UiLayout.Create(w, h, 0f);
            StatusBarRecordingCanvas c = Run(table, BarView(1f, hp: 120, maxHp: 120), l);

            // Nothing numeric (text or numeral sprite) on the whole right group, and the gem there draws once.
            Assert.DoesNotContain(c.Texts, t => t.TextPos.X >= l.RightGroup.X);
            Assert.DoesNotContain(c.Calls, x => IsNumeral(x) && x.Rect.X >= l.RightGroup.X);
            StatusBarCall mp = Assert.Single(c.Gems, g => g.Gem.Kind == UiGemKind.Mana);
            Assert.Equal(StatusBarPanel.ManaGemFill, mp.Gem.Fill);
        }
    }

    // ---- easing and the surface ----

    [Fact]
    public void ClampDelta_CapsAtADecisecond_AndIgnoresNonsense()
    {
        Assert.Equal(0.1f, GemAnimation.MaxFrameSeconds);
        Assert.Equal(0.1f, GemAnimation.ClampDelta(5f));
        Assert.Equal(0.1f, GemAnimation.ClampDelta(0.1f));
        Assert.Equal(0.016f, GemAnimation.ClampDelta(0.016f));
        Assert.Equal(0f, GemAnimation.ClampDelta(-1f));
        Assert.Equal(0f, GemAnimation.ClampDelta(float.NaN));
        Assert.Equal(0.1f, GemAnimation.ClampDelta(float.PositiveInfinity));
    }

    [Fact]
    public void Ease_NeverLeavesZeroToOne_NeverOvershoots_AndConverges()
    {
        Random rng = new(1234);
        for (int i = 0; i < 2000; i++)
        {
            float current = (float)rng.NextDouble();
            float target = (float)rng.NextDouble();
            float dt = (float)(rng.NextDouble() * 0.3);

            float next = GemAnimation.Ease(current, target, dt);

            Assert.InRange(next, Math.Min(current, target) - 1e-6f, Math.Max(current, target) + 1e-6f);
            Assert.InRange(next, 0f, 1f);
        }

        float level = 0f;
        for (int i = 0; i < 400; i++)
        {
            level = GemAnimation.Ease(level, 0.7f, 1f / 60f);
        }

        Assert.Equal(0.7f, level);
    }

    [Fact]
    public void Ease_OneCappedFrame_MovesAFixedShareOfTheGap_WhateverTheRealFrameTime()
    {
        float after5Seconds = GemAnimation.Ease(0f, 1f, 5f);
        float afterCap = GemAnimation.Ease(0f, 1f, GemAnimation.MaxFrameSeconds);

        Assert.Equal(afterCap, after5Seconds);
        Assert.Equal(1f - MathF.Exp(-GemAnimation.EaseRate * GemAnimation.MaxFrameSeconds), afterCap, 5);
        Assert.InRange(afterCap, 0.3f, 0.7f);
    }

    [Fact]
    public void Ease_HandlesNaNAndOutOfRangeInput()
    {
        Assert.Equal(0f, GemAnimation.Ease(float.NaN, float.NaN, 0.05f));
        Assert.Equal(1f, GemAnimation.Ease(5f, 7f, 0.05f));
        Assert.Equal(0f, GemAnimation.Ease(-2f, -9f, 0.05f));
    }

    [Theory]
    [InlineData(1f, 0.4f)]
    [InlineData(0.5f, 0.4f)]
    [InlineData(0.25f, 0.4f)]
    [InlineData(0.3f, 0.123f)]
    public void Pulse_IsZeroAtOrAboveAQuarter(float level, float seconds)
    {
        Assert.Equal(0f, GemAnimation.Pulse(level, seconds));
    }

    [Fact]
    public void Pulse_ThrobsBelowAQuarter_DeepensAsTheLevelFalls_AndStaysInRange()
    {
        // 1.25 Hz: the wave is 0 at t = 0 and peaks at 0.4 s.
        Assert.Equal(0f, GemAnimation.Pulse(0.05f, 0f), 5);
        Assert.Equal(1f, GemAnimation.Pulse(0.05f, 0.4f), 5);
        Assert.Equal(1f, GemAnimation.Pulse(0.10f, 0.4f), 5);
        float shallow = GemAnimation.Pulse(0.2f, 0.4f);
        Assert.InRange(shallow, 0.2f, 0.5f);
        Assert.True(GemAnimation.Pulse(0.2f, 0.4f) < GemAnimation.Pulse(0.12f, 0.4f));
        for (float t = 0f; t < 3f; t += 0.037f)
        {
            Assert.InRange(GemAnimation.Pulse(0.08f, t), 0f, 1f);
        }
    }

    [Fact]
    public void PhaseAndSweep_StayInRange_AndTheSweepRunsOnlyDuringItsWindow()
    {
        for (float t = 0f; t < GemAnimation.LoopSeconds; t += 0.31f)
        {
            Assert.InRange(GemAnimation.Phase(t), 0f, 1f);
            Assert.InRange(GemAnimation.Sweep(t, 0f), 0f, 1f);
            Assert.InRange(GemAnimation.Sweep(t, 3.5f), 0f, 1f);
        }

        Assert.Equal(0f, GemAnimation.Sweep(0f, 0f));
        Assert.Equal(0.5f, GemAnimation.Sweep(0.7f, 0f), 5);
        Assert.Equal(0f, GemAnimation.Sweep(3f, 0f));
        Assert.Equal(0f, GemAnimation.Sweep(float.NaN, 0f));
        // Every period divides the loop, so the wrap is seamless.
        Assert.Equal(0f, GemAnimation.LoopSeconds % GemAnimation.SweepPeriodSeconds);
        Assert.Equal(0f, GemAnimation.LoopSeconds * GemAnimation.PhaseHertz % 1f);
        Assert.Equal(0f, GemAnimation.LoopSeconds * GemAnimation.PulseHertz % 1f);
    }

    private sealed class BatchBackend : IClassicHudBackend
    {
        private readonly UiBatch _batch;

        public BatchBackend(UiSpriteTable table)
        {
            Sprites = table;
            _batch = new UiBatch(BlockGlyphs.Create());
            _batch.SetSpriteTable(table);
        }

        public ClassicHudBackendState State => ClassicHudBackendState.Ready;

        public string? FailureReason => null;

        public UiSpriteTable? Sprites { get; }

        public UiBatch Batch => _batch;

        public IUiCanvas BeginFrame(in UiLayout layout)
        {
            _batch.BeginFrame(in layout);
            return _batch;
        }

        public void Submit()
        {
        }

        public void Dispose()
        {
        }
    }

    private static ClassicHudFrame FrameWith(int hp, int maxHp) => new()
    {
        Width = 1920,
        Height = 1080,
        Pointer = new UiPointer(Vector2.Zero, false, false, false),
        Status = new HudStatusSnapshot(hp, maxHp, Dream.Simulation.Players.PlayerInteractionMode.Combat, 5, 9, 12, 1.2f, 14, 7, 40, 100, 10, 1f, 10, 3, false, 0),
    };

    [Fact]
    public void Surface_FirstFrame_ShowsTheTargetsAsTheyAre_WithoutFillingFromEmpty()
    {
        double now = 50;
        using ClassicHudSurface surface = new(new BatchBackend(RealTable()), () => now);

        surface.Draw(FrameWith(60, 120));

        Assert.Equal(0.5f, surface.View.HealthLevel);
        Assert.Equal(StatusBarPanel.ManaGemFill, surface.View.ManaLevel);
        Assert.Equal(0f, surface.View.AnimationSeconds);
        Assert.Equal(0f, surface.View.GemPulse);
    }

    [Fact]
    public void Surface_EasesTowardANewHealth_InBounds_AndConvergesExactly()
    {
        double now = 50;
        using ClassicHudSurface surface = new(new BatchBackend(RealTable()), () => now);
        surface.Draw(FrameWith(120, 120));

        float previous = surface.View.HealthLevel;
        Assert.Equal(1f, previous);
        for (int i = 0; i < 300; i++)
        {
            now += 1.0 / 60.0;
            surface.Draw(FrameWith(30, 120));
            float level = surface.View.HealthLevel;
            Assert.InRange(level, 0.25f, previous);
            previous = level;
        }

        Assert.Equal(0.25f, surface.View.HealthLevel);
        // Easing back up never passes the target either.
        for (int i = 0; i < 300; i++)
        {
            now += 1.0 / 60.0;
            surface.Draw(FrameWith(90, 120));
            Assert.InRange(surface.View.HealthLevel, 0.25f, 0.75f);
        }

        Assert.Equal(0.75f, surface.View.HealthLevel);
    }

    [Fact]
    public void Surface_AStall_MovesTheAnimationByNoMoreThanTheCap()
    {
        double now = 50;
        using ClassicHudSurface surface = new(new BatchBackend(RealTable()), () => now);
        surface.Draw(FrameWith(120, 120));

        now += 30.0;
        surface.Draw(FrameWith(0, 120));

        // One 0.1 s step toward empty, not the whole fall; and 0.1 s of animation time, not 30.
        Assert.Equal(GemAnimation.Ease(1f, 0f, 0.1f), surface.View.HealthLevel, 5);
        Assert.True(surface.View.HealthLevel > 0.4f);
        Assert.Equal(0.1f, surface.View.AnimationSeconds, 5);
    }

    [Fact]
    public void Surface_AClockThatRunsBackwards_DoesNotMoveAnything()
    {
        double now = 50;
        using ClassicHudSurface surface = new(new BatchBackend(RealTable()), () => now);
        surface.Draw(FrameWith(120, 120));
        now += 0.05;
        surface.Draw(FrameWith(120, 120));

        now -= 10;
        surface.Draw(FrameWith(0, 120));

        Assert.Equal(1f, surface.View.HealthLevel);
        Assert.Equal(0.05f, surface.View.AnimationSeconds, 5);
    }

    [Fact]
    public void Surface_AnimationSeconds_WrapsInsideTheLoop()
    {
        double now = 0;
        using ClassicHudSurface surface = new(new BatchBackend(RealTable()), () => now);
        surface.Draw(FrameWith(120, 120));

        for (int i = 0; i < 9_000; i++)
        {
            now += 0.1;
            surface.Draw(FrameWith(120, 120));
            Assert.InRange(surface.View.AnimationSeconds, 0f, GemAnimation.LoopSeconds);
        }

        // 9,000 frames of 0.1 s is 900 s: past one loop of 840 s, so the clock did wrap.
        Assert.InRange(surface.View.AnimationSeconds, 0f, 100f);
    }

    [Fact]
    public void Surface_ThePulse_StartsOnlyBelowAQuarterOfTheEasedLevel()
    {
        double now = 50;
        using ClassicHudSurface surface = new(new BatchBackend(RealTable()), () => now);
        surface.Draw(FrameWith(120, 120));
        Assert.Equal(0f, surface.View.GemPulse);

        bool pulsedWhileAbove = false;
        bool pulsedBelow = false;
        for (int i = 0; i < 400; i++)
        {
            now += 1.0 / 60.0;
            surface.Draw(FrameWith(12, 120));
            if (surface.View.HealthLevel >= GemAnimation.PulseBelow)
            {
                pulsedWhileAbove |= surface.View.GemPulse > 0f;
            }
            else
            {
                pulsedBelow |= surface.View.GemPulse > 0.05f;
            }
        }

        Assert.False(pulsedWhileAbove);
        Assert.True(pulsedBelow);
        Assert.InRange(surface.View.GemPulse, 0f, 1f);
    }

    [Fact]
    public void Surface_Draw_AllocatesNothing_WithTheGemsAnimating()
    {
        double now = 50;
        BatchBackend backend = new(ModifiedTable(addNewArt: true));
        using ClassicHudSurface surface = new(backend, () => now);
        surface.Draw(FrameWith(120, 120));
        int step = 0;

        AllocationAssert.AtMost(0, () =>
        {
            // The target alternates, so the easing keeps moving; the clock advances a frame at a time.
            now += 1.0 / 60.0;
            surface.Draw(FrameWith((step++ >> 6 & 1) == 0 ? 20 : 110, 120));
        });

        Assert.True(backend.Batch.QuadCount > 100);
        Assert.Equal(0, backend.Batch.DroppedQuads);
    }

    // ---- shader sources ----

    [Fact]
    public void TheShaders_DeclareTheGemUniformsTheRendererUploads_AndBothNewModes()
    {
        string fragment = File.ReadAllText(AssetPaths.Shader("uiFragment.glsl"));
        string vertex = File.ReadAllText(AssetPaths.Shader("uiVertex.glsl"));

        // Six vec2 offsets (3 per kind), two stone rects, the same program for every mode.
        Assert.Contains("uniform vec2 uGemOffset[6];", fragment);
        Assert.Contains("uniform vec4 uGemRect[2];", fragment);
        Assert.Contains("mode == 3u", fragment);
        Assert.Contains("mode == 4u", fragment);
        Assert.Contains("vec4(t.rgb * vTint.rgb * a, 0.0)", fragment);
        Assert.Contains("flat in uint vMode;", fragment);
        Assert.Contains("flat out uint vMode;", vertex);
        Assert.Equal(UiBatch.GemKindCount * 3, 6);
    }
}
