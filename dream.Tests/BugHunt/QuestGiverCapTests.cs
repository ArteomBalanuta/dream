using System.Text;
using Dream.Rendering.Hud;
using Dream.Simulation.Content;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-QUEST-3: a giver with more than QuestHudState.MaxOffers quests silently lost the rest - the client
// keeps the first eight offers of an NPC and drops the remainder, so those quests could be neither accepted
// nor turned in, and nothing said so at load. The shipped content already has 7 of 8 on the gate warden, so
// one more authored quest would have broken it quietly. quests.json now fails to load instead, which is the
// repo's content convention: every reference must resolve, and a cap is a load-time failure, not a clamp.
//
// Validated rather than raised: eight is what the dialogue window's art has row plates for, so raising the
// cap means teaching that window to page or scroll, which is not a content fix.
public sealed class QuestGiverCapTests
{
    private static string ShippedFile(string file) => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, file));

    private static ContentCatalog Load(string questsJson) =>
        ContentCatalog.Load(
            ShippedFile(ContentCatalog.ItemsFileName),
            ShippedFile(ContentCatalog.EnemiesFileName),
            ShippedFile(ContentCatalog.LootFileName),
            ShippedFile(ContentCatalog.SpawnsFileName),
            ShippedFile(ContentCatalog.NpcsFileName),
            ShippedFile(ContentCatalog.SkillsFileName),
            questsJson);

    private static string QuestsFor(string giver, int count) => QuestsFor((giver, count, "q"));

    // quests.json holding the given number of quests for each giver, in the order listed.
    private static string QuestsFor(params (string Giver, int Count, string KeyPrefix)[] givers)
    {
        StringBuilder json = new("{\"version\":1,\"quests\":[");
        bool first = true;
        foreach ((string giver, int count, string prefix) in givers)
        {
            for (int i = 0; i < count; i++)
            {
                if (!first)
                {
                    json.Append(',');
                }

                first = false;
                json.Append("{\"key\":\"").Append(prefix).Append(i)
                    .Append("\",\"title\":\"T\",\"description\":\"D\",\"giver\":\"")
                    .Append(giver)
                    .Append("\",\"objectives\":[{\"kind\":\"Kill\",\"target\":\"Wolf\",\"count\":1}],")
                    .Append("\"rewards\":{\"xp\":10,\"gold\":5,\"items\":[]}}");
            }
        }

        return json.Append("]}").ToString();
    }

    // The cap is the client's, so the two numbers must stay equal.
    [Fact]
    public void TheAuthoringCap_IsTheNumberOfOffersTheDialogueShows()
    {
        Assert.Equal(QuestHudState.MaxOffers, ContentCatalog.MaxQuestsPerGiver);
        Assert.True(ContentCatalog.MaxQuestsPerGiver <= ContentCatalog.MaxQuests);
    }

    // The shipped content is one short of the cap and still loads, and the warden gives all of them.
    [Fact]
    public void TheShippedContent_LoadsAndIsUnderTheCap()
    {
        ContentCatalog shipped = ContentCatalog.Shipped;

        Assert.Equal(7, shipped.QuestsFor("gate-warden").Count);
        Assert.InRange(shipped.QuestsFor("gate-warden").Count, 1, ContentCatalog.MaxQuestsPerGiver);
        foreach (NpcDefinition npc in shipped.Npcs)
        {
            Assert.InRange(shipped.QuestsFor(npc.Key).Count, 0, ContentCatalog.MaxQuestsPerGiver);
        }
    }

    // Control: exactly the cap still loads, and every quest of it is reachable through QuestsFor.
    [Fact]
    public void AGiverWithExactlyTheCap_StillLoads()
    {
        ContentCatalog catalog = Load(QuestsFor("gate-warden", ContentCatalog.MaxQuestsPerGiver));

        Assert.Equal(ContentCatalog.MaxQuestsPerGiver, catalog.QuestsFor("gate-warden").Count);
        Assert.Equal(ContentCatalog.MaxQuestsPerGiver, catalog.Quests.Count);
    }

    // The defect: one more is now a loud load failure, naming the quest that broke the cap.
    [Fact]
    public void AGiverWithOneMoreThanTheCap_FailsToLoad_NamingTheQuestAndTheGiver()
    {
        ContentValidationException failure = Assert.Throws<ContentValidationException>(
            () => Load(QuestsFor("gate-warden", ContentCatalog.MaxQuestsPerGiver + 1)));

        Assert.Contains(ContentCatalog.QuestsFileName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("gate-warden", failure.Message, StringComparison.Ordinal);
        Assert.Contains("quests[" + ContentCatalog.MaxQuestsPerGiver + "]", failure.Message, StringComparison.Ordinal);
    }

    // Control: the cap is per giver, not a total. Two givers may each hold the cap.
    [Fact]
    public void TwoGiversMayEachHoldTheCap()
    {
        ContentCatalog catalog = Load(QuestsFor(
            ("gate-warden", ContentCatalog.MaxQuestsPerGiver, "q"),
            ("spawn-quartermaster", ContentCatalog.MaxQuestsPerGiver, "p")));

        Assert.Equal(ContentCatalog.MaxQuestsPerGiver, catalog.QuestsFor("gate-warden").Count);
        Assert.Equal(ContentCatalog.MaxQuestsPerGiver, catalog.QuestsFor("spawn-quartermaster").Count);
    }
}
