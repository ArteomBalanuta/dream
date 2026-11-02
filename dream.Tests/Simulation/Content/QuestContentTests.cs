using Dream.Simulation.Content;
using Xunit;

namespace Dream.Tests.Simulation.Content;

// M6 workstream B: quests.json in the content loader, strict like the other content files, and the shipped
// quests the gate warden gives.
public sealed class QuestContentTests
{
    private static string Shipped(string file) => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, file));

    private static ContentCatalog Load(string questsJson) =>
        ContentCatalog.Load(
            Shipped(ContentCatalog.ItemsFileName),
            Shipped(ContentCatalog.EnemiesFileName),
            Shipped(ContentCatalog.LootFileName),
            Shipped(ContentCatalog.SpawnsFileName),
            Shipped(ContentCatalog.NpcsFileName),
            Shipped(ContentCatalog.SkillsFileName),
            questsJson);

    private static string One(string objectives = "[{\"kind\":\"Kill\",\"target\":\"Wolf\",\"count\":2}]", string rewards = "{\"xp\":10,\"gold\":5,\"items\":[]}", string giver = "gate-warden", string key = "q") =>
        "{\"version\":1,\"quests\":[{\"key\":\"" + key + "\",\"title\":\"T\",\"description\":\"D\",\"giver\":\"" + giver + "\",\"objectives\":" + objectives + ",\"rewards\":" + rewards + "}]}";

    private static ContentValidationException Rejected(string json) => Assert.Throws<ContentValidationException>(() => Load(json));

    [Fact]
    public void TheShippedQuests_AreGivenByTheGateWarden_AndPayFixedStatItemsOnly()
    {
        ContentCatalog catalog = ContentCatalog.Shipped;

        Assert.Equal(["wolf-cull", "torch-supply", "pale-eyes", "marked-for-the-dark"], catalog.Quests.Select(q => q.Key));
        NpcDefinition warden = Assert.Single(catalog.Npcs, npc => npc.Key == "gate-warden");
        Assert.Empty(warden.Stock);
        Assert.False(string.IsNullOrWhiteSpace(warden.Greeting));
        Assert.Equal(catalog.Quests.Count, catalog.QuestsFor("gate-warden").Count);
        Assert.Empty(catalog.QuestsFor("spawn-quartermaster"));
        Assert.Empty(catalog.QuestsFor("nobody"));
        foreach (QuestDefinition quest in catalog.Quests)
        {
            Assert.True(catalog.TryGetQuest(quest.Key, out QuestDefinition? found));
            Assert.Same(quest, found);
            foreach (QuestRewardItem reward in quest.Rewards.Items)
            {
                Assert.True(catalog.TryGetItem(reward.ItemKind, out ItemDefinition? item));
                Assert.Null(item.Rolls);
            }
        }

        QuestDefinition mixed = catalog.Quests.Single(q => q.Key == "marked-for-the-dark");
        Assert.Equal(
            [QuestObjectiveKind.Kill, QuestObjectiveKind.Collect],
            mixed.Objectives.Select(o => o.Kind));
    }

    [Fact]
    public void TheShippedQuests_UseTheCampEnemiesThatAreShipped()
    {
        ContentCatalog catalog = ContentCatalog.Shipped;
        foreach (QuestObjective objective in catalog.Quests.SelectMany(q => q.Objectives).Where(o => o.Kind == QuestObjectiveKind.Kill))
        {
            Assert.Contains(catalog.Camps, camp => string.Equals(camp.Template, objective.Target, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void QuestLookup_IsExact_AndUnknownKeysAreNotFound()
    {
        ContentCatalog catalog = ContentCatalog.Shipped;

        Assert.False(catalog.TryGetQuest("WOLF-CULL", out _));
        Assert.False(catalog.TryGetQuest("", out _));
        Assert.False(catalog.TryGetQuest(null!, out _));
    }

    [Fact]
    public void WithoutAQuestsFile_ThereAreNoQuests_AndTheDigestIgnoresTheMissingFile()
    {
        ContentCatalog none = Load("{\"version\":1,\"quests\":[]}");
        ContentCatalog withQuests = Load(Shipped(ContentCatalog.QuestsFileName));

        Assert.Empty(none.Quests);
        Assert.Empty(none.QuestsFor("gate-warden"));
        Assert.NotEqual(none.ContentDigest, withQuests.ContentDigest);
        Assert.Equal(ContentCatalog.Shipped.ContentDigest, withQuests.ContentDigest);
    }

    [Fact]
    public void ADirectoryWithoutQuestsJson_LoadsWithNoQuests()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dream-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string file in Directory.GetFiles(ContentCatalog.ShippedDirectory, "*.json"))
            {
                if (!string.Equals(Path.GetFileName(file), ContentCatalog.QuestsFileName, StringComparison.Ordinal))
                {
                    File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
                }
            }

            Assert.Empty(ContentCatalog.LoadFromDirectory(directory).Quests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheDigest_ChangesWithAQuestReward()
    {
        string json = Shipped(ContentCatalog.QuestsFileName);
        ContentCatalog changed = Load(json.Replace("\"xp\": 100,", "\"xp\": 101,", StringComparison.Ordinal));

        Assert.NotEqual(ContentCatalog.Shipped.ContentDigest, changed.ContentDigest);
    }

    [Fact]
    public void AValidQuest_Loads_WithTheTemplateAndItemKeysCanonical()
    {
        ContentCatalog catalog = Load(One(objectives: "[{\"kind\":\"Kill\",\"target\":\"wolf\",\"count\":2},{\"kind\":\"Collect\",\"target\":\"TOOL.TORCH\",\"count\":1}]"));

        QuestDefinition quest = Assert.Single(catalog.Quests);
        Assert.Equal("Wolf", quest.Objectives[0].Target);
        Assert.Equal("tool.torch", quest.Objectives[1].Target);
    }

    [Theory]
    [InlineData("giver", "nobody")]
    [InlineData("enemy", "Dragon")]
    [InlineData("item", "tool.nothing")]
    [InlineData("rewardItem", "tool.nothing")]
    [InlineData("rolledReward", "gear.iron-blade")]
    public void AnUnknownReference_OrARolledReward_IsRejected(string what, string value)
    {
        string json = what switch
        {
            "giver" => One(giver: value),
            "enemy" => One(objectives: "[{\"kind\":\"Kill\",\"target\":\"" + value + "\",\"count\":1}]"),
            "item" => One(objectives: "[{\"kind\":\"Collect\",\"target\":\"" + value + "\",\"count\":1}]"),
            _ => One(rewards: "{\"xp\":1,\"gold\":1,\"items\":[{\"item\":\"" + value + "\",\"count\":1}]}"),
        };

        ContentValidationException error = Rejected(json);
        Assert.Contains("quests.json", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-1)]
    public void AnObjectiveCountOutsideOneToNinetyNine_IsRejected(int count)
    {
        Rejected(One(objectives: "[{\"kind\":\"Kill\",\"target\":\"Wolf\",\"count\":" + count + "}]"));
    }

    [Fact]
    public void ObjectiveShapes_AreChecked()
    {
        Rejected(One(objectives: "[]"));
        Rejected(One(objectives: "[{\"kind\":\"Kill\",\"target\":\"Wolf\",\"count\":1},{\"kind\":\"Kill\",\"target\":\"DireWolf\",\"count\":1},{\"kind\":\"Collect\",\"target\":\"tool.torch\",\"count\":1},{\"kind\":\"Collect\",\"target\":\"potion.health\",\"count\":1},{\"kind\":\"Collect\",\"target\":\"potion.mana\",\"count\":1}]"));
        Rejected(One(objectives: "[{\"kind\":\"Explore\",\"target\":\"Wolf\",\"count\":1}]"));
        Rejected(One(objectives: "[{\"kind\":\"Kill\",\"target\":\"Wolf\",\"count\":1},{\"kind\":\"Kill\",\"target\":\"Wolf\",\"count\":2}]"));
        Rejected(One(objectives: "[{\"kind\":\"Kill\",\"target\":\"Wolf\",\"count\":1,\"extra\":true}]"));
    }

    [Fact]
    public void RewardShapes_AreChecked()
    {
        Rejected(One(rewards: "{\"xp\":-1,\"gold\":0,\"items\":[]}"));
        Rejected(One(rewards: "{\"xp\":0,\"gold\":-1,\"items\":[]}"));
        Rejected(One(rewards: "{\"xp\":0,\"gold\":0}"));
        Rejected(One(rewards: "{\"xp\":0,\"gold\":0,\"items\":[{\"item\":\"potion.health\",\"count\":1},{\"item\":\"potion.health\",\"count\":1}]}"));
        Rejected(One(rewards: "{\"xp\":0,\"gold\":0,\"items\":[{\"item\":\"potion.health\",\"count\":0}]}"));
        Rejected(One(rewards: "{\"xp\":0,\"gold\":0,\"items\":[{\"item\":\"potion.health\",\"count\":1},{\"item\":\"potion.mana\",\"count\":1},{\"item\":\"tool.torch\",\"count\":1},{\"item\":\"tool.field-blade\",\"count\":1},{\"item\":\"tool.stone-maul\",\"count\":1}]}"));
    }

    [Fact]
    public void DuplicateKeys_UnknownProperties_AndTooManyQuests_AreRejected()
    {
        string quest = "{\"key\":\"K\",\"title\":\"T\",\"description\":\"D\",\"giver\":\"gate-warden\",\"objectives\":[{\"kind\":\"Kill\",\"target\":\"Wolf\",\"count\":1}],\"rewards\":{\"xp\":0,\"gold\":0,\"items\":[]}}";
        Rejected("{\"version\":1,\"quests\":[" + quest + "," + quest + "]}");
        Rejected("{\"version\":1,\"quests\":[" + quest.Replace("\"giver\"", "\"extra\":1,\"giver\"", StringComparison.Ordinal) + "]}");
        Rejected("{\"version\":2,\"quests\":[]}");
        Rejected("{\"version\":1}");

        string many = string.Join(",", Enumerable.Range(0, ContentCatalog.MaxQuests + 1).Select(n => quest.Replace("\"K\"", "\"K" + n + "\"", StringComparison.Ordinal)));
        Rejected("{\"version\":1,\"quests\":[" + many + "]}");
        string exactly = string.Join(",", Enumerable.Range(0, ContentCatalog.MaxQuests).Select(n => quest.Replace("\"K\"", "\"K" + n + "\"", StringComparison.Ordinal)));
        Assert.Equal(ContentCatalog.MaxQuests, Load("{\"version\":1,\"quests\":[" + exactly + "]}").Quests.Count);
    }

    [Fact]
    public void ABlankOrControlTitle_IsRejected()
    {
        Rejected(One().Replace("\"title\":\"T\"", "\"title\":\"  \"", StringComparison.Ordinal));
        Rejected(One().Replace("\"title\":\"T\"", "\"title\":\"a\\u0000b\"", StringComparison.Ordinal));
    }
}
