using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Combat;
using Dream.Simulation.Inventory;
using Dream.Tests.Rendering.Ui.Panels;
using Dream.Tests.Support;
using OpenTK.Mathematics;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Dream.Tests.Rendering.Ui;

// Headless previews. With DREAM_HUD_PREVIEW=1 the PNGs land in artifacts/hud-preview/. Without it
// the same scenes run and are probed, but nothing is written (the fixture scene is always written
// once so the foundation can be looked at without the variable).
public class HudPreviewTests
{
    private static readonly UiColor White = UiColor.White;

    private sealed class NoActions : IHudActions
    {
        public void Inventory(InventoryHudAction action) { }

        public void CloseInventory() { }
    }

    private static Rgba32 Backdrop(UiSoftwareCompositor c, int x, int y) => c.GetRgba(x, y);

    // Fixture scene: every primitive the batch has, drawn with the real sprites and the real font.
    private static void DrawFixtures(UiBatch b, UiSpriteTable t, UiLayout l)
    {
        float s = l.PixelsPerTexel;

        // Bar: three groups stretched into their rects, gap fill tiled between them.
        b.Tile(t.Get(UiSpriteNames.GapFill), l.LeftGap, White);
        b.Tile(t.Get(UiSpriteNames.GapFill), l.RightGap, White);
        b.Sprite(t.Get(UiSpriteNames.BaseLeft), l.LeftGroup, White);
        b.Sprite(t.Get(UiSpriteNames.BaseCentre), l.CentreGroup, White);
        b.Sprite(t.Get(UiSpriteNames.BaseRight), l.RightGroup, White);

        // Inventory window chrome: crown, then the slab 9-sliced to the rest of the window.
        UiSprite crown = t.Get(UiSpriteNames.WinCrownInventory);
        UiSprite slab = t.Get(UiSpriteNames.WinSlabInventory);
        UiRect inv = l.Inventory;
        b.Sprite(crown, new UiRect(inv.X, inv.Y, crown.W * s, crown.H * s), White);
        b.NineSlice(slab, new UiRect(inv.X, inv.Y + crown.H * s, inv.W, inv.H - crown.H * s), White);

        // Character window frame next to it, clipped to its rect.
        b.PushClip(l.Character);
        b.NineSlice(t.Get(UiSpriteNames.WinSlabCombine), new UiRect(l.Character.X - 40 * s, l.Character.Y, l.Character.W + 80 * s, l.Character.H), White);
        b.PopClip();

        // Items in the three tiers on a row of cells.
        UiSprite cell = t.Get(UiSpriteNames.CellOccupied);
        string[] kinds = ["tool.torch", "tool.pickaxe", "weapon.sword"];
        float x = l.Character.X;
        float y = l.Character.Y + 200 * s;
        foreach (UiPage tier in new[] { UiPage.ItemsCommon, UiPage.ItemsUncommon, UiPage.ItemsRare })
        {
            foreach (string kind in kinds)
            {
                if (t.TryGet(UiSpriteNames.Item(kind), out UiSprite icon) || TryFirstItem(t, out icon))
                {
                    UiRect slot = new(x, y, 40 * s, 40 * s);
                    b.NineSlice(cell, slot, White);
                    float k = MathF.Min(slot.W / icon.W, slot.H / icon.H);
                    b.Sprite(icon with { Page = tier }, new UiRect(slot.X + (slot.W - icon.W * k) / 2, slot.Y + (slot.H - icon.H * k) / 2, icon.W * k, icon.H * k), White);
                    x += 42 * s;
                }
            }
        }

        // Text, both styles, with a drop shadow done by drawing twice.
        b.Text("Iron blade  Lv 12  100%", new Vector2(21, 21), UiTextStyle.Body, new UiColor(0, 0, 0, 255));
        b.Text("Iron blade  Lv 12  100%", new Vector2(20, 20), UiTextStyle.Body, White);
        b.Text("1,284 / 1,500", new Vector2(20, 60), UiTextStyle.Large, new UiColor(255, 90, 90, 255));

        // Two-channel damage digits: gold fill and its rim, then crit blue.
        float dx = 20;
        foreach (int d in new[] { 4, 4, 3, 0, 0 })
        {
            UiSprite digit = t.Get(UiSpriteNames.Digit(d));
            b.MaskGlyph(digit, new UiRect(dx, 120, digit.W * 4, digit.H * 4), new UiColor(0xFD, 0xB4, 0x42, 255), new UiColor(0x4A, 0x18, 0x00, 255));
            b.MaskGlyph(digit, new UiRect(dx, 220, digit.W * 4, digit.H * 4), new UiColor(0x0A, 0x9A, 0xFE, 255), new UiColor(0x00, 0x17, 0x50, 255));
            dx += digit.W * 4 * 44f / 64f;
        }

        // Solid band behind a notice and a fading quad.
        b.Solid(new UiRect(20, 330, 240, 22), new UiColor(0, 0, 0, 128));
        b.Text("Picked up: Torch x1", new Vector2(26, 334), UiTextStyle.Body, White);

        // Cursor at its pivot.
        UiSprite cursor = t.Get(UiSpriteNames.CursorPointer);
        b.Sprite(cursor, new UiRect(300 - cursor.PivotX * s, 380 - cursor.PivotY * s, cursor.W * s, cursor.H * s), White);
    }

