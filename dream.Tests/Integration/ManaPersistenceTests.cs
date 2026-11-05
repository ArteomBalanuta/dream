using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Players;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;

namespace Dream.Tests.Integration;

// Mana is real state (M4 workstream A): saved as player_world_state.current_energy on leave and at
// checkpoints, restored on join (clamped to the maximum, which is what lets migration 0013's backfill
// leave returning characters at full mana), reset when a seat changes hands, refilled by a respawn and
// frozen while dead. Real server, real database, shipped content.
public sealed class ManaPersistenceTests
{
    private const string SessionA = "m4a-mana-a";
    private const string SessionB = "m4a-mana-b";

    [Fact]
    public async Task SpentMana_IsSavedOnLeave_AndComesBackNotFull_AcrossARestart()
    {
        using MigrationTestDatabase db = new();
        string keyA = M4AKit.KeyOf(SessionA);
        int spentTo;

        await using (WireLoopbackServer server = await M4AKit.StartAsync(db))
        {
            await using HeadlessPlayScene a = await M4AKit.JoinAsync(server, SessionA);
            await M4AKit.PlaceAsync(server, a, M4AKit.Away, a);
            PlayerCombatant combatant = M4AKit.Combatant(server, SessionA);
            Assert.Equal(combatant.MaxMana, combatant.CurrentMana);
            Assert.True(combatant.TrySpendMana(30));
            spentTo = combatant.CurrentMana;
            await a.LeaveAsync();
            M4AKit.Flush(server);
            Assert.Equal(spentTo, M4AKit.Count(db, $"SELECT current_energy FROM player_world_state WHERE player_name = '{keyA}'"));
        }

        SqlitePools.Release(db.Schema);
        await using (WireLoopbackServer again = await M4AKit.StartAsync(db))
        {
            await using HeadlessPlayScene back = await M4AKit.JoinAsync(again, SessionA);
            PlayerCombatant combatant = M4AKit.Combatant(again, SessionA);
            Assert.Equal(spentTo, combatant.CurrentMana);
            Assert.True(combatant.CurrentMana < combatant.MaxMana);

            // The owner is told on its first drain.
            await M4AKit.StepThroughVitalsDrainAsync(again, back);
            ServerPlayerVitals vitals = M4AKit.LastVitals(back)!;
            Assert.Equal(combatant.MaxMana, vitals.MaxMana);
            Assert.InRange(vitals.CurrentMana, spentTo, combatant.MaxMana);
            await back.LeaveAsync();
        }
    }

    [Fact]
    public async Task AnOldRow_WithTheMigrationBackfillValue_JoinsAtFullMana_ClampedToTheMaximum()
    {
        using MigrationTestDatabase db = new();
        string keyA = M4AKit.KeyOf(SessionA);

        await using (WireLoopbackServer server = await M4AKit.StartAsync(db))
        {
            await using HeadlessPlayScene a = await M4AKit.JoinAsync(server, SessionA);
            await a.LeaveAsync();
            M4AKit.Flush(server);
        }

        SqlitePools.Release(db.Schema);
        db.ExecuteScript($"UPDATE player_world_state SET current_energy = 1000000 WHERE player_name = '{keyA}'");
        SqlitePools.Release(db.Schema);

        await using (WireLoopbackServer again = await M4AKit.StartAsync(db))
        {
            await using HeadlessPlayScene back = await M4AKit.JoinAsync(again, SessionA);
            PlayerCombatant combatant = M4AKit.Combatant(again, SessionA);
            Assert.Equal(combatant.MaxMana, combatant.CurrentMana);
            Assert.Equal(PlayerManaRules.MaxMana(combatant.Level, combatant.Stats.Energy), combatant.MaxMana);
            await back.LeaveAsync();
        }
    }

    [Fact]
    public async Task ASecondCharacter_OnTheReusedPrimarySeat_StartsWithFullMana_NotTheFirstOnesLeftovers()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await M4AKit.StartAsync(db);

        await using (HeadlessPlayScene a = await M4AKit.JoinAsync(server, SessionA))
        {
            PlayerCombatant seat = M4AKit.Combatant(server, SessionA);
            Assert.Same(server.Runtime.Player.Combatant, seat);
            Assert.True(seat.TrySpendMana(seat.MaxMana - 3));
            Assert.Equal(3, seat.CurrentMana);
            await a.LeaveAsync();
        }

        await using HeadlessPlayScene b = await M4AKit.JoinAsync(server, SessionB);
        PlayerCombatant occupant = M4AKit.Combatant(server, SessionB);
        Assert.Same(server.Runtime.Player.Combatant, occupant);
        Assert.Equal(occupant.MaxMana, occupant.CurrentMana);
        Assert.Equal(PlayerManaRules.MaxMana(occupant.Level, occupant.Stats.Energy), occupant.CurrentMana);
        await b.LeaveAsync();
    }

    [Fact]
    public async Task ManaDoesNotRegenerateWhileDead_AndARespawnRefillsIt()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await M4AKit.StartAsync(db);
        await using HeadlessPlayScene a = await M4AKit.JoinAsync(server, SessionA);
        await using HeadlessPlayScene b = await M4AKit.JoinAsync(server, SessionB);
        await M4AKit.PlaceAsync(server, a, M4AKit.Away, a, b);
        await M4AKit.PlaceAsync(server, b, M4AKit.Away + new Vector3(3f, 0f, 0f), a, b);

        PlayerCombatant dead = M4AKit.Combatant(server, SessionA);
        PlayerCombatant alive = M4AKit.Combatant(server, SessionB);
        Assert.True(dead.TrySpendMana(30));
        Assert.True(alive.TrySpendMana(30));
        int deadMana = dead.CurrentMana;
        int aliveMana = alive.CurrentMana;
        dead.Health.ApplyDamage(1_000_000);
        Assert.False(dead.Health.IsAlive);

        // Two regeneration intervals, well inside the respawn wait: the living one gains, the dead one does not.
        await M4AKit.StepManyAsync(server, PlayerManaRules.RegenIntervalTicks * 2 + 1, a, b);
        Assert.False(dead.Health.IsAlive);
        Assert.Equal(deadMana, dead.CurrentMana);
        Assert.True(alive.CurrentMana > aliveMana);

        // The respawn brings full health and full mana.
        await M4AKit.StepManyAsync(server, PlayerRespawnRules.DelayTicks + 8, a, b);
        Assert.True(dead.Health.IsAlive);
        Assert.Equal(dead.MaxMana, dead.CurrentMana);
        await M4AKit.StepThroughVitalsDrainAsync(server, a, b);
        Assert.Equal(dead.MaxMana, M4AKit.LastVitals(a)!.CurrentMana);
    }
}
