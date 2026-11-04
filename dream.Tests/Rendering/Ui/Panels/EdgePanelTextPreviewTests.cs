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

// The panels on the screen edges -- chat, the party plate, the quest tracker, the notices and the
// world labels -- all size their boxes FROM the text height, so UiLayout.TextScale alone makes them
// follow ui_scale. This class holds that: at every review resolution, and at a forced ui_scale 4, the
// plates stay on screen, nothing crosses the top of the bar, and the party plate's reported bottom
// edge really is below its own rows and above the quest plate (the two share the left edge and the
// tracker positions itself from PartyPanel.BottomOf, so they must measure the same way).
//
// It also writes artifacts/hud-preview/edges-{auto,4}_WxH.png with the real font: every edge panel
// populated at once, for looking at by eye.
public class EdgePanelTextPreviewTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach ((int w, int h) in new[] { (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160) })
        {
            yield return [w, h, 0f];
            yield return [w, h, 4f];
        }
    }

    // ---- the content every case is drawn with ----

    private static PartyHudState Party()
    {
        PartyHudState party = new();
        party.Set(
        [
            new PartyMemberLine("Aldric the Warden", 12, 74, 120, true),
            new PartyMemberLine("Brynn", 9, 31, 90, false),
            new PartyMemberLine("Cael", 14, 0, 110, false),
            new PartyMemberLine("Dunmar", 7, 88, 88, false),
        ],
            "Eorl");
        return party;
    }

    private static QuestHudState Quests()
    {
        QuestHudState quests = new();
        quests.SetLog(
        [
            new QuestLogEntry("wolves", "The Wolves of Greymoor", QuestState.Active, [new QuestObjectiveLine("Slay Grey Wolf", 1, 4), new QuestObjectiveLine("Recover the collar", 0, 1)]),
            new QuestLogEntry("ledger", "The Warden's Ledger", QuestState.Active, [new QuestObjectiveLine("Find the ledger", 1, 1)]),
            new QuestLogEntry("well", "A Fouled Well", QuestState.Active, [new QuestObjectiveLine("Cleanse the well", 3, 3)]),
        ]);
        return quests;
    }

    private static ChatHudState Chat()
    {
        ChatHudState chat = new();
        chat.AddServerMessage(ChatChannel.World, "Warden", "the road is watched");
        chat.AddServerMessage(ChatChannel.Say, "Aldric", "hold here, the wolves are close");
        chat.AddServerMessage(ChatChannel.Party, "Brynn", "I am down to a third");
        chat.AddServerMessage(ChatChannel.Whisper, "Cael", "bring the torch");
        chat.AddSystemLine("Cael has fallen.");
        chat.AddServerMessage(ChatChannel.Command, "server", "party invite Eorl");
        chat.AddServerMessage(ChatChannel.World, "Herald", "rain begins over the keep");
        chat.AddServerMessage(ChatChannel.Say, "Dunmar", "on my way");
        chat.Typing = true;
        foreach (char ch in "a long line being typed right now to the party")
        {
            chat.AppendInput(ch);
        }

        return chat;
    }

    // Two labels on nearly the same spot, so the stacking that pushes the second one down is in the
    // picture, plus one apart from them.
    private static HudWorldLabel[] Labels() =>
        [
            new HudWorldLabel(new Vector2(0.44f, 0.36f), "Grey Wolf Lv 5", "HP 14/40"),
            new HudWorldLabel(new Vector2(0.46f, 0.37f), "Dire Wolf Lv 9", "HP 62/62"),
            new HudWorldLabel(new Vector2(0.72f, 0.28f), "Gate Warden", string.Empty),
        ];

    private static HudView View(int w, int h)
    {
        HudView v = StatusBarTestData.View(hp: 74, maxHp: 120, exp: 2100, toNext: 6500, level: 12);
        v.Quickbar = new QuickbarHudState();
        v.Loot = new LootFeedbackSystem();
        v.Loot.AddNotices(
        [
            new LootNotice("Rusted Iron Key", ItemRarity.Common, false),
            new LootNotice("Warden's Signet", ItemRarity.Uncommon, false),
            new LootNotice("Ashen Greatsword", ItemRarity.Rare, false),
        ]);
        v.RejectionMessage = "Your pack has no room for that.";
        v.Labels = [.. Labels().Select(x => x with { Screen = new Vector2(x.Screen.X * w, x.Screen.Y * h) })];
        return v;
    }

    // ---- geometry ----

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryEdgePlate_StaysOnScreen_AndClearOfTheBar(int w, int h, float configured)
    {
        UiLayout l = UiLayout.Create(w, h, configured);
        HudView v = View(w, h);
        PartyHudState party = Party();
        QuestHudState quests = Quests();
        float textH = 13f * l.TextScale;

        foreach ((string name, Action<StatusBarRecordingCanvas> build) in Panels(v, l, party, quests))
        {
            StatusBarRecordingCanvas c = Canvas(l);
            build(c);

            foreach (StatusBarCall solid in c.Calls.Where(x => x.Kind == StatusBarCallKind.Solid))
            {
                Assert.True(
                    solid.Rect.X >= 0f && solid.Rect.Y >= 0f && solid.Rect.Right <= w + 1f && solid.Rect.Bottom <= h + 1f,
                    $"{name} plate {solid.Rect} leaves the {w}x{h} screen at scale {l.Scale}");
            }

            foreach (StatusBarCall text in c.Texts)
            {
                Assert.True(
                    text.TextPos.Y + textH <= l.Bar.Y + 1f,
                    $"{name} text at {text.TextPos.Y} (+{textH}) crosses the bar top {l.Bar.Y} at scale {l.Scale}");
                Assert.True(text.TextPos.Y >= 0f, $"{name} text at {text.TextPos.Y} is above the screen");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ThePartyPlatesBottom_IsBelowItsRows_AndAboveTheQuestPlate(int w, int h, float configured)
    {
        UiLayout l = UiLayout.Create(w, h, configured);
        HudView v = View(w, h);
        PartyHudState party = Party();
        QuestHudState quests = Quests();
        float textH = 13f * l.TextScale;

        StatusBarRecordingCanvas partyCanvas = Canvas(l);
        PartyPanel.Build(partyCanvas, v, l, party, null);
        float bottom = PartyPanel.BottomOf(partyCanvas, l, party);

        Assert.True(bottom > 0f);
        foreach (StatusBarCall text in partyCanvas.Texts)
        {
            Assert.True(text.TextPos.Y + textH <= bottom, $"party row at {text.TextPos.Y} (+{textH}) falls past the reported bottom {bottom}");
        }

        foreach (StatusBarCall solid in partyCanvas.Calls.Where(x => x.Kind == StatusBarCallKind.Solid))
        {
            Assert.True(solid.Rect.Bottom <= bottom + 1f, $"party fill {solid.Rect} falls past the reported bottom {bottom}");
        }

        // The tracker's own plate is the first and widest Solid it draws.
        StatusBarRecordingCanvas questCanvas = Canvas(l);
        QuestLogPanel.Build(questCanvas, v, l, quests, party);
        UiRect plate = questCanvas.Calls.First(x => x.Kind == StatusBarCallKind.Solid).Rect;

        Assert.True(plate.Y >= bottom, $"quest plate top {plate.Y} overlaps the party plate, which ends at {bottom}");
        Assert.True(plate.Bottom <= l.Bar.Y + 1f, $"quest plate bottom {plate.Bottom} runs under the bar top {l.Bar.Y}");
    }

    // The gem numbers and the quickbar tile glyphs come from the art's own boxes, so they ask UiText
    // for a size instead of following the default: the size must grow with the scale without ever
    // changing face, and two tile line boxes must still fit the shortest tile (the 17 u bracket).
    [Theory]
    [MemberData(nameof(Cases))]
    public void ArtBoxedText_GrowsWithTheScale_WithoutOutgrowingItsBox(int w, int h, float configured)
    {
        UiLayout l = UiLayout.Create(w, h, configured);

        UiSizedText gem = StatusBarPanel.GemNumber(in l);
        Assert.True(gem.Px >= 13f);
        Assert.True(gem.Px <= 18f * l.Scale + 1f, $"gem number {gem.Px}px overflows the 50 u stone at scale {l.Scale}");

        // The key digit sits at the tile's top and the bag count at its bottom, so two line boxes and
        // a hairline have to fit the shortest tile, the 17 u skill bracket. Up to scale 1.75 the 13 px
        // bake floor is wider than the tile and nothing can be done about it; above that the size is
        // what has to give way, and it did not before: the default 9.5 u met itself at ui_scale 4.
        UiSizedText tile = QuickbarPanel.TileText(in l);
        float shortest = 17f * l.Scale;
        Assert.True(tile.Px >= 13f);
        Assert.True(
            (2f * tile.Px) + UiText.ShadowPx(in l) <= shortest + 1f || tile.Px <= UiFontSetup.BodyPixels,
            $"the key digit and the count ({tile.Px}px each) meet in a {shortest}px tile at scale {l.Scale}");
        Assert.True(tile.Px <= UiText.For(in l, UiText.DefaultTextUnits).Px, "the tile text should never be larger than the default");
    }

    [Fact]
    public void TheGemNumberSize_IsStrictlyIncreasing_AndNeverFlipsFace()
    {
        float previous = 0f;
        UiTextStyle? face = null;
        for (float scale = 1f; scale <= 4f + 0.01f; scale += UiScale.Step)
        {
            UiLayout l = UiLayout.Create(3840, 2160, scale);
            UiSizedText t = StatusBarPanel.GemNumber(in l);
            Assert.True(t.Px > previous, $"gem number did not grow at scale {scale}: {t.Px} after {previous}");
            previous = t.Px;
            face ??= t.Style;
            // One step from the pixel fallback to the engraved capitals is allowed, and only upwards.
            Assert.True(t.Style == face || t.Style == UiTextStyle.Large, $"face flipped to {t.Style} at scale {scale}");
            face = t.Style;
        }
    }

    // ---- the preview PNGs ----

    [Theory]
    [MemberData(nameof(Cases))]
    public void EdgePanels_RenderAtEveryScale(int w, int h, float configured)
    {
        (UiGlyphTable glyphs, UiTexturePixels page) = RealFont.Get();
        HudView v = View(w, h);
        PartyHudState party = Party();
        QuestHudState quests = Quests();
        ChatHudState chat = Chat();
        string tag = configured <= 0f ? "auto" : configured.ToString("0.##", CultureInfo.InvariantCulture);

        UiSoftwareCompositor c = HudPreview.Render(
            $"edges-{tag}", w, h,
            (b, t, lay) =>
            {
                // The panels are called directly, in the HUD's own order, rather than through
                // ClassicHud: its chat, party and quest states are static, and the preview classes
                // run in parallel with everything else that draws the HUD.
                UiInputRouter router = new();
                b.BeginFrame(in lay);
                router.BeginFrame(new UiPointer(Vector2.Zero, false, false, false));
                WorldLabelsPanel.Build(b, v, in lay);
                StatusBarPanel.Build(b, router, v, in lay, t, new NoHudActions());
                PartyPanel.Build(b, v, in lay, party, null);
                QuestLogPanel.Build(b, v, in lay, quests, party);
                NoticePanel.Build(b, v, in lay);
                ChatPanel.Build(b, v, in lay, chat);
                router.EndFrame();
            },
            configured, glyphs, page, forceWrite: true);

        Assert.Equal((w, h), (c.Width, c.Height));
    }

    private sealed class NoHudActions : IHudActions
    {
        public void Inventory(InventoryHudAction action) { }

        public void CloseInventory() { }
    }

    // A recording canvas at the layout's own text scale. UiBatch.BeginFrame sets this from the
    // layout; the fake has to be told, and before 969bd497 it ignored the scale entirely, so width
    // and height assertions against it only started to mean anything here.
    private static StatusBarRecordingCanvas Canvas(in UiLayout l) =>
        new(StatusBarTestData.Table) { TextScale = l.TextScale };

    private static IEnumerable<(string Name, Action<StatusBarRecordingCanvas> Build)> Panels(
        HudView v, UiLayout l, PartyHudState party, QuestHudState quests)
    {
        yield return ("party", c => PartyPanel.Build(c, v, in l, party, null));
        yield return ("quests", c => QuestLogPanel.Build(c, v, in l, quests, party));
        yield return ("chat", c => ChatPanel.Build(c, v, in l, Chat()));
    }
}