    private static bool TryFirstItem(UiSpriteTable t, out UiSprite sprite)
    {
        foreach (string name in t.Names)
        {
            if (name.StartsWith("item.", StringComparison.Ordinal))
            {
                sprite = t.Get(name);
                return true;
            }
        }

        sprite = default;
        return false;
    }

    private static UiSoftwareCompositor Render(string name, int w, int h, Action<UiBatch, UiSpriteTable, UiLayout> draw, bool force = false)
    {
        (UiGlyphTable glyphs, UiTexturePixels page) = RealFont.Get();
        return HudPreview.Render(name, w, h, draw, glyphs: glyphs, fontPage: page, forceWrite: force);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(1440, 1024)]
    public void Fixtures_RenderAtTheReviewSizes(int w, int h)
    {
        UiSoftwareCompositor c = Render("fixtures", w, h, DrawFixtures, force: w == 1920);
        UiLayout l = UiLayout.Create(w, h, 0f);

        // The bar occupies the bottom edge: its left group differs from the backdrop pattern.
        Rgba32 corner = c.GetRgba(0, 0);
        int changed = 0;
        for (int x = (int)l.LeftGroup.X; x < (int)l.LeftGroup.Right; x += 3)
        {
            for (int y = (int)l.LeftGroup.Y; y < h; y += 3)
            {
                if (c.GetRgba(x, y) != corner)
                {
                    changed++;
                }
            }
        }

        Assert.True(changed > 200, $"left bar group should be painted ({changed} probes differ)");
        // Nothing is drawn beyond the right edge of the viewport and the output has the asked size.
        Assert.Equal((w, h), (c.Width, c.Height));
    }

    [Fact]
    public void Fixtures_ClipRectLimitsTheCharacterWindow()
    {
        int w = 1920;
        int h = 1080;
        UiLayout l = UiLayout.Create(w, h, 0f);
        UiSoftwareCompositor c = Render("clip", w, h, (b, t, lay) =>
        {
            b.PushClip(lay.Character);
            b.Solid(new UiRect(0, 0, w, h), new UiColor(255, 0, 0, 255));
            b.PopClip();
        });

        Rgba32 inside = c.GetRgba((int)(l.Character.X + 5), (int)(l.Character.Y + 5));
        Rgba32 outside = c.GetRgba((int)(l.Character.X - 5), (int)(l.Character.Y + 5));
        Assert.Equal(new Rgba32(255, 0, 0, 255), inside);
        Assert.NotEqual(new Rgba32(255, 0, 0, 255), outside);
    }

