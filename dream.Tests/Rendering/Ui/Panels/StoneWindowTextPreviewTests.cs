using System.Globalization;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using Dream.Tests.Support;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

// Measures a recorded text call the way the canvas that drew it would. The fake records the text scale
// and the style of every call, so the drawn box is (position, measured width, bake x scale) -- which is
// exactly the UiSizedText.Px the panel asked UiText for.
internal static class UiTextProbe
{
    public static float Width(InventoryFakeCanvas c, CanvasCall call)
    {
        float previous = c.TextScale;
        c.TextScale = call.TextScale;
        float width = c.MeasureText(call.Text, call.TextStyle);
        c.TextScale = previous;
        return width;
    }

    public static float Height(CanvasCall call) => UiText.BakePixels(call.TextStyle) * call.TextScale;

    public static UiRect Box(InventoryFakeCanvas c, CanvasCall call) =>
        new(call.TextPos.X, call.TextPos.Y, Width(c, call), Height(call));

    // A box the art fixed, plus the slack the 13 px bake forces on a box shorter than it. UiText never
    // sizes text under UiFontSetup.BodyPixels, so an 8 u strip at scale 1 holds a 13 px line that hangs
    // out of it by 2.5 px top and bottom; that is the old behaviour, not an overflow this pass can fix.
    public static UiRect WithFloorSlack(UiRect box)
    {
        float slack = MathF.Max(0f, UiFontSetup.BodyPixels - box.H) * 0.5f;
        return new UiRect(box.X, box.Y - slack, box.W, box.H + (2f * slack));
    }

    public static bool Inside(UiRect inner, UiRect outer, float tolerance = 1f) =>
        inner.X >= outer.X - tolerance
        && inner.Y >= outer.Y - tolerance
        && inner.X + inner.W <= outer.X + outer.W + tolerance
        && inner.Y + inner.H <= outer.Y + outer.H + tolerance;

    // Every text call sits inside the window AND inside one of the boxes the art gave the panel. The
    // drop shadow is the same line drawn ShadowPx down and right of the text, so it is allowed that
    // much more room; the text itself is not.
    public static void AssertEveryTextFits(
        InventoryFakeCanvas c, in UiLayout l, UiRect window, IReadOnlyList<UiRect> boxes, string what)
    {
        float shadow = UiText.ShadowPx(in l);
        Assert.NotEmpty(c.Texts);
        foreach (CanvasCall call in c.Texts)
        {
            if (call.Text.Length == 0)
            {
                continue;
            }

            float tolerance = call.Color == ItemTierStyle.Shadow ? shadow + 1f : 1f;
            UiRect box = Box(c, call);
            Assert.True(Inside(box, window, tolerance), $"{what}: '{call.Text}' at {box.X},{box.Y} {box.W}x{box.H} leaves the window {window.X},{window.Y} {window.W}x{window.H}");
            bool held = false;
            foreach (UiRect candidate in boxes)
            {
                if (Inside(box, WithFloorSlack(candidate), tolerance))
                {
                    held = true;
                    break;
                }
            }

            Assert.True(held, $"{what}: '{call.Text}' at {box.X},{box.Y} {box.W}x{box.H} is in none of its {boxes.Count} boxes");
        }
    }

    // The size a unit constant resolves to never outgrows the box it goes in (above the 13 px bake,
    // which is the floor UiText.For keeps for legibility at scale 1).
    public static void AssertSizeFitsBox(in UiLayout l, UiSizedText t, float boxUnits, string what) =>
        Assert.True(
            t.Px <= MathF.Max(UiFontSetup.BodyPixels, boxUnits * l.Scale) + 1f,
            $"{what}: {t.Px}px in a {boxUnits} u ({boxUnits * l.Scale}px) box at scale {l.Scale}");
}

// The four stone windows -- inventory, vendor, trade and the quest giver's dialogue -- at every review
// resolution and at a forced ui_scale, with representative content: long item names, a five digit price,
// a greeting that wraps. Two things are checked: every line of text each window draws stays inside the
// box the art gave it (measured at the size it is drawn with, which is what InventoryFakeCanvas records),
// and every unit constant resolves to a size its box can hold. The PNGs go to artifacts/hud-preview/ so
// the sizes can be looked at.
public sealed class StoneWindowTextPreviewTests : IDisposable
{
    private readonly InventoryScene _scene = new();
    private readonly VendorHudState _vendor = new();
    private readonly TradeHudState _trade = new();
    private readonly QuestHudState _quests = new();
    private readonly HudIntentQueue _intents = new();

