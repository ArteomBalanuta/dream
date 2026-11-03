using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Characters;
using Dream.Simulation.Persistence;
using Dream.Tests.Server.Persistence;
using Dream.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Dream.Tests.Server;

// Wave G glue: the join rules of CharacterJoinTests run against the production SqliteAccountRepository, on
// the real write queue. CountingAccountRepository stands in for it there; these pin that the SQL behaves the
// same, above all that a claim runs behind the profile save that creates the row it claims.
public sealed class CharacterJoinSqliteTests
{
    private static string TokenOf(string who) => TestAccountTokens.For(who);

    private static AccountKey AccountOf(string who) => TestAccountTokens.KeyFor(who);

    private static AccountKey? OwnerOf(AccountHostHarness harness, string name) =>
        harness.Sqlite!.Lookup(name, null).Existing?.Owner;

    private static void Execute(PersistenceTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection connection = database.Schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static void AddLegacyRow(PersistenceTestDatabase database, string key, string? display = null) =>
        Execute(
            database,
            "INSERT INTO player_profiles (player_name, display_name) VALUES ($key, $display);",
            ("$key", key),
            ("$display", display ?? key));

    private static void Release(PersistenceTestDatabase database, string key) =>
        Execute(database, "UPDATE player_profiles SET account_id = NULL WHERE player_name = $key;", ("$key", key));

    [Fact]
    public void NewCharacter_TheClaimFindsTheRowTheJoinsOwnSaveCreated()
    {
        using AccountHostHarness harness = AccountHostHarness.OverSqlite();

        harness.JoinAccepted("c1", "s1", "Fresh Face", TokenOf("fresh"));

        Assert.Equal(AccountOf("fresh"), OwnerOf(harness, "Fresh Face"));
        Assert.Equal(1, harness.Database.Count("SELECT COUNT(*) FROM accounts;"));
        Assert.Equal(0, harness.Runtime.PersistenceWrites!.FailedWrites);
    }

    [Fact]
    public void ManyNewCharactersInOneWriteGroup_AreEachClaimed()
    {
        using AccountHostHarness harness = AccountHostHarness.OverSqlite();
        string[] names = ["Group One", "Group Two", "Group Three", "Group Four", "Group Five", "Group Six"];
        for (int i = 0; i < names.Length; i++)
        {
            harness.SendJoin($"c{i}", $"s{i}", names[i], TokenOf(names[i]));
        }

        for (int i = 0; i < 8; i++)
        {
            harness.Tick();
        }

        Assert.All(names, name => Assert.Equal(AccountOf(name), OwnerOf(harness, name)));
    }

    [Fact]
    public void RejoiningOwner_IsAccepted_AndLastSeenMovesWithoutANewAccount()
    {
        using AccountHostHarness harness = AccountHostHarness.OverSqlite();
        harness.JoinAccepted("c1", "s1", "Regular", TokenOf("regular"));
        Execute(harness.Database, "UPDATE accounts SET last_seen_utc = '2000-01-01T00:00:00.0000000Z';");
        harness.Leave("c1");

        harness.JoinAccepted("c2", "s2", "regular", TokenOf("regular"));

        Assert.Equal(1, harness.Database.Count("SELECT COUNT(*) FROM accounts;"));
        Assert.Equal(0, harness.Database.Count("SELECT COUNT(*) FROM accounts WHERE last_seen_utc LIKE '2000%';"));
        Assert.Equal(1, harness.Database.Count("SELECT COUNT(*) FROM player_profiles;"));
    }

    [Fact]
    public void LegacyRow_IsClaimedByTheFirstToken_AndALaterTokenIsRejected()
    {
        using PersistenceTestDatabase database = new();
        AddLegacyRow(database, "Legacy");
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(database);

        harness.JoinAccepted("c1", "s1", "LEGACY", TokenOf("first"));
        harness.Leave("c1");
        string reason = harness.JoinRejectionOf("c2", "s2", "Legacy", TokenOf("second"));

        Assert.Equal(JoinRejectionReasons.CharacterOwned, reason);
        Assert.Equal(AccountOf("first"), OwnerOf(harness, "Legacy"));
    }

    [Fact]
    public void ReleaseThenClaim_ReadsTheDatabaseAfresh_WhileTheServerRuns()
    {
        using AccountHostHarness harness = AccountHostHarness.OverSqlite();
        harness.JoinAccepted("c1", "s1", "Moved", TokenOf("old"));
        harness.Leave("c1");
        Assert.Equal(JoinRejectionReasons.CharacterOwned, harness.JoinRejectionOf("c2", "s2", "Moved", TokenOf("new")));

        Release(harness.Database, "Moved");
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        harness.JoinAccepted("c3", "s3", "Moved", TokenOf("new"));

        Assert.Equal(AccountOf("new"), OwnerOf(harness, "Moved"));
    }

    [Fact]
    public void SixthCharacter_IsRejected_ForACreateAndForAClaim_AndTheFiveOwnedStillJoin()
    {
        using PersistenceTestDatabase database = new();
        AddLegacyRow(database, "Loose");
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(database);
        string token = TokenOf("collector");
        for (int i = 0; i < CharacterNames.MaxCharactersPerAccount; i++)
        {
            harness.JoinAccepted($"c{i}", $"s{i}", $"Owned{i}", token);
            harness.Leave($"c{i}");
        }

        Assert.Equal(JoinRejectionReasons.CharacterLimit, harness.JoinRejectionOf("x1", "x1", "Brand New", token));
        Assert.Equal(JoinRejectionReasons.CharacterLimit, harness.JoinRejectionOf("x2", "x2", "Loose", token));

        Assert.Null(OwnerOf(harness, "Loose"));
        Assert.Equal(0, harness.Database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'Brand New';"));
        harness.JoinAccepted("c-again", "s-again", "Owned2", token);
    }

    [Fact]
    public void TwoNewNamesInTheSameBatch_AtFourOwned_AcceptsExactlyOne()
    {
        using AccountHostHarness harness = AccountHostHarness.OverSqlite();
        string token = TokenOf("busy");
        for (int i = 0; i < CharacterNames.MaxCharactersPerAccount - 1; i++)
        {
            harness.JoinAccepted($"c{i}", $"s{i}", $"Owned{i}", token);
            harness.Leave($"c{i}");
        }

        harness.SendJoin("n1", "n1", "NewOne", token);
        harness.SendJoin("n2", "n2", "NewTwo", token);
        for (int i = 0; i < 8; i++)
        {
            harness.Tick();
        }

        // An accepted join also gets the owner's pushes (vitals, progress); the join reply is what counts.
        static IServerToClientMessage JoinReplyOf(AccountHostHarness harness, string connection) =>
            Assert.Single(harness.RepliesOf(connection), reply => reply is JoinAcceptedMessage or JoinRejectedMessage);

        IServerToClientMessage[] replies = [JoinReplyOf(harness, "n1"), JoinReplyOf(harness, "n2")];
        Assert.Equal(1, replies.Count(reply => reply is JoinAcceptedMessage));
        Assert.Equal(
            JoinRejectionReasons.CharacterLimit,
            Assert.IsType<JoinRejectedMessage>(Assert.Single(replies, reply => reply is JoinRejectedMessage)).Reason);
        Assert.Equal(5, harness.Database.Count("SELECT COUNT(*) FROM player_profiles WHERE account_id IS NOT NULL;"));
        Assert.Equal(5, harness.Database.Count("SELECT COUNT(*) FROM player_profiles;"));
    }

    [Theory]
    [InlineData("Race", "Race")]
    [InlineData("Race", "rACE")]
    public void TwoClientsRacingOneNewName_ExactlyOneWins_AndNoSaveFails(string firstName, string secondName)
    {
        using AccountHostHarness harness = AccountHostHarness.OverSqlite();
        harness.SendJoin("c-1", "s-1", firstName, TokenOf("one"));
        harness.SendJoin("c-2", "s-2", secondName, TokenOf("two"));

        for (int i = 0; i < 20 && (harness.RepliesOf("c-1").Count == 0 || harness.RepliesOf("c-2").Count == 0); i++)
        {
            harness.Tick();
        }

        Assert.IsType<JoinAcceptedMessage>(Assert.Single(harness.RepliesOf("c-1")));
        Assert.Equal(JoinRejectionReasons.CharacterOwned, Assert.IsType<JoinRejectedMessage>(Assert.Single(harness.RepliesOf("c-2"))).Reason);
        Assert.Equal(AccountOf("one"), OwnerOf(harness, "race"));
        Assert.Equal(1, harness.Database.Count("SELECT COUNT(*) FROM player_profiles;"));
        Assert.Equal(0, harness.Runtime.PersistenceWrites!.FailedWrites);
    }

    [Fact]
    public void ACaseVariantOfAnExistingName_ReachesTheSameCharacter_AndKeepsItsDisplayName()
    {
        using PersistenceTestDatabase database = new();
        AddLegacyRow(database, "Bob");
        AddLegacyRow(database, "bob", "bob~2");
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(database);

        harness.JoinAccepted("c1", "s1", "BOB", TokenOf("bob"));
        harness.JoinAccepted("c2", "s2", "BOB~2", TokenOf("other bob"));
        harness.Leave("c1");
        harness.Leave("c2");

        Assert.Equal(AccountOf("bob"), OwnerOf(harness, "Bob"));
        Assert.Equal(AccountOf("other bob"), OwnerOf(harness, "bob~2"));
        Assert.Equal(1, harness.Database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'Bob' AND display_name = 'Bob';"));
        Assert.Equal(1, harness.Database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'bob' AND display_name = 'bob~2';"));
        Assert.Equal(0, harness.Runtime.PersistenceWrites!.FailedWrites);
    }

    [Fact]
    public void ANewNameEqualToARetiredKey_IsRejectedAsTaken_AndTheRowIsUntouched()
    {
        using PersistenceTestDatabase database = new();
        AddLegacyRow(database, "Bob", "Robert");
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(database);

        Assert.Equal(JoinRejectionReasons.NameTaken, harness.JoinRejectionOf("c1", "s1", "Bob", TokenOf("someone")));

        Assert.Null(OwnerOf(harness, "Robert"));
        Assert.Equal(0, harness.Database.Count("SELECT COUNT(*) FROM accounts;"));
    }

    // Risk 3 of the plan: the queue drops a failed write, the player plays on unowned, and the next join claims.
    [Fact]
    public void AClaimLostToAQueueFailure_LeavesTheJoinPlaying_AndTheNextJoinClaims()
    {
        FailingAccounts? failing = null;
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(wrap: inner => failing = new FailingAccounts(inner));
        failing!.FailNextRecordJoin = true;

        harness.JoinAccepted("c1", "s1", "Unlucky", TokenOf("unlucky"));

        Assert.Equal(1, harness.Runtime.PersistenceWrites!.FailedWrites);
        Assert.True(harness.Host.IsConnectionJoined("c1"));
        Assert.Equal(1, harness.Database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'Unlucky' AND account_id IS NULL;"));
        Assert.Equal(0, harness.Database.Count("SELECT COUNT(*) FROM accounts;"));

        // While it is held, unowned, another token cannot have it.
        Assert.Equal(JoinRejectionReasons.CharacterInUse, harness.JoinRejectionOf("c2", "s2", "Unlucky", TokenOf("stranger")));
        Assert.True(harness.Host.IsConnectionJoined("c1"));

        harness.Leave("c1");
        harness.JoinAccepted("c3", "s3", "Unlucky", TokenOf("unlucky"));
        Assert.Equal(AccountOf("unlucky"), OwnerOf(harness, "Unlucky"));
    }

    private sealed class FailingAccounts(IAccountRepository inner) : IAccountRepository
    {
        public volatile bool FailNextRecordJoin;

        public CharacterLookup Lookup(string trimmedName, AccountKey? account) => inner.Lookup(trimmedName, account);

        public bool RecordJoin(AccountKey account, CharacterKey character, bool claim, DateTime utcNow)
        {
            if (FailNextRecordJoin)
            {
                FailNextRecordJoin = false;
                throw new InvalidOperationException("disk I/O error");
            }

            return inner.RecordJoin(account, character, claim, utcNow);
        }
    }
}
