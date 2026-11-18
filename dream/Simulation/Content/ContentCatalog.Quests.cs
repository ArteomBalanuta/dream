using System.Globalization;
using System.Text;
using System.Text.Json;
using Dream.Simulation.World;

namespace Dream.Simulation.Content;

// quests.json (M6): the quests the quest givers of npcs.json offer. Optional: a content directory without it
// has none. Validated like the other files, with strict keys, and every reference must resolve.
public sealed partial class ContentCatalog
{
    public const string QuestsFileName = "quests.json";

    public const int MaxQuests = 32;

    // The most quests one giver may be authored with. BuildNpcDialogue sends an NPC's whole list, and the
    // dialogue window keeps the first QuestHudState.MaxOffers of them and silently drops the rest, so a
    // ninth quest on one NPC is simply unreachable - it can be neither accepted nor turned in
    // (BH-QUEST-3). The shipped content already has 7 of 8 on the gate warden, so one more authored quest
    // would have broken it with no complaint at load. Rendering cannot be referenced from the simulation,
    // so the number is repeated here and QuestGiverCapTests pins the two together. Raising it means
    // teaching the dialogue window to page or scroll: it draws MaxOffers fixed row plates from the art.
    public const int MaxQuestsPerGiver = 8;

    public const int MaxQuestObjectives = 4;
    public const int MaxQuestRewardItems = 4;
    public const int MaxQuestCount = 99;
    public const int MaxQuestXp = 100_000;

    private const string EmptyQuestsJson = "{\"version\":1,\"quests\":[]}";

    private static readonly IReadOnlyList<QuestDefinition> NoQuests = [];

    private Dictionary<string, QuestDefinition> _quests = new(StringComparer.Ordinal);
    private Dictionary<string, QuestDefinition[]> _questsByGiver = new(StringComparer.OrdinalIgnoreCase);

    // The quests in file order. Empty when the content has none.
    public IReadOnlyList<QuestDefinition> Quests { get; private set; } = NoQuests;

    // By the exact key: a quest key is stored in a character's quest log, so it is not case-folded.
    public bool TryGetQuest(string key, out QuestDefinition quest)
    {
        if (key != null && _quests.TryGetValue(key, out QuestDefinition? found))
        {
            quest = found;
            return true;
        }

        quest = null!;
        return false;
    }

    // The quests an NPC offers, in file order; empty for an NPC that offers none. Allocates nothing.
    public IReadOnlyList<QuestDefinition> QuestsFor(string npcKey)
    {
        if (npcKey != null && _questsByGiver.TryGetValue(npcKey, out QuestDefinition[]? list))
        {
            return list;
        }

        return NoQuests;
    }