    [Fact]
    public void Fixtures_ZOrderLaterDrawsCoverEarlierOnes()
    {
        UiSoftwareCompositor c = Render("zorder", 640, 480, (b, t, lay) =>
        {
            b.Solid(new UiRect(10, 10, 100, 100), new UiColor(255, 0, 0, 255));
            b.Solid(new UiRect(60, 60, 100, 100), new UiColor(0, 0, 255, 255));
            b.Solid(new UiRect(60, 10, 50, 50), new UiColor(0, 255, 0, 128));
        });

        Assert.Equal(new Rgba32(255, 0, 0, 255), c.GetRgba(20, 20));
        Assert.Equal(new Rgba32(0, 0, 255, 255), c.GetRgba(80, 80));
        // 50% green premultiplied over red
        Rgba32 blend = c.GetRgba(80, 30);
        Assert.InRange(blend.R, 125, 130);
        Assert.InRange(blend.G, 125, 130);
    }

    [Fact]
    public void Fixtures_TierPagesDifferForTheSameIcon()
    {
        string name = "item.tool.torch";
        UiSoftwareCompositor c = Render("tiers", 640, 240, (b, t, lay) =>
        {
            UiSprite icon = t.TryGet(name, out UiSprite found) ? found : default;
            if (icon.W == 0)
            {
                TryFirstItem(t, out icon);
            }

            float k = 4f;
            b.Sprite(icon, new UiRect(20, 20, icon.W * k, icon.H * k), White);
            b.Sprite(icon with { Page = UiPage.ItemsUncommon }, new UiRect(220, 20, icon.W * k, icon.H * k), White);
            b.Sprite(icon with { Page = UiPage.ItemsRare }, new UiRect(420, 20, icon.W * k, icon.H * k), White);
        });

        long a = Sum(c, 20), u = Sum(c, 220), r = Sum(c, 420);
        Assert.NotEqual(a, u);
        Assert.NotEqual(u, r);
        Assert.NotEqual(a, r);
    }

    private static long Sum(UiSoftwareCompositor c, int x0)
    {
        long sum = 0;
        for (int y = 20; y < 180; y++)
        {
            for (int x = x0; x < x0 + 160; x++)
            {
                Rgba32 p = c.GetRgba(x, y);
                sum += p.R * 3 + p.G * 5 + p.B * 7;
            }
        }

        return sum;
    }

    [Fact]
    public void MaskGlyph_PaintsFillAndRimInTheirColours()
    {
        UiSoftwareCompositor c = Render("mask", 320, 160, (b, t, lay) =>
        {
            UiSprite d = t.Get(UiSpriteNames.Digit(8));
            b.MaskGlyph(d, new UiRect(10, 10, d.W * 6, d.H * 6), new UiColor(0x0A, 0x9A, 0xFE, 255), new UiColor(0x00, 0x17, 0x50, 255));
        });

        bool sawFill = false;
        bool sawRim = false;
        for (int y = 10; y < 150; y++)
        {
            for (int x = 10; x < 200; x++)
            {
                Rgba32 p = c.GetRgba(x, y);
                sawFill |= p.B > 230 && p.G > 130 && p.R < 40;
                sawRim |= p.B > 60 && p.B < 100 && p.G < 40 && p.R < 10;
            }
        }

        Assert.True(sawFill, "expected fill-coloured pixels");
        Assert.True(sawRim, "expected rim-coloured pixels");
    }

    // ---- real panels on a populated view ----------------------------------------------------

    private static HudStatusSnapshot Status(bool dead = false) => new(
        73, 120, Dream.Simulation.Players.PlayerInteractionMode.Combat, 5, 9, 12, 1.2f, 14, 7, 40, 100, 10, 1f, 10, 3, dead, dead ? 5 : 0);