    public void Dispose() => _scene.Dispose();

    // The four review resolutions on auto, plus a forced ui_scale 4 (capped by the auto fit, so it only
    // differs where the viewport can take it).
    public static IEnumerable<object[]> Cases()
    {
        foreach ((int w, int h) in new[] { (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160) })
        {
            yield return [w, h, 0f];
            yield return [w, h, 4f];
        }
    }

    private static string Tag(float configured) =>
        configured <= 0f ? "auto" : configured.ToString("0.##", CultureInfo.InvariantCulture);

    private static InventoryItemSnapshot Item(long id, string kind, string name, int stack = 1, ItemRarity rarity = ItemRarity.Common) =>
        new(id, kind, name, "visual", stack, "A plain item with a description long enough to wrap in the tooltip.", ["Detail one", "Detail two"], null, HeldLightKind.None, 0, rarity, false);

    // Long names, a big stack and a tier, in the bag and in two wells.
    private void FillBag()
    {
        _scene.Items[0] = Item(1, "gem.amber", "Amberlight Shard of the Drowned Hall", stack: 128);
        _scene.Items[1] = Item(2, "gear.iron-band", "Iron Band", rarity: ItemRarity.Rare);
        _scene.Items[2] = Item(3, "nothing.known", "Unrendered Reliquary");
        _scene.Items[18] = Item(4, "axe.twinbit", "Twinbit Axe of the Long Watch", stack: 9999);
        for (int n = 0; n < 10; n++)
        {
            _scene.Items[24 + n] = Item(100 + n, "gem.amber", "Pale Amber Lot " + (100 + n));
        }

        _scene.Worn[0] = Item(50, "nothing.worn", "Wornless Blade of Testing");
        _scene.Refresh();
        _scene.View.HeldBackCount = 3;
        _scene.View.Inventory = _scene.View.Inventory!.Value with { Gold = 98765 };
    }

    private void OpenVendor() => _vendor.Open(
        900,
        "Old Mara of the Long Road",
        [
            new VendorStockLine("tool.torch", "Torch", 12),
            new VendorStockLine("weapon.sword", "Greatsword of the Drowned Hall", 99999),
            new VendorStockLine("gem.amber", "Amberlight Shard", 1450),
            new VendorStockLine("gear.iron-band", "Iron Band", 60000),
        ]);

    private void OpenTrade() => _trade.Open(
        TradeHudStage.Open,
        "Bob the Bold of Westmarch",
        [new TradeHudLine(1, ItemRarity.Rare, "Amberlight Shard of the Drowned Hall")],
        12345,
        false,
        false,
        [
            new TradeHudLine(2, ItemRarity.Uncommon, "Greatsword of the Long Watch"),
            new TradeHudLine(3, ItemRarity.Common, "Torch"),
        ],
        54321,
        true,
        false);

    private void OpenDialogue() => _quests.OpenDialogue(
        900,
        "Gate Warden of the Low Keep",
        "Keep your voice low, traveller. The hall below has not been quiet since the river rose, and the ones who went down to look have not come back up again.",
        [
            new QuestOfferLine("q1", "Drain the Drowned Hall", "The river took the lower hall and something came up with it. Find what is down there, and bring back what it wears.", "1200 XP, 450 gold", QuestOfferState.Available),
            new QuestOfferLine("q2", "Count the Dead", "Walk the lower hall and count what the water left.", "300 XP", QuestOfferState.InProgress),
            new QuestOfferLine("q3", "The Warden's Debt", "Pay what is owed.", "90 gold", QuestOfferState.ReadyToTurnIn),
        ]);

    private void SetQuestLog() => _quests.SetLog(
    [
        new QuestLogEntry("q1", "Drain the Drowned Hall", QuestState.Active,
            [new QuestObjectiveLine("Drowned Thralls slain in the lower hall", 3, 12), new QuestObjectiveLine("Warden's Seal recovered", 0, 1)]),
    ]);

    private InventoryFakeCanvas Canvas(in UiLayout l)
    {
        InventoryFakeCanvas canvas = new(_scene.Table) { TextScale = l.TextScale };
        return canvas;
    }