    private void SetQuests(List<QuestDefinition> quests)
    {
        Quests = quests;
        _quests = new Dictionary<string, QuestDefinition>(StringComparer.Ordinal);
        Dictionary<string, List<QuestDefinition>> byGiver = new(StringComparer.OrdinalIgnoreCase);
        foreach (QuestDefinition quest in quests)
        {
            _quests[quest.Key] = quest;
            if (!byGiver.TryGetValue(quest.Giver, out List<QuestDefinition>? list))
            {
                list = [];
                byGiver[quest.Giver] = list;
            }

            list.Add(quest);
        }

        _questsByGiver = new Dictionary<string, QuestDefinition[]>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<QuestDefinition>> entry in byGiver)
        {
            _questsByGiver[entry.Key] = [.. entry.Value];
        }
    }

    private static List<QuestDefinition> ParseQuests(string json, ParsedItems items, ParsedEnemies enemies, List<NpcDefinition> npcs)
    {
        using JsonDocument document = ParseDocument(QuestsFileName, json);
        JsonElement root = document.RootElement;
        ExpectObject(QuestsFileName, "$", root);
        Dictionary<string, JsonElement> properties = ReadProperties(QuestsFileName, "$", root, ["version", "quests"]);
        _ = ReadInt(QuestsFileName, "$.version", properties["version"], 1, 1);

        HashSet<string> npcKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (NpcDefinition npc in npcs)
        {
            npcKeys.Add(npc.Key);
        }

        List<QuestDefinition> list = [];
        HashSet<string> keys = new(StringComparer.Ordinal);

        // Quests per giver, folded the way QuestsFor groups them. Counted as the file is read, so the
        // quest that breaks the cap is the one named in the failure - and the check is file-order
        // deterministic.
        Dictionary<string, int> perGiver = new(StringComparer.OrdinalIgnoreCase);
        JsonElement questsElement = properties["quests"];
        ExpectArray(QuestsFileName, "$.quests", questsElement);
        int index = 0;
        foreach (JsonElement questElement in questsElement.EnumerateArray())
        {
            string path = $"$.quests[{index}]";
            if (index >= MaxQuests)
            {
                throw Fail(QuestsFileName, "$.quests", $"more than {MaxQuests} quests");
            }

            ExpectObject(QuestsFileName, path, questElement);
            Dictionary<string, JsonElement> quest = ReadProperties(
                QuestsFileName,
                path,
                questElement,
                ["key", "title", "description", "giver", "objectives", "rewards"],
                optional: ["minWorldSize"]);
            string key = ReadKey(QuestsFileName, $"{path}.key", quest["key"]);
            if (!keys.Add(key))
            {
                throw Fail(QuestsFileName, $"{path}.key", $"duplicate quest key '{key}'");
            }

            string giver = ReadKey(QuestsFileName, $"{path}.giver", quest["giver"]);
            if (!npcKeys.Contains(giver))
            {
                throw Fail(QuestsFileName, $"{path}.giver", $"references missing npc '{Truncate(giver)}'");
            }

            int given = perGiver.GetValueOrDefault(giver) + 1;
            perGiver[giver] = given;
            if (given > MaxQuestsPerGiver)
            {
                throw Fail(
                    QuestsFileName,
                    $"{path}.giver",
                    $"npc '{Truncate(giver)}' is given more than {MaxQuestsPerGiver} quests; "
                        + "the dialogue window shows only the first few, so the rest would be unreachable");
            }

            List<QuestObjective> objectives = ParseQuestObjectives($"{path}.objectives", quest["objectives"], items, enemies);
            QuestRewards rewards = ParseQuestRewards($"{path}.rewards", quest["rewards"], items);
            string title = ReadName(QuestsFileName, $"{path}.title", quest["title"]);
            string description = ReadText(QuestsFileName, $"{path}.description", quest["description"], MaxDescriptionLength);
            int minWorldSize = 0;
            if (quest.TryGetValue("minWorldSize", out JsonElement minElement))
            {
                minWorldSize = ReadInt(QuestsFileName, $"{path}.minWorldSize", minElement, WorldDescriptor.MinHorizontalSize, WorldDescriptor.MaxHorizontalSize);
            }

            list.Add(new QuestDefinition(key, title, description, giver, objectives, rewards, minWorldSize));
            index++;
        }

        return list;
    }

    private static List<QuestObjective> ParseQuestObjectives(string path, JsonElement element, ParsedItems items, ParsedEnemies enemies)
    {
        ExpectArray(QuestsFileName, path, element);
        int length = element.GetArrayLength();
        if (length < 1 || length > MaxQuestObjectives)
        {
            throw Fail(QuestsFileName, path, $"must have 1 to {MaxQuestObjectives} objectives");
        }

        List<QuestObjective> objectives = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement objectiveElement in element.EnumerateArray())
        {
            string objectivePath = $"{path}[{index}]";
            ExpectObject(QuestsFileName, objectivePath, objectiveElement);
            Dictionary<string, JsonElement> objective = ReadProperties(QuestsFileName, objectivePath, objectiveElement, ["kind", "target", "count"]);
            string kindName = ReadString(QuestsFileName, $"{objectivePath}.kind", objective["kind"]);
            string target = ReadKey(QuestsFileName, $"{objectivePath}.target", objective["target"]);
            int count = ReadInt(QuestsFileName, $"{objectivePath}.count", objective["count"], 1, MaxQuestCount);
            QuestObjective parsed;
            switch (kindName)
            {
                case "Kill":
                    if (!enemies.ByKey.TryGetValue(target, out EnemyTemplate? template))
                    {
                        throw Fail(QuestsFileName, $"{objectivePath}.target", $"references missing enemy '{Truncate(target)}'");
                    }

                    parsed = new QuestObjective(QuestObjectiveKind.Kill, template.Key, count);
                    break;
                case "Collect":
                    if (!items.ByKey.TryGetValue(target, out ItemDefinition? item))
                    {
                        throw Fail(QuestsFileName, $"{objectivePath}.target", $"references missing item '{Truncate(target)}'");
                    }

                    parsed = new QuestObjective(QuestObjectiveKind.Collect, item.Key, count);
                    break;
                default:
                    throw Fail(QuestsFileName, $"{objectivePath}.kind", $"unknown objective kind '{Truncate(kindName)}' (Kill or Collect)");
            }

            if (!seen.Add(parsed.Kind + "|" + parsed.Target))
            {
                throw Fail(QuestsFileName, objectivePath, "repeats an objective of the same kind and target");
            }

            objectives.Add(parsed);
            index++;
        }

        return objectives;
    }

    private static QuestRewards ParseQuestRewards(string path, JsonElement element, ParsedItems items)
    {
        ExpectObject(QuestsFileName, path, element);
        Dictionary<string, JsonElement> rewards = ReadProperties(QuestsFileName, path, element, ["xp", "gold", "items"]);
        int xp = ReadInt(QuestsFileName, $"{path}.xp", rewards["xp"], 0, MaxQuestXp);
        int gold = ReadInt(QuestsFileName, $"{path}.gold", rewards["gold"], 0, MaxGoldAmount);
        JsonElement itemsElement = rewards["items"];
        ExpectArray(QuestsFileName, $"{path}.items", itemsElement);
        if (itemsElement.GetArrayLength() > MaxQuestRewardItems)
        {
            throw Fail(QuestsFileName, $"{path}.items", $"has more than {MaxQuestRewardItems} entries");
        }

        List<QuestRewardItem> rewardItems = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement entryElement in itemsElement.EnumerateArray())
        {
            string entryPath = $"{path}.items[{index}]";
            ExpectObject(QuestsFileName, entryPath, entryElement);
            Dictionary<string, JsonElement> entry = ReadProperties(QuestsFileName, entryPath, entryElement, ["item", "count"]);
            string itemKey = ReadKey(QuestsFileName, $"{entryPath}.item", entry["item"]);
            if (!items.ByKey.TryGetValue(itemKey, out ItemDefinition? item))
            {
                throw Fail(QuestsFileName, $"{entryPath}.item", $"references missing item '{Truncate(itemKey)}'");
            }

            // A reward is a fixed item: a rolled one would need a seed per turn-in.
            if (item.Rolls != null)
            {
                throw Fail(QuestsFileName, $"{entryPath}.item", $"'{item.Key}' is a rolled item; a quest rewards fixed-stat items only");
            }

            if (!seen.Add(item.Key))
            {
                throw Fail(QuestsFileName, $"{entryPath}.item", $"duplicate reward item '{item.Key}'");
            }

            rewardItems.Add(new QuestRewardItem(item.Key, ReadInt(QuestsFileName, $"{entryPath}.count", entry["count"], 1, MaxQuestCount)));
            index++;
        }

        return new QuestRewards(xp, gold, rewardItems);
    }

    private static void AppendQuestDigest(StringBuilder text, List<QuestDefinition> quests)
    {
        if (quests.Count == 0)
        {
            return;
        }

        CultureInfo c = CultureInfo.InvariantCulture;
        text.Append("quests.v1\n");
        foreach (QuestDefinition quest in quests)
        {
            text.Append("quest|");
            AppendField(text, quest.Key).Append('|');
            AppendField(text, quest.Title).Append('|');
            AppendField(text, quest.Description).Append('|');
            AppendField(text, quest.Giver).Append('|')
                .Append(quest.Rewards.Xp.ToString(c)).Append('|').Append(quest.Rewards.Gold.ToString(c));
            if (quest.MinWorldSize > 0)
            {
                text.Append("|min|").Append(quest.MinWorldSize.ToString(c));
            }

            text.Append('\n');
            foreach (QuestObjective objective in quest.Objectives)
            {
                text.Append("objective|").Append(objective.Kind.ToString()).Append('|');
                AppendField(text, objective.Target).Append('|').Append(objective.Count.ToString(c)).Append('\n');
            }

            foreach (QuestRewardItem reward in quest.Rewards.Items)
            {
                text.Append("reward|");
                AppendField(text, reward.ItemKind).Append('|').Append(reward.Count.ToString(c)).Append('\n');
            }
        }
    }
}
