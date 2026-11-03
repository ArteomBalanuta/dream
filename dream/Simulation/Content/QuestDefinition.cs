namespace Dream.Simulation.Content;

public enum QuestObjectiveKind : byte
{
    // Kill Count enemies of the template named by Target (an enemies.json key).
    Kill = 1,

    // Hand in Count items of the kind named by Target (an items.json key) at turn-in.
    Collect = 2
}

public readonly record struct QuestObjective(QuestObjectiveKind Kind, string Target, int Count);

// One item a quest pays. Fixed-stat items only (validated at load): a rolled reward would need a seed.
public readonly record struct QuestRewardItem(string ItemKind, int Count);

public sealed record QuestRewards(int Xp, long Gold, IReadOnlyList<QuestRewardItem> Items);

// A quest as authored in quests.json (M6). Giver is the key of the NPC in npcs.json that offers it and takes
// it back. Kill progress is kept per player; collect progress is never stored, it is read from the bag.
public sealed record QuestDefinition(
    string Key,
    string Title,
    string Description,
    string Giver,
    IReadOnlyList<QuestObjective> Objectives,
    QuestRewards Rewards,
    int MinWorldSize = 0)
{
    // Whether the quest is offered on a floor of floorSize metres.
    public bool ExistsOn(int floorSize) => floorSize >= MinWorldSize;
}