    private static List<UiRect> Rows(in UiLayout l, UiRect win, float top, float pitch, int count, float x = 19f, float w = 152f, float h = 12f)
    {
        List<UiRect> boxes = new(count);
        for (int n = 0; n < count; n++)
        {
            boxes.Add(l.FromUnits(win, x, top + (n * pitch), w, h));
        }

        return boxes;
    }

    // ---- the inventory window ---------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void InventoryWindow_EveryLineStaysInItsBox(int w, int h, float configured)
    {
        FillBag();
        UiLayout l = UiLayout.Create(w, h, configured);
        UiRect win = l.Inventory;
        InventoryFakeCanvas c = Canvas(in l);
        InventoryWindow.Build(c, _scene.Input, _scene.View, l, _scene.Table, _scene.Actions);

        List<UiRect> boxes =
        [
            l.FromUnits(win, 34f, 12f, 120f, InventoryWindow.TitleStripUnits),
            l.FromUnits(win, InventoryWindow.HeldBackLeftUnits, InventoryWindow.HeldBackTopUnits, 160f, InventoryWindow.BandUnits),
            l.FromUnits(win, InventoryWindow.HeldBackLeftUnits, InventoryWindow.GoldTopUnits, 160f, InventoryWindow.BandUnits),
        ];
        for (int anchor = 0; anchor < HudView.BackpackSlots; anchor++)
        {
            if (_scene.Items[anchor] is InventoryItemSnapshot placed)
            {
                boxes.Add(InventoryWindow.ItemRect(l, win, anchor, _scene.FootprintOf(placed.ItemKind)));
            }
        }

        for (int well = 0; well < HudView.WornSlots; well++)
        {
            boxes.Add(InventoryWindow.WellRect(l, win, well));
        }

        UiTextProbe.AssertEveryTextFits(c, in l, win, boxes, $"inventory {w}x{h} @{Tag(configured)}");
        UiTextProbe.AssertSizeFitsBox(in l, InventoryWindow.TitleTextFor(in l), InventoryWindow.TitleStripUnits, "inventory title");
        UiTextProbe.AssertSizeFitsBox(in l, InventoryWindow.RowTextFor(in l), InventoryWindow.RowUnits, "inventory row");
        UiTextProbe.AssertSizeFitsBox(in l, UiText.For(in l, InventoryWindow.BandTextUnits, InventoryWindow.BandUnits), InventoryWindow.BandUnits, "inventory band");
        UiTextProbe.AssertSizeFitsBox(in l, UiText.For(in l, InventoryWindow.CellTextUnits, InventoryWindow.CellUnits), InventoryWindow.CellUnits, "inventory cell");
    }

    // ---- the vendor window ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void VendorWindow_EveryLineStaysInItsBox(int w, int h, float configured)
    {
        FillBag();
        OpenVendor();
        _scene.View.Vendor = _vendor;
        UiLayout l = UiLayout.Create(w, h, configured);
        UiRect win = l.Character;
        InventoryFakeCanvas c = Canvas(in l);
        VendorWindow.Build(c, _scene.Input, _scene.View, l, _scene.Table, _scene.Actions);

        List<UiRect> boxes =
        [
            l.FromUnits(win, 35f, 8f, 120f, 8f),
            l.FromUnits(win, 35f, 20f, 119f, 8f),
            l.FromUnits(win, 19f, 46f, 152f, 12f),
            l.FromUnits(win, 19f, 168f, 152f, 13f),
            l.FromUnits(win, 19f, 182f + (VendorWindow.SellRows * 13f) + 6f, 152f, 16f),
        ];
        boxes.AddRange(Rows(in l, win, 58f, 13f, VendorWindow.BuyRows));
        boxes.AddRange(Rows(in l, win, 182f, 13f, VendorWindow.SellRows));

        UiTextProbe.AssertEveryTextFits(c, in l, win, boxes, $"vendor {w}x{h} @{Tag(configured)}");
        UiTextProbe.AssertSizeFitsBox(in l, UiText.For(in l, VendorWindow.RowTextUnits, 12f), 12f, "vendor row");
        UiTextProbe.AssertSizeFitsBox(in l, UiText.For(in l, VendorWindow.HeadTextUnits, 12f), 12f, "vendor head");
        UiTextProbe.AssertSizeFitsBox(in l, UiText.For(in l, VendorWindow.ButtonTextUnits, 16f), 16f, "vendor button");
    }

