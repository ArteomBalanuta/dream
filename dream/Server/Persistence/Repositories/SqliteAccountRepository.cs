using Dream.Server.Persistence.Store;
using Dream.Simulation.Characters;
using Dream.Simulation.Persistence;
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
        if (account is { } key && key.HashHex.Length != 0)
        {
            using SqliteCommand command = session.CreateCommand(CountSql);
            command.Parameters.AddWithValue("$hash", key.ToHashBytes());
            owned = Convert.ToInt32(command.ExecuteScalar());
        }

        return new CharacterLookup(existing, reserved, owned);
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