    // Every item sprite the art has, cycling the three tiers, plus four worn items.
    private static InventoryScene PopulatedScene()
    {
        InventoryScene scene = new();
        ItemRarity[] tiers = [ItemRarity.Common, ItemRarity.Uncommon, ItemRarity.Rare];
        int slot = 0;
        foreach (string name in scene.Table.Names.Where(n => n.StartsWith("item.", StringComparison.Ordinal)).Order())
        {
            if (slot >= scene.Items.Length)
            {
                break;
            }

            scene.Items[slot] = InventoryScene.Item(slot + 1, name["item.".Length..], rarity: tiers[slot % 3], stack: slot % 5 == 4 ? 12 : 1);
            slot++;
        }

        scene.Worn[0] = scene.Items[0];
        scene.Equipped[0] = true;
        scene.Worn[2] = scene.Items[1];
        scene.Equipped[1] = true;
        scene.Refresh();
        scene.View.Status = Status();
        scene.View.PlayerPosition = new Vector3(142.6f, 12f, -87.2f);
        scene.View.Equipment = CharacterPanelModel.BuildSlotRows(scene.View.Inventory);
        scene.View.StatRows = CharacterPanelModel.BuildStatRows(scene.View.Status);
        return scene;
    }

    private static void BuildFrames(UiBatch b, UiSpriteTable t, UiLayout lay, HudView view, UiPointer[] frames)
    {
        UiInputRouter router = new();
        NoActions actions = new();
        foreach (UiPointer pointer in frames)
        {
            b.BeginFrame(in lay);
            router.BeginFrame(pointer);
            ClassicHud.Build(b, router, view, in lay, t, actions);
            router.EndFrame();
        }
    }

