using Dream.Server.Persistence.Store;
using Dream.Simulation.Characters;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Repositories;

// Accounts and character ownership (migration 0007). Global: not bound to a world. Lookup is read-only;
// RecordJoin enlists in the ambient write batch (one savepoint) when there is one. A token hash is stored
// and compared as 32 bytes and never printed.
public sealed class SqliteAccountRepository(SqliteSchemaBootstrapper schema)
    : SqliteRepositoryBase(new SqliteWorldStore(schema)), IAccountRepository
{
    private const string LookupSql = """
        SELECT p.player_name, p.display_name, a.token_hash
        FROM player_profiles AS p
        LEFT JOIN accounts AS a ON a.account_id = p.account_id
        WHERE p.display_name = $name COLLATE NOCASE;
        """;

    private const string KeyReservedSql = "SELECT EXISTS(SELECT 1 FROM player_profiles WHERE player_name = $name);";

    private const string CountSql = """
        SELECT COUNT(*)
        FROM player_profiles AS p
        JOIN accounts AS a ON a.account_id = p.account_id
        WHERE a.token_hash = $hash;
        """;

    private const string ListSql = """
        SELECT p.display_name, p.level
        FROM player_profiles AS p
        JOIN accounts AS a ON a.account_id = p.account_id
        WHERE a.token_hash = $hash
        ORDER BY p.updated_utc DESC, p.display_name
        LIMIT $max;
        """;

    private const string UpsertAccountSql = """
        INSERT INTO accounts (token_hash, created_utc, last_seen_utc)
        VALUES ($hash, $now, $now)
        ON CONFLICT(token_hash) DO UPDATE SET last_seen_utc = excluded.last_seen_utc;
        """;

    private const string ClaimSql = """
        UPDATE player_profiles
        SET account_id = (SELECT account_id FROM accounts WHERE token_hash = $hash)
        WHERE player_name = $key AND account_id IS NULL;
        """;

    private const string StandingSql = """
        SELECT role, banned_until_utc, ban_reason, muted_until_utc
        FROM accounts
        WHERE token_hash = $hash;
        """;

    // The account that owns the character with this display name (NOCASE, as Lookup finds it).
    private const string OwnerOfNameSql = "(SELECT account_id FROM player_profiles WHERE display_name = $name COLLATE NOCASE)";

    private const string SetBanSql =
        "UPDATE accounts SET banned_until_utc = $until, ban_reason = $reason WHERE account_id = " + OwnerOfNameSql + ";";

    private const string SetMuteSql =
        "UPDATE accounts SET muted_until_utc = $until WHERE account_id = " + OwnerOfNameSql + ";";

    private const string SetRoleSql =
        "UPDATE accounts SET role = $role WHERE account_id = " + OwnerOfNameSql + ";";

    // Inserts the lease, or takes it over while it is this world's own or expired. Zero rows: another
    // world holds a live lease.
    private const string ClaimPresenceSql = """
        INSERT INTO character_presence (player_name, world_id, expires_utc)
        VALUES ($key, $world, $expires)
        ON CONFLICT(player_name) DO UPDATE
        SET world_id = excluded.world_id, expires_utc = excluded.expires_utc
        WHERE character_presence.world_id = excluded.world_id OR character_presence.expires_utc < $now;
        """;

    private const string RenewPresenceSql =
        "UPDATE character_presence SET expires_utc = $expires WHERE player_name = $key AND world_id = $world;";

    private const string ReleasePresenceSql =
        "DELETE FROM character_presence WHERE player_name = $key AND world_id = $world;";

    public CharacterLookup Lookup(string trimmedName, AccountKey? account)
    {
        ArgumentNullException.ThrowIfNull(trimmedName);
        using SqliteSession session = Store.OpenSession();
        CharacterRecordRef? existing = null;
        using (SqliteCommand command = session.CreateCommand(LookupSql))
        {
            command.BindText("$name", trimmedName);
            using SqliteDataReader reader = command.ExecuteReader();
            if (reader.Read())
            {
                AccountKey? owner = reader.IsDBNull(2)
                    ? null
                    : AccountKey.FromHash(reader.GetFieldValue<byte[]>(2));
                existing = new CharacterRecordRef(new CharacterKey(reader.GetString(0)), reader.GetString(1), owner);
            }
        }

        bool reserved = false;
        if (existing is null)
        {
            using SqliteCommand command = session.CreateCommand(KeyReservedSql);
            command.BindText("$name", trimmedName);
            reserved = Convert.ToInt64(command.ExecuteScalar()) != 0;
        }

        int owned = 0;
        AccountStanding standing = default;
        if (account is { } key && key.HashHex.Length != 0)
        {
            using (SqliteCommand command = session.CreateCommand(CountSql))
            {
                command.Parameters.AddWithValue("$hash", key.ToHashBytes());
                owned = Convert.ToInt32(command.ExecuteScalar());
            }

            // The joining account's own role and sanctions; an account with no row yet has none.
            using SqliteCommand standingCommand = session.CreateCommand(StandingSql);
            standingCommand.Parameters.AddWithValue("$hash", key.ToHashBytes());
            using SqliteDataReader standingReader = standingCommand.ExecuteReader();
            if (standingReader.Read())
            {
                standing = new AccountStanding(
                    standingReader.GetInt32(0),
                    standingReader.IsDBNull(1) ? null : standingReader.GetUtc(1),
                    standingReader.GetNullableString(2),
                    standingReader.IsDBNull(3) ? null : standingReader.GetUtc(3));
            }
        }

        return new CharacterLookup(existing, reserved, owned, standing);
    }

    public bool TrySetBan(string displayName, DateTime? untilUtc, string? reason)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        using SqliteSession session = Store.OpenSession();
        using SqliteCommand command = session.CreateCommand(SetBanSql);
        command.BindText("$name", displayName.Trim());
        command.BindNullableText("$until", untilUtc is { } until ? SqliteSchemaBootstrapper.FormatUtc(until) : null);
        command.BindNullableText("$reason", untilUtc is null ? null : reason);
        return command.ExecuteNonQuery() > 0;
    }

    public bool TrySetMute(string displayName, DateTime? untilUtc)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        using SqliteSession session = Store.OpenSession();
        using SqliteCommand command = session.CreateCommand(SetMuteSql);
        command.BindText("$name", displayName.Trim());
        command.BindNullableText("$until", untilUtc is { } until ? SqliteSchemaBootstrapper.FormatUtc(until) : null);
        return command.ExecuteNonQuery() > 0;
    }

    public bool TrySetRole(string displayName, int role)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        if (role is not (0 or 1))
        {
            return false;
        }

        using SqliteSession session = Store.OpenSession();
        using SqliteCommand command = session.CreateCommand(SetRoleSql);
        command.BindText("$name", displayName.Trim());
        command.BindInt64("$role", role);
        return command.ExecuteNonQuery() > 0;
    }

    public bool TryClaimPresence(string characterKey, WorldId world, DateTime nowUtc, DateTime expiresUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterKey);
        using SqliteSession session = Store.OpenSession();
        using SqliteCommand command = session.CreateCommand(ClaimPresenceSql);
        command.BindText("$key", characterKey);
        command.BindInt64("$world", world.Value);
        command.BindUtc("$expires", expiresUtc);
        command.BindUtc("$now", nowUtc);
        return command.ExecuteNonQuery() > 0;
    }

    public void RenewPresence(IReadOnlyList<string> characterKeys, WorldId world, DateTime expiresUtc)
    {
        ArgumentNullException.ThrowIfNull(characterKeys);
        if (characterKeys.Count == 0)
        {
            return;
        }

        using SqliteSession session = Store.OpenSession();
        using SqliteCommand command = session.CreateCommand(RenewPresenceSql);
        command.BindInt64("$world", world.Value);
        command.BindUtc("$expires", expiresUtc);
        for (int i = 0; i < characterKeys.Count; i++)
        {
            command.BindText("$key", characterKeys[i]);
            command.ExecuteNonQuery();
        }
    }

    public void ReleasePresence(string characterKey, WorldId world)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterKey);
        using SqliteSession session = Store.OpenSession();
        using SqliteCommand command = session.CreateCommand(ReleasePresenceSql);
        command.BindText("$key", characterKey);
        command.BindInt64("$world", world.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AccountCharacterSummary> ListCharacters(AccountKey account, int max)
    {
        byte[] hash = account.ToHashBytes();
        if (hash.Length != AccountKey.TokenBytes || max <= 0)
        {
            return [];
        }

        using SqliteSession session = Store.OpenSession();
        using SqliteCommand command = session.CreateCommand(ListSql);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$max", max);
        using SqliteDataReader reader = command.ExecuteReader();
        List<AccountCharacterSummary> characters = [];
        while (reader.Read())
        {
            characters.Add(new AccountCharacterSummary(reader.GetString(0), reader.GetInt32(1)));
        }

        return characters;
    }

    public bool RecordJoin(AccountKey account, CharacterKey character, bool claim, DateTime utcNow)
    {
        byte[] hash = account.ToHashBytes();
        if (hash.Length != AccountKey.TokenBytes)
        {
            throw new ArgumentException("The account has no token hash.", nameof(account));
        }

        using SqliteSession session = Store.OpenSession();
        using (SqliteCommand upsert = session.CreateCommand(UpsertAccountSql))
        {
            upsert.Parameters.AddWithValue("$hash", hash);
            upsert.BindUtc("$now", utcNow);
            upsert.ExecuteNonQuery();
        }

        if (!claim)
        {
            return true;
        }

        using SqliteCommand update = session.CreateCommand(ClaimSql);
        update.Parameters.AddWithValue("$hash", hash);
        update.BindText("$key", character.Value);
        return update.ExecuteNonQuery() > 0;
    }
}
