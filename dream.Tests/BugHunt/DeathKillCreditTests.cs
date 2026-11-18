using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Characters;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.BugHunt;

// BH-DEATH-1: RecordEnemyKill deduped its contributors by SESSION id but credited the kill by CHARACTER
// key, so one enemy death advanced one character's kill objective twice whenever that character held two
// live seats (DeathJoinSeatKeyTests shows the join path that seats them). The quest layer now dedupes on
// the key it credits.
public sealed class DeathKillCreditTests
{
    private const string Bob = "Bob";
    private const string Carol = "Carol";
    private const string WolfCull = "wolf-cull";

    private static readonly PlayerSessionId SeatA = new("death-seat-a");
    private static readonly PlayerSessionId SeatB = new("death-seat-b");

    private static QuestDefinition Quest()
        => ContentCatalog.Shipped.TryGetQuest(WolfCull, out QuestDefinition? quest)
            ? quest
            : throw new InvalidOperationException(WolfCull);

    // Seats a session on a character key, the way a join does on a host without accounts.
    private static void Seat(WaveIRuntimeKit.RealRun run, PlayerSessionId session, string key)
    {
        run.Runtime.AttachRemoteSession(session, key, new CharacterKey(key));
        run.Runtime.RestorePersistedPlayerState(session, key, key, account: null);
        WaveIRuntimeKit.EndProtection(run.Runtime, session);
    }

    // The test assembly's default content (LegacyWolfPopulation) carries no quests, so the runtime is put
    // on the shipped catalog after the wolf has spawned and before any kill. No tick runs after this, so
    // the shipped spawn camps add nothing and the one wolf stays the one wolf.
    private static void UseShippedQuests(WaveIRuntimeKit.RealRun run)
    {
        run.Runtime.EnemyContent = ContentCatalog.Shipped;
        Assert.True(run.Runtime.EnemyContent.Quests.Count > 0);
    }

    private static void Accept(WaveIRuntimeKit.RealRun run, string key)
        => Assert.Equal(
            InventoryEquipOutcome.Equipped,
            run.Runtime.InventoryService.TryAcceptQuest(key, Quest(), ContentCatalog.Shipped));

    private static int KillsOf(WaveIRuntimeKit.RealRun run, string key)
    {
        Assert.True(
            run.Runtime.InventoryService.TryGetQuestRecord(key, WolfCull, out PlayerInventoryService.QuestRecord record),
            "the quest record should exist");
        return record.Kills[0];
    }

    // Both seats damage one wolf and it dies to the second blow.
    private static void KillTogether(WaveIRuntimeKit.RealRun run, PlayerSessionId chipper, PlayerSessionId finisher)
    {
        WaveIRuntimeKit.EnterCombat(run.Runtime, chipper);
        AuthoritativeActionResult chip = WaveIRuntimeKit.HitWolf(run.Runtime, chipper, actionSequence: 1, wolfHealth: 1_000_000);
        Assert.True(chip.Accepted, chip.Rejection?.Reason);
        Assert.False(Assert.IsType<CombatResolutionResult>(chip.Payload).TargetDied);

        WaveIRuntimeKit.EnterCombat(run.Runtime, finisher);
        AuthoritativeActionResult kill = WaveIRuntimeKit.HitWolf(run.Runtime, finisher, actionSequence: 2, wolfHealth: 1);
        Assert.True(kill.Accepted, kill.Rejection?.Reason);
        Assert.True(Assert.IsType<CombatResolutionResult>(kill.Payload).TargetDied);
    }

    [Fact]
    public void OneDeath_WithTwoLiveSeatsOnOneCharacterKey_AdvancesTheKillObjectiveOnce()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        using WaveIRuntimeKit.RealRun run = new(db);

        Seat(run, SeatA, Bob);
        Seat(run, SeatB, Bob);
        run.Advance(3);
        UseShippedQuests(run);

        // Both seats really are live and share the one character key: the shape BH-DEATH-1 needs.
        Assert.True(run.Runtime.Sessions.TryGet(SeatA, out ConnectedPlayer? a));
        Assert.True(run.Runtime.Sessions.TryGet(SeatB, out ConnectedPlayer? b));
        Assert.True(a!.IsSpawned && b!.IsSpawned);
        Assert.Equal(a.CharacterKey.Value, b.CharacterKey.Value);

        Accept(run, Bob);
        KillTogether(run, SeatA, SeatB);

        Assert.Equal(1, KillsOf(run, Bob));
    }

    // Control: the ordinary single-seat case must still count, and count exactly once.
    [Fact]
    public void OneDeath_ForASingleSeatPlayer_AdvancesTheKillObjectiveExactlyOnce()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        using WaveIRuntimeKit.RealRun run = new(db);

        Seat(run, SeatA, Bob);
        run.Advance(3);
        UseShippedQuests(run);
        Accept(run, Bob);
        Assert.Equal(0, KillsOf(run, Bob));

        WaveIRuntimeKit.EnterCombat(run.Runtime, SeatA);
        AuthoritativeActionResult kill = WaveIRuntimeKit.HitWolf(run.Runtime, SeatA, actionSequence: 1, wolfHealth: 1);
        Assert.True(kill.Accepted, kill.Rejection?.Reason);
        Assert.True(Assert.IsType<CombatResolutionResult>(kill.Payload).TargetDied);

        Assert.Equal(1, KillsOf(run, Bob));
    }

    // Control: two DIFFERENT characters contributing to one death are both credited, once each.
    [Fact]
    public void OneDeath_WithTwoDifferentCharacters_CreditsEachOfThemOnce()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        using WaveIRuntimeKit.RealRun run = new(db);

        Seat(run, SeatA, Bob);
        Seat(run, SeatB, Carol);
        run.Advance(3);
        UseShippedQuests(run);
        Accept(run, Bob);
        Accept(run, Carol);

        KillTogether(run, SeatA, SeatB);

        Assert.Equal(1, KillsOf(run, Bob));
        Assert.Equal(1, KillsOf(run, Carol));
    }
}
