using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

public class NoticePanelTests
{
    private static UiLayout Layout() => UiLayout.Create(1920, 1080, 0f);

    private static StatusBarRecordingCanvas Run(HudView v, bool recording = true)
    {
        StatusBarRecordingCanvas c = new(StatusBarTestData.Table, recording);
        NoticePanel.Build(c, v, Layout());
        return c;
    }

    private static HudView ViewWithLoot(params LootNotice[] notices)
    {
        HudView v = StatusBarTestData.View();
        v.Loot = new LootFeedbackSystem();
        v.Loot.AddNotices(notices);
        return v;
    }

    private static UiColor Color(int r, int g, int b) => new((byte)r, (byte)g, (byte)b, 255);

    [Fact]
    public void LootLines_UseTheTierColourAndAFlatBandEach()
    {
        HudView v = ViewWithLoot(
            new LootNotice("Plain", ItemRarity.Common, false),
            new LootNotice("Pink", ItemRarity.Uncommon, false),
            new LootNotice("Mint", ItemRarity.Rare, false),
            new LootNotice("Odd", (ItemRarity)9, false));
        StatusBarRecordingCanvas c = Run(v);

        List<StatusBarCall> texts = c.Texts.ToList();
        Assert.Equal(4, texts.Count);
        Assert.Equal(Color(0xF9, 0xF8, 0xFA), texts[0].Color);
        Assert.Equal(Color(0xF0, 0x8F, 0xB0), texts[1].Color);
        Assert.Equal(Color(0x5F, 0xD6, 0xA0), texts[2].Color);
        Assert.Equal(Color(0xF5, 0xDD, 0xA5), texts[3].Color);
        Assert.Equal(4, c.Calls.Count(x => x.Kind == StatusBarCallKind.Solid && x.Color.A is > 100 and < 160));
        // Rows climb one pitch per older line; the newest is on the entry row.
        Assert.True(texts[0].TextPos.Y < texts[1].TextPos.Y && texts[2].TextPos.Y < texts[3].TextPos.Y);
        Assert.Equal(NoticePanel.EntryRowUnits * 2.25f, texts[3].TextPos.Y, 0);
    }

    [Fact]
    public void LootLines_AreCappedAtTheSystemLimit()
    {
        LootNotice[] many = Enumerable.Range(0, 20).Select(n => new LootNotice("Item" + n, ItemRarity.Common, false)).ToArray();
        StatusBarRecordingCanvas c = Run(ViewWithLoot(many));

        Assert.Equal(LootFeedbackSystem.MaxLines, c.Texts.Count());
    }

    [Fact]
    public void LinesAreCentredOnTheWholeScreen()
    {
        StatusBarRecordingCanvas c = Run(ViewWithLoot(new LootNotice("Axe", ItemRarity.Rare, false)));

        StatusBarCall text = Assert.Single(c.Texts);
        StatusBarCall band = Assert.Single(c.Calls, x => x.Kind == StatusBarCallKind.Solid);
        Assert.Equal(960f, band.Rect.X + band.Rect.W * 0.5f, 0);
        Assert.Equal(960f, text.TextPos.X + text.Text!.Length * 7f * 0.5f, 0);
    }

    [Fact]
    public void PercentAndBracesInANameStayLiteral()
    {
        StatusBarRecordingCanvas c = Run(ViewWithLoot(new LootNotice("100%s {0} %d", ItemRarity.Common, false)));

        Assert.Equal("Looted: 100%s {0} %d (Common)", Assert.Single(c.Texts).Text);
    }

    [Fact]
    public void LongRejectionIsClippedToTheWidthFractionAndKeepsPercentLiteral()
    {
        HudView v = StatusBarTestData.View();
        v.RejectionMessage = new string('%', 600);
        StatusBarRecordingCanvas c = Run(v);

        StatusBarCall text = Assert.Single(c.Texts);
        Assert.Equal(Color(0xF4, 0x66, 0x50), text.Color);
        Assert.True(text.Text!.Length * 7f <= 1920f * NoticePanel.MaxWidthFraction);
        Assert.All(text.Text, ch => Assert.Equal('%', ch));
        Assert.True(text.Text.Length > 100);
    }

    [Fact]
    public void WorldTransfer_ShowsOnlyAfterTheThreshold()
    {
        HudView v = StatusBarTestData.View();
        v.WorldTransfer = new WorldTransferHudState(true, 412 * 1024, 3, TimeSpan.FromMilliseconds(100));
        Assert.Empty(Run(v).Texts);

        v.WorldTransfer = new WorldTransferHudState(true, 412 * 1024, 3, TimeSpan.FromSeconds(6));
        List<string?> lines = Run(v).Texts.Select(t => t.Text).ToList();
        Assert.Equal(["Receiving world... 412 KB, 6 s", WorldTransferHudState.DetailLine], lines);
    }