    private static UiPointer NoPointer => new(Vector2.Zero, false, false, false);

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(1440, 1024)]
    public void RealPanels_PlayScene(int w, int h)
    {
        using InventoryScene scene = PopulatedScene();
        scene.View.InventoryOpen = false;
        UiLayout layout = UiLayout.Create(w, h, 0f);
        FloatingCombatTextRenderState Number(string text, FloatingCombatTextKind kind, float x, long order) =>
            new(text, Vector3.Zero, 0f, 0f, 0f, 1f, 1f, default, 0, kind, 0.3f, order, (int)order, 1f, new Vector2(x, h * 0.4f));
        scene.View.CombatText = [
            Number("128", FloatingCombatTextKind.Regular, w * 0.3f, 1),
            Number("2047", FloatingCombatTextKind.Critical, w * 0.5f, 2),
            Number("36", FloatingCombatTextKind.Incoming, w * 0.7f, 3)];
        scene.View.Labels = [new HudWorldLabel(new Vector2(w * 0.5f, h * 0.25f), "Grey wolf Lv 4", "42/60")];

        UiSoftwareCompositor c = Render("real-play", w, h, (b, t, lay) => BuildFrames(b, t, lay, scene.View, [NoPointer]));

        Assert.Equal((w, h), (c.Width, c.Height));
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(1440, 1024)]
    public void RealPanels_InventoryOpen(int w, int h)
    {
        using InventoryScene scene = PopulatedScene();
        UiLayout layout = UiLayout.Create(w, h, 0f);
        Vector2 p = new(layout.Inventory.X + (70f * layout.Scale), layout.Inventory.Y + (300f * layout.Scale));
        UiPointer pointer = new(p, false, false, true);

        UiSoftwareCompositor c = Render("real-inventory", w, h, (b, t, lay) => BuildFrames(b, t, lay, scene.View, [pointer]));

        Assert.Equal((w, h), (c.Width, c.Height));
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void RealPanels_Tooltip(int w, int h)
    {
        using InventoryScene scene = PopulatedScene();
        UiLayout layout = UiLayout.Create(w, h, 0f);
        Vector2 p = InventoryScene.Centre(InventoryWindow.SlotRect(layout, layout.Inventory, 6));
        UiPointer pointer = new(p, false, false, true);

        UiSoftwareCompositor c = Render("real-tooltip", w, h, (b, t, lay) => BuildFrames(b, t, lay, scene.View, [pointer, pointer, pointer]));

        Assert.True(InventoryUiState.Current.HoverActive);
        Assert.Equal((w, h), (c.Width, c.Height));
    }

    // 3840x2160 is scale 4 (text doubled to 26 and 52 px); 2560x1440 is scale 3 (Large status text,
    // text not doubled). Written to artifacts/hud-preview/ every run so the sizes can be looked at.
    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(2560, 1440)]
    public void RealPanels_HighScale_PlayAndInventory(int w, int h)
    {
        using InventoryScene scene = PopulatedScene();
        scene.View.Labels = [new HudWorldLabel(new Vector2(w * 0.5f, h * 0.25f), "Grey wolf Lv 4", "42/60")];
        scene.View.InventoryOpen = false;
        UiSoftwareCompositor play = Render("hi-play", w, h, (b, t, lay) => BuildFrames(b, t, lay, scene.View, [NoPointer]), force: true);
        scene.View.InventoryOpen = true;
        UiLayout layout = UiLayout.Create(w, h, 0f);
        Vector2 p = InventoryScene.Centre(InventoryWindow.SlotRect(layout, layout.Inventory, 6));
        UiPointer pointer = new(p, false, false, true);
        UiSoftwareCompositor inv = Render("hi-inventory", w, h, (b, t, lay) => BuildFrames(b, t, lay, scene.View, [pointer, pointer, pointer]), force: true);

        Assert.Equal((w, h), (play.Width, play.Height));
        Assert.Equal((w, h), (inv.Width, inv.Height));
    }

    // The drawn body line, in pixels, at each review resolution's auto scale.
    [Theory]
    [InlineData(1920, 1080, 21f)]
    [InlineData(2560, 1440, 28f)]
    [InlineData(3840, 2160, 38f)]
    public void PanelTextHeight_FollowsTheLayoutTextScale(int w, int h, float bodyPx)
    {
        UiBatch batch = new(BlockGlyphs.Create());
        UiLayout layout = UiLayout.Create(w, h, 0f);
        batch.BeginFrame(in layout);

        Assert.Equal(bodyPx, PanelText.Height(batch, UiTextStyle.Body), 3);
        Assert.Equal(bodyPx, InventoryWindow.BodyHeight(batch), 3);
        Assert.Equal(26f * layout.TextScale, PanelText.Height(batch, UiTextStyle.Large), 3);

        // The height a panel lays out with is the height the batch actually draws, whichever bake
        // the pixel face ends up on. These disagreeing is how text leaves its row.
        Assert.Equal(bodyPx, batch.LineHeight(UiTextStyle.Body), 3);
    }

    // Past 20 px the pixel face moves to its 26 px bake rather than stretching the 13 px one, and
    // the width must not jump when it does: measuring and drawing go through the same decision.
    [Fact]
    public void ThePixelFace_SwitchesBakeWithoutChangingTheMeasuredWidth()
    {
        UiBatch batch = new(BlockGlyphs.Create());
        UiLayout layout = UiLayout.Create(3840, 2160, 0f);
        batch.BeginFrame(in layout);

        batch.TextScale = 19f / 13f;
        float below = batch.MeasureText("Inventory", UiTextStyle.Body) / 19f;
        batch.TextScale = 21f / 13f;
        float above = batch.MeasureText("Inventory", UiTextStyle.Body) / 21f;

        Assert.Equal(below, above, 2);
    }

    [Fact]
    public void RealPanels_Dead()
    {
        using InventoryScene scene = PopulatedScene();
        scene.View.InventoryOpen = false;
        scene.View.Status = Status(dead: true);

        UiSoftwareCompositor c = Render("real-dead", 1920, 1080, (b, t, lay) => BuildFrames(b, t, lay, scene.View, [NoPointer]));

        Assert.Equal(1920, c.Width);
    }

    [Fact]
    public void ClassicHudBuild_WithStubPanelsAndEmptyView_DrawsWithoutThrowing()
    {
        UiSoftwareCompositor c = Render("empty-view", 1920, 1080, (b, t, lay) =>
        {
            ClassicHud.Build(b, new NullUiInput(), new HudView(), in lay, t, new NoActions());
        });

        Assert.Equal(1920, c.Width);
    }

    // ---- hover info panel scenes ----------------------------------------------------------------

    private static void BuildHover(UiBatch b, UiSpriteTable t, UiLayout lay, HudView view, Vector2 pointer)
    {
        UiInputRouter router = new();
        b.BeginFrame(in lay);
        router.BeginFrame(new UiPointer(pointer, false, false, true));
        ClassicHud.Build(b, router, view, in lay, t, new NoActions());
        router.EndFrame();
    }

    private static HudView HoverView(HoverInfo info, HoverKind kind, bool inventory = false)
    {
        HudView v = StatusBarTestData.View();
        v.InventoryOpen = inventory;
        v.HoverInfo = info;
        v.HoverCursorKind = kind;
        v.HoverPanel = new HoverPanelState(true, 1f, kind);
        return v;
    }

    private static HoverInfo NpcInfo()
    {
        HoverInfo info = new()
        {
            Target = new HoverTargetId(HoverKind.Npc, 3),
            Disposition = HoverDisposition.Friendly,
            Title = "Maren the Smith",
            Subtitle = "Blacksmith",
            LineCount = 1,
            Hint = "Click to talk"
        };
        info.Lines[0] = new HoverLine("Wares", "Weapons, armour");
        return info;
    }

    private static HoverInfo ItemInfo()
    {
        HoverInfo info = new()
        {
            Target = new HoverTargetId(HoverKind.GroundItem, 91),
            Disposition = HoverDisposition.Item,
            Title = "Ashen Longsword",
            Subtitle = "Rare weapon",
            Tier = ItemTier.Rare,
            LineCount = 2,
            Hint = "Click to take"
        };
        info.Lines[0] = new HoverLine("Damage", "14 - 22");
        info.Lines[1] = new HoverLine("Weight", "6");
        return info;
    }

    [Fact]
    public void HoverPanels_Scenes()
    {
        const int w = 1920;
        const int h = 1080;
        Vector2 centre = new(w * 0.45f, h * 0.4f);
        (string Name, HudView View, Vector2 Pointer)[] scenes =
        [
            ("hover-enemy-full", HoverView(HoverInfoPanelTests.EnemyInfo(100, 100), HoverKind.Enemy), centre),
            ("hover-enemy-half", HoverView(HoverInfoPanelTests.EnemyInfo(50, 100), HoverKind.Enemy), centre),
            ("hover-npc", HoverView(NpcInfo(), HoverKind.Npc), centre),
            ("hover-item", HoverView(ItemInfo(), HoverKind.GroundItem), centre),
            ("hover-defeated", HoverView(DefeatedInfo(), HoverKind.Enemy), centre),
            ("hover-corner", HoverView(HoverInfoPanelTests.EnemyInfo(30, 100), HoverKind.Enemy), new Vector2(w - 24f, h - 150f)),
            ("hover-inventory", HoverView(ItemInfo(), HoverKind.GroundItem, inventory: true), new Vector2(w * 0.7f, h * 0.45f))
        ];
        foreach ((string name, HudView view, Vector2 pointer) in scenes)
        {
            UiSoftwareCompositor c = Render(name, w, h, (b, t, lay) => BuildHover(b, t, lay, view, pointer));
            Assert.Equal(w, c.Width);
        }
    }

    private static HoverInfo DefeatedInfo()
    {
        HoverInfo info = HoverInfoPanelTests.EnemyInfo(0, 100);
        info.Defeated = true;
        return info;
    }
}
