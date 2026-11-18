using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.BugHunt;

// BH-DEATH-1, the join path behind it: JoinSession's duplicate-name gate sits BELOW the existing-session
// branch, so a session that left and comes back is re-seated without ever meeting the gate - even if a
// second session took its name while it was away. These tests pin what the join path actually does today.
// They are observational for the join layer (the fix for BH-DEATH-1 is in the quest credit, which no
// longer depends on this); the reconnect case is the control that must never start failing.
public sealed class DeathJoinSeatKeyTests
{
    private static readonly PlayerSessionId First = new("death-join-first");
    private static readonly PlayerSessionId Second = new("death-join-second");

    private static ConnectedPlayer Player(WaveIRuntimeKit.RealRun run, PlayerSessionId session)
    {
        Assert.True(run.Runtime.Sessions.TryGet(session, out ConnectedPlayer? player));
        return player!;
    }

    // The packet's four-step sequence: join, drop, a second session takes the name, the first reconnects.
    [Fact]
    public void AReconnect_AfterASecondSessionTookTheName_SeatsTwoLivePlayersOnOneCharacterKey()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        using WaveIRuntimeKit.RealRun run = new(db);

        run.Host.JoinSession(First.Value, "Bob");
        run.Advance(1);
        run.Runtime.DetachRemoteSession(First);
        run.Advance(1);

        // The duplicate gate only refuses a SPAWNED holder, so the departed seat does not block this.
        run.Host.JoinSession(Second.Value, "Bob");
        run.Advance(1);

        // The reconnect takes the existing-session branch, above the gate, and is seated again.
        run.Host.JoinSession(First.Value, "Bob");
        run.Advance(1);

        ConnectedPlayer first = Player(run, First);
        ConnectedPlayer second = Player(run, Second);
        Assert.True(first.IsSpawned);
        Assert.True(second.IsSpawned);
        Assert.Equal(first.CharacterKey.Value, second.CharacterKey.Value);
    }

    // Control: the plain reconnect - nobody else took the name - still gets the seat back.
    [Fact]
    public void AReconnect_WithNobodyElseOnTheName_ReclaimsItsSeat()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        using WaveIRuntimeKit.RealRun run = new(db);

        run.Host.JoinSession(First.Value, "Bob");
        run.Advance(1);
        Assert.True(Player(run, First).IsSpawned);

        run.Runtime.DetachRemoteSession(First);
        run.Advance(1);
        Assert.False(Player(run, First).IsSpawned);

        run.Host.JoinSession(First.Value, "Bob");
        run.Advance(1);

        ConnectedPlayer back = Player(run, First);
        Assert.True(back.IsSpawned);
        Assert.Equal("Bob", back.CharacterKey.Value);
    }

    // Control: a second, different character still joins and plays beside the first.
    [Fact]
    public void ASecondCharacter_StillJoinsBesideTheFirst()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        using WaveIRuntimeKit.RealRun run = new(db);

        run.Host.JoinSession(First.Value, "Bob");
        run.Host.JoinSession(Second.Value, "Carol");
        run.Advance(1);

        Assert.True(Player(run, First).IsSpawned);
        Assert.True(Player(run, Second).IsSpawned);
        Assert.NotEqual(Player(run, First).CharacterKey.Value, Player(run, Second).CharacterKey.Value);
    }

    // Control: a live, spawned holder of the name is still refused on a NEW session id.
    [Fact]
    public void ANewSession_OnALiveCharactersName_IsStillRefused()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        using WaveIRuntimeKit.RealRun run = new(db);

        run.Host.JoinSession(First.Value, "Bob");
        run.Advance(1);

        Assert.Throws<InvalidOperationException>(() => run.Host.JoinSession(Second.Value, "Bob"));
    }
}
