using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Repositories;

// player_quests (migration 0014): one row per quest a player accepted in a world, keyed by
// (world_id, player_name, quest_key). State 1 is active, 2 is completed. Progress is the comma-separated kill
// counts of the objectives. A completed row is final: no statement here changes or deletes it, except that
// CompleteQuest is what makes it final and throws when there is no active row to complete.
public sealed class SqliteQuestRepository(IWorldStore store, WorldId worldId)
    : SqliteRepositoryBase(store, worldId), IQuestRepository
{
    private const string LoadSql = """
        SELECT player_name, quest_key, state, progress, updated_utc
        FROM player_quests
        WHERE world_id = $worldId AND player_name = $playerName
        ORDER BY quest_key;
        """;

    // On a conflict only an active row changes, and only its progress: the state never moves back.
    private const string UpsertSql = """
        INSERT INTO player_quests (world_id, player_name, quest_key, state, progress, updated_utc)
        VALUES ($worldId, $playerName, $questKey, $state, $progress, $updatedUtc)
        ON CONFLICT(world_id, player_name, quest_key) DO UPDATE SET
            progress = excluded.progress,
            updated_utc = excluded.updated_utc
        WHERE player_quests.state = 1;
        """;

    private const string CompleteSql = """
        UPDATE player_quests
        SET state = 2, updated_utc = $updatedUtc
        WHERE world_id = $worldId AND player_name = $playerName AND quest_key = $questKey AND state = 1;
        """;

    private const string DeleteSql = """
        DELETE FROM player_quests
        WHERE world_id = $worldId AND player_name = $playerName AND quest_key = $questKey AND state = 1;
        """;

    public IReadOnlyList<PersistedQuest> LoadPlayerQuests(string playerName)
    {
        using SqliteSession session = Store.OpenSession();
        using SqliteCommand command = session.CreateCommand(LoadSql);
        command.BindInt64("$worldId", WorldIdValue);
        command.BindText("$playerName", playerName.Trim());
        using SqliteDataReader reader = command.ExecuteReader();
        List<PersistedQuest> quests = [];
        while (reader.Read())
        {
            quests.Add(new PersistedQuest(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetUtc(4)));
        }

        return quests;
    }

    public void SaveQuestProgress(PersistedQuest quest) =>
        Execute(UpsertSql, quest, static (command, q) =>
        {
            command.BindText("$playerName", q.PlayerName.Trim());
            command.BindText("$questKey", q.QuestKey);
            command.BindInt64("$state", q.State);
            command.BindText("$progress", q.Progress ?? string.Empty);
            command.BindUtc("$updatedUtc", q.UpdatedUtc);
        });

    public void CompleteQuest(string playerName, string questKey, DateTime utc)
    {
        int changed = Execute(CompleteSql, (playerName, questKey, utc), static (command, d) =>
        {
            command.BindText("$playerName", d.playerName.Trim());
            command.BindText("$questKey", d.questKey);
            command.BindUtc("$updatedUtc", d.utc);
        });
        if (changed != 1)
        {
            throw new InvalidOperationException($"{playerName} has no active quest '{questKey}' to complete.");
        }
    }

    public void DeleteQuest(string playerName, string questKey) =>
        Execute(DeleteSql, (playerName, questKey), static (command, d) =>
        {
            command.BindText("$playerName", d.playerName.Trim());
            command.BindText("$questKey", d.questKey);
        });
}