    // ---- the trade window -------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void TradeWindow_EveryLineStaysInItsBox(int w, int h, float configured)
    {
        FillBag();
        OpenTrade();
        UiLayout l = UiLayout.Create(w, h, configured);
        UiRect win = l.Character;
        InventoryFakeCanvas c = Canvas(in l);
        TradeWindow.Build(c, _scene.Input, _scene.View, l, _scene.Table, _scene.Actions, _trade, _intents);

        float stepW = (152f - 10f) / 6f;
        float third = (152f - 4f) / 3f;
        List<UiRect> boxes =
        [
            l.FromUnits(win, 35f, 8f, 120f, 8f),
            l.FromUnits(win, 35f, 20f, 119f, 8f),
            l.FromUnits(win, 19f, 34f, 152f, 12f),
            l.FromUnits(win, 19f, 46f, 152f, 12f),
            l.FromUnits(win, 19f, 166f, 152f, 12f),
            l.FromUnits(win, 19f, 194f, 152f, 13f),
        ];
        boxes.AddRange(Rows(in l, win, 58f, 13f, TradeWindow.OfferRows));
        boxes.AddRange(Rows(in l, win, 206f, 13f, TradeWindow.BagRows));
        for (int n = 0; n < 6; n++)
        {
            boxes.Add(l.FromUnits(win, 19f + (n * (stepW + 2f)), 178f, stepW, 12f));
        }

        for (int n = 0; n < 3; n++)
        {
            boxes.Add(l.FromUnits(win, 19f + (n * (third + 2f)), 316f, third, 16f));
        }

        UiTextProbe.AssertEveryTextFits(c, in l, win, boxes, $"trade {w}x{h} @{Tag(configured)}");
        UiTextProbe.AssertSizeFitsBox(in l, UiText.For(in l, TradeWindow.RowTextUnits, 12f), 12f, "trade row");
        UiTextProbe.AssertSizeFitsBox(in l, UiText.For(in l, TradeWindow.HeadTextUnits, 12f), 12f, "trade head");
        UiTextProbe.AssertSizeFitsBox(in l, UiText.For(in l, TradeWindow.ButtonTextUnits, 16f), 16f, "trade button");

        // The gold steps are 23.7 u wide with four character labels, so their size satisfies the WIDTH.
        var step = UiText.ForFitting(c, in l, "-100", TradeWindow.GoldStepTextUnits, 12f, (stepW - 2f) * l.Scale);
        Assert.True(
            UiText.Measure(c, "-100", in step) <= (stepW - 2f) * l.Scale + 1f,
            $"'-100' is {UiText.Measure(c, "-100", in step)}px in a {(stepW - 2f) * l.Scale}px step button at scale {l.Scale}");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TradeWindow_TheWaitingStagesAlsoStayInTheirBoxes(int w, int h, float configured)
    {
        _trade.Open(TradeHudStage.Incoming, "Bob the Bold of Westmarch", [], 0, false, false, [], 0, false, false);
        UiLayout l = UiLayout.Create(w, h, configured);
        UiRect win = l.Character;
        InventoryFakeCanvas c = Canvas(in l);
        TradeWindow.Build(c, _scene.Input, _scene.View, l, _scene.Table, _scene.Actions, _trade, _intents);

        List<UiRect> boxes =
        [
            l.FromUnits(win, 35f, 8f, 120f, 8f),
            l.FromUnits(win, 35f, 20f, 119f, 8f),
            l.FromUnits(win, 19f, 70f, 152f, 12f),
            l.FromUnits(win, 19f, 90f, 152f, 16f),
            l.FromUnits(win, 19f, 110f, 152f, 16f),
        ];
        UiTextProbe.AssertEveryTextFits(c, in l, win, boxes, $"trade incoming {w}x{h} @{Tag(configured)}");

        // An early return must not leave the canvas's text scale behind: the panels after this one
        // would be resized in this trade stage only.
        Assert.Equal(l.TextScale, c.TextScale, 4);
    }

    // ---- the dialogue window ----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void NpcDialogueWindow_EveryLineStaysInItsBox(int w, int h, float configured)
    {
        OpenDialogue();
        SetQuestLog();
        UiLayout l = UiLayout.Create(w, h, configured);
        UiRect win = l.Character;
        InventoryFakeCanvas c = Canvas(in l);
        NpcDialogueWindow.Build(c, _scene.Input, _scene.View, l, _scene.Table, _scene.Actions, _quests, _intents);

        List<UiRect> boxes =
        [
            l.FromUnits(win, 35f, 8f, 120f, 8f),
            l.FromUnits(win, 35f, 20f, 119f, 8f),

            // The wrapped blocks: each stops short of the head under it.
            l.FromUnits(win, 19f, 40f, 152f, 114f - 40f),
            l.FromUnits(win, 19f, 114f, 152f, 12f),
            l.FromUnits(win, 19f, 212f, 152f, 288f - 212f),
            l.FromUnits(win, 19f, 288f, 152f, 316f - 288f),
            l.FromUnits(win, 19f, 372f, 152f, 16f),
        ];
        boxes.AddRange(Rows(in l, win, 127f, 13f, QuestHudState.MaxOffers));
        boxes.AddRange(Rows(in l, win, 316f, 12f, 4));

        UiTextProbe.AssertEveryTextFits(c, in l, win, boxes, $"dialogue {w}x{h} @{Tag(configured)}");
        UiTextProbe.AssertSizeFitsBox(in l, UiText.For(in l, NpcDialogueWindow.RowTextUnits, 12f), 12f, "dialogue row");
        UiTextProbe.AssertSizeFitsBox(in l, UiText.For(in l, NpcDialogueWindow.HeadTextUnits, 12f), 12f, "dialogue head");
        UiTextProbe.AssertSizeFitsBox(in l, UiText.For(in l, NpcDialogueWindow.ProseTextUnits, 12f), 12f, "dialogue prose");
        UiTextProbe.AssertSizeFitsBox(in l, UiText.For(in l, NpcDialogueWindow.ButtonTextUnits, 16f), 16f, "dialogue button");

        // The greeting block must not reach the "Tasks" head at 114 u.
        float headY = l.FromUnits(win, 19f, 114f, 152f, 12f).Y;
        foreach (CanvasCall call in c.Texts)
        {
            if (call.Color == ItemTierStyle.Shadow)
            {
                continue;
            }

            UiRect box = UiTextProbe.Box(c, call);
            if (box.Y < headY)
            {
                Assert.True(box.Y + box.H <= headY + 1f, $"'{call.Text}' runs into the Tasks head at {headY}");
            }
        }
    }

    // ---- the previews -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void StoneWindows_RenderWithTheRealFont(int w, int h, float configured)
    {
        FillBag();
        OpenVendor();
        OpenTrade();
        OpenDialogue();
        SetQuestLog();
        (UiGlyphTable glyphs, UiTexturePixels page) = RealFont.Get();
        string tag = Tag(configured);

        foreach ((string name, Action<UiBatch, UiSpriteTable, UiLayout> draw) in Panels())
        {
            UiSoftwareCompositor compositor = HudPreview.Render(
                $"win-{name}-{tag}", w, h, draw, configured, glyphs, page, forceWrite: true);
            Assert.Equal((w, h), (compositor.Width, compositor.Height));
        }
    }

    private IEnumerable<(string Name, Action<UiBatch, UiSpriteTable, UiLayout> Draw)> Panels()
    {
        yield return ("inventory", (b, t, l) =>
        {
            UiInputRouter router = Idle();
            InventoryWindow.Build(b, router, _scene.View, l, t, _scene.Actions);
            router.EndFrame();
        });

        yield return ("vendor", (b, t, l) =>
        {
            UiInputRouter router = Idle();
            _scene.View.Vendor = _vendor;
            VendorWindow.Build(b, router, _scene.View, l, t, _scene.Actions);
            router.EndFrame();
        });

        yield return ("trade", (b, t, l) =>
        {
            UiInputRouter router = Idle();
            TradeWindow.Build(b, router, _scene.View, l, t, _scene.Actions, _trade, _intents);
            router.EndFrame();
        });

        yield return ("dialogue", (b, t, l) =>
        {
            UiInputRouter router = Idle();
            NpcDialogueWindow.Build(b, router, _scene.View, l, t, _scene.Actions, _quests, _intents);
            router.EndFrame();
        });
    }

    // A pointer far off the window, so nothing is hovered and no tooltip covers the text.
    private static UiInputRouter Idle()
    {
        UiInputRouter router = new();
        router.BeginFrame(new UiPointer(new Vector2(-1000f, -1000f), false, false, false));
        return router;
    }
}