    [Fact]
    public void Death_DrawsTheOverlayTitleAndRespawnLabel()
    {
        HudView v = StatusBarTestData.View();
        v.Status = v.Status with { IsDead = true, RespawnSeconds = 3 };
        StatusBarRecordingCanvas c = Run(v);

        StatusBarCall dim = c.Calls.First(x => x.Kind == StatusBarCallKind.Solid);
        Assert.Equal(new UiRect(0f, 0f, 1920f, 1080f), dim.Rect);
        Assert.Contains(c.Texts, t => t.Text == "You died");
        Assert.Contains(c.Texts, t => t.Text == "Respawning in 3 s");

        v.Status = v.Status with { RespawnSeconds = 0 };
        Assert.Contains(Run(v).Texts, t => t.Text == "Respawning");
    }

    [Fact]
    public void Death_WithTheInventoryOpen_DoesNotDimTheWindows_AndStaysLeftOfThem()
    {
        UiLayout layout = Layout();
        HudView v = StatusBarTestData.View();
        v.InventoryOpen = true;
        v.Status = v.Status with { IsDead = true, RespawnSeconds = 3 };
        StatusBarRecordingCanvas c = Run(v);

        Assert.DoesNotContain(c.Calls, x => x.Kind == StatusBarCallKind.Solid && x.Rect.W >= layout.ViewportWidth);
        Assert.All(c.Texts.Where(t => t.Text == "You died"), t => Assert.True(t.TextPos.X + (t.Text!.Length * 7f) < layout.Character.X));
    }

    [Fact]
    public void Rejection_WithTheInventoryOpen_CentresInTheStripLeftOfTheWindows()
    {
        UiLayout layout = Layout();
        HudView v = StatusBarTestData.View();
        v.InventoryOpen = true;
        v.RejectionMessage = "Inventory move rejected because too many inventory actions, try again shortly.";
        StatusBarRecordingCanvas c = Run(v);

        StatusBarCall band = Assert.Single(c.Calls, x => x.Kind == StatusBarCallKind.Solid);
        Assert.True(band.Rect.Right <= layout.Character.X + 1f, $"band {band.Rect} runs into the character window at {layout.Character.X}");
        Assert.InRange(band.Rect.X + (band.Rect.W * 0.5f), (layout.Character.X * 0.5f) - 1f, (layout.Character.X * 0.5f) + 1f);
    }

    [Fact]
    public void Alive_DrawsNoOverlayAndNothingWhenThereAreNoNotices()
    {
        StatusBarRecordingCanvas c = Run(StatusBarTestData.View());

        Assert.Empty(c.Calls);
    }

    [Fact]
    public void GetRarity_FollowsTheLinesThroughExpiryAndEviction()
    {
        LootFeedbackSystem loot = new();
        loot.AddNotices([new LootNotice("a", ItemRarity.Rare, false), new LootNotice("b", ItemRarity.Uncommon, false)]);
        Assert.Equal(ItemRarity.Rare, loot.GetRarity(0));
        Assert.Equal(ItemRarity.Uncommon, loot.GetRarity(1));

        loot.Update(3f);
        loot.AddNotices([new LootNotice("c", ItemRarity.Common, false)]);
        loot.Update(1.5f);
        Assert.Equal(1, loot.Count);
        Assert.Equal(ItemRarity.Common, loot.GetRarity(0));

        for (int n = 0; n < 6; n++)
        {
            loot.AddNotices([new LootNotice("x" + n, n % 2 == 0 ? ItemRarity.Rare : ItemRarity.Common, false)]);
        }

        Assert.Equal(LootFeedbackSystem.MaxLines, loot.Count);
        Assert.Equal(ItemRarity.Rare, loot.GetRarity(0));
        Assert.Equal(ItemRarity.Common, loot.GetRarity(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => loot.GetRarity(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => loot.GetRarity(-1));
    }

    [Fact]
    public void Build_AllocatesNothingAfterWarmUp()
    {
        HudView v = ViewWithLoot(new LootNotice("Axe", ItemRarity.Rare, false), new LootNotice("Stone", ItemRarity.Common, true));
        v.RejectionMessage = "Backpack is full";
        v.WorldTransfer = new WorldTransferHudState(true, 4096, 1, TimeSpan.FromSeconds(2));
        UiLayout l = Layout();
        StatusBarRecordingCanvas c = new(StatusBarTestData.Table, recording: false);

        for (int i = 0; i < 3; i++)
        {
            NoticePanel.Build(c, v, l);
        }

        long bytes = Dream.Tests.Utilities.AllocationAssert.MeasureBytesPerIteration(() =>
        {
            NoticePanel.Build(c, v, l);
        });

        Assert.Equal(0L, bytes);
        Assert.True(c.Count > 10);
    }
}
