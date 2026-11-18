using System.Text.Json.Nodes;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Content;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-CONTENT-5: authored text wider than the panel that draws it was accepted and silently cut. A quest
// description could be 512 characters while the dialogue box keeps QuestHudState.MaxDescriptionChars; an
// item description likewise, against ItemOverlays' text cap; and an item could author 16 detailLines
// while the tooltip draws ItemOverlays.MaxDetailLines and stops. The tail was never shown to any player
// and nothing said so at load. The loader now caps each at what the panel honours. (The NPC greeting cap
// was already the same number - the quest and item descriptions were the ones left unaligned.)
public sealed class ContentPanelTextCapTests
{
    private const string Item = "tool.torch";

    private static string QuestWithDescription(int length) =>
        "{\"version\":1,\"quests\":[{\"key\":\"q\",\"title\":\"T\",\"description\":\"" + new string('d', length)
        + "\",\"giver\":\"gate-warden\",\"objectives\":[{\"kind\":\"Kill\",\"target\":\"Wolf\",\"count\":1}],"
        + "\"rewards\":{\"xp\":10,\"gold\":5,\"items\":[]}}]}";

    private static JsonArray Lines(int count) => [.. Enumerable.Range(0, count).Select(n => (JsonNode)JsonValue.Create("line " + n)!)];

    // The caps are the panels', so the numbers must stay in step.
    [Fact]
    public void TheAuthoringCaps_AreWhatThePanelsDraw()
    {
        Assert.Equal(QuestHudState.MaxDescriptionChars, ContentCatalog.MaxPanelDescriptionLength);
        Assert.Equal(ItemOverlays.MaxDetailLines, ContentCatalog.MaxDetailLines);
        // The NPC greeting was already aligned; it is the precedent this follows.
        Assert.Equal(ContentCatalog.MaxPanelDescriptionLength, ContentCatalog.MaxNpcGreetingLength);
    }

    // The shipped content still loads and sits well inside every cap.
    [Fact]
    public void TheShippedContent_LoadsAndIsInsideEveryCap()
    {
        ContentCatalog shipped = ContentCatalog.Shipped;

        Assert.All(shipped.Items, item =>
        {
            Assert.InRange(item.Description.Length, 0, ContentCatalog.MaxPanelDescriptionLength);
            Assert.InRange(item.DetailLines.Count, 0, ContentCatalog.MaxDetailLines);
        });
        Assert.NotEmpty(shipped.Quests);
        Assert.All(shipped.Quests, quest => Assert.InRange(quest.Description.Length, 0, ContentCatalog.MaxPanelDescriptionLength));
    }

    // Exactly at the limit: accepted, and the whole string survives the parse.
    [Fact]
    public void AQuestDescriptionAtTheLimit_Loads()
    {
        ContentCatalog catalog = ContentBugHunt.Load(questsJson: QuestWithDescription(ContentCatalog.MaxPanelDescriptionLength));

        Assert.True(catalog.TryGetQuest("q", out QuestDefinition? quest));
        Assert.Equal(ContentCatalog.MaxPanelDescriptionLength, quest!.Description.Length);
    }

    [Fact]
    public void AQuestDescriptionPastTheLimit_IsRefusedAtLoad()
    {
        ContentValidationException failure = Assert.Throws<ContentValidationException>(
            () => ContentBugHunt.Load(questsJson: QuestWithDescription(ContentCatalog.MaxPanelDescriptionLength + 1)));

        Assert.Contains(ContentCatalog.QuestsFileName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("description", failure.Message, StringComparison.Ordinal);
        Assert.Contains(ContentCatalog.MaxPanelDescriptionLength.ToString(), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnItemDescriptionAtTheLimit_Loads()
    {
        string text = new('d', ContentCatalog.MaxPanelDescriptionLength);

        ContentCatalog catalog = ContentBugHunt.Load(
            itemsJson: ContentBugHunt.ItemsWith(Item, "description", JsonValue.Create(text)));

        Assert.True(catalog.TryGetItem(Item, out ItemDefinition? item));
        Assert.Equal(text, item!.Description);
    }

    [Fact]
    public void AnItemDescriptionPastTheLimit_IsRefusedAtLoad()
    {
        ContentValidationException failure = Assert.Throws<ContentValidationException>(
            () => ContentBugHunt.Load(
                itemsJson: ContentBugHunt.ItemsWith(Item, "description", JsonValue.Create(new string('d', ContentCatalog.MaxPanelDescriptionLength + 1)))));

        Assert.Contains(ContentCatalog.ItemsFileName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("description", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnItemAtTheDetailLineLimit_LoadsAndKeepsEveryLine()
    {
        ContentCatalog catalog = ContentBugHunt.Load(
            itemsJson: ContentBugHunt.ItemsWith(Item, "detailLines", Lines(ContentCatalog.MaxDetailLines)));

        Assert.True(catalog.TryGetItem(Item, out ItemDefinition? item));
        Assert.Equal(ContentCatalog.MaxDetailLines, item!.DetailLines.Count);
    }

    [Fact]
    public void AnItemOneDetailLinePastTheLimit_IsRefusedAtLoad()
    {
        ContentValidationException failure = Assert.Throws<ContentValidationException>(
            () => ContentBugHunt.Load(
                itemsJson: ContentBugHunt.ItemsWith(Item, "detailLines", Lines(ContentCatalog.MaxDetailLines + 1))));

        Assert.Contains("detailLines", failure.Message, StringComparison.Ordinal);
        Assert.Contains(ContentCatalog.MaxDetailLines.ToString(), failure.Message, StringComparison.Ordinal);
    }

    // Control: a skill description is never drawn by a panel, so it keeps the wider cap - this fix
    // narrowed only the two fields a panel cuts.
    [Fact]
    public void ASkillDescription_KeepsTheWiderCap()
    {
        Assert.True(ContentCatalog.MaxDescriptionLength > ContentCatalog.MaxPanelDescriptionLength);
    }
}
