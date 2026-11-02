namespace Dream.Simulation.Persistence;

// One row of player_quests (M6). State is a QuestState value (1 active, 2 completed); Progress is the
// comma-separated kill counts of the objectives, clamped on load.
public sealed record PersistedQuest(string PlayerName, string QuestKey, int State, string Progress, DateTime UpdatedUtc);

// player_quests: one row per quest a player accepted in a world. Every member has a default body, so a
// fake without quests keeps compiling.
public interface IQuestRepository
{
    IReadOnlyList<PersistedQuest> LoadPlayerQuests(string playerName) => [];

    // Upsert. ON CONFLICT updates only WHERE state = 1, so a completed quest is never changed.
    void SaveQuestProgress(PersistedQuest quest)
    {
    }

    // SQLite: state 1 to 2, THROWS unless exactly one row changed, so a double turn-in stops in the
    // enclosing closure.
    void CompleteQuest(string playerName, string questKey, DateTime utc)
    {
    }

    // Only while the quest is still active (state = 1).
    void DeleteQuest(string playerName, string questKey)
    {
    }
}
