using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;

namespace Dream.Tests.Integration;

// M4 PvP flag. Off (the default), no player is ever a target of a primary attack or a skill. On (DREAM_PVP=1),
// the first player on the ray, an enemy first, takes the damage through the victim's own armor and death
// event; nobody gets experience, loot or gold for it; a victim under spawn protection is skipped; a killed
// player respawns like after any death.
[Collection(TimingSensitiveCollection.Name)]
public sealed class PvpFlagTests
{
    private static readonly Vector3 Victim = new(20.5f, 1.5f, 22f);

    // A at (20.5, 20) looking down +Z at B two metres ahead, both past join protection.
    private static async Task<M4BRig> FaceOffAsync(MigrationTestDatabase db, bool pvp)
    {
        M4BRig rig = await M4BRig.StartAsync(db, pvp);
        await rig.PlaceAsync(rig.B, Victim);
        rig.Player(rig.A).ProtectedThroughTick = 0;
        rig.Player(rig.B).ProtectedThroughTick = 0;
        return rig;
    }

    private static Task<AuthoritativeActionResult> StrikeAsync(M4BRig rig) =>
        rig.UseAsync(rig.A, new CombatUseIntent(Vector3.Zero, Vector3.UnitZ));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData(" 1", false)]
    [InlineData("11", false)]
    [InlineData("1", true)]
    public void TheBootstrapEnablesPvpOnlyForExactlyOne(string? value, bool expected)
    {
        string? previous = Environment.GetEnvironmentVariable(ServerBootstrap.PvpEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ServerBootstrap.PvpEnvironmentVariable, value);

            using var host = ServerBootstrap.CreateRuntimeHost(persistence: null, log: null);

            Assert.Equal(expected, host.Runtime.PvpEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ServerBootstrap.PvpEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task PvpOff_NoPlayerIsHit_ByTheAttackOrBySkills()
    {
        using MigrationTestDatabase db = new();
        await using M4BRig rig = await FaceOffAsync(db, pvp: false);
        PlayerCombatant victim = rig.Combatant(rig.B);
        int full = victim.Health.CurrentHealth;

        AuthoritativeActionResult strike = await StrikeAsync(rig);
        Assert.True(strike.Accepted);
        Assert.Equal(0, Assert.IsType<CombatResolutionResult>(strike.Payload).DamageApplied);
        Assert.Equal(0, M4BRig.AssertUsed(await rig.UseAsync(rig.A, new SkillUseIntent("skill.bolt"))).TargetsHit);
        Assert.Equal(0, M4BRig.AssertUsed(await rig.UseAsync(rig.A, new SkillUseIntent("skill.cleave"))).TargetsHit);

        Assert.Equal(full, victim.Health.CurrentHealth);
    }

    [Fact]
    public async Task PvpOn_TheAttackBoltAndCleaveHitThePlayer_WithNoRewards_AndAProtectedVictimIsSkipped()
    {
        using MigrationTestDatabase db = new();
        await using M4BRig rig = await FaceOffAsync(db, pvp: true);
        PlayerCombatant victim = rig.Combatant(rig.B);
        PlayerCombatant attacker = rig.Combatant(rig.A);
        // Enough health to survive a hit, a bolt and a cleave.
        victim.Health.SetAuthoritativeState(10_000, 10_000);
        int full = victim.Health.CurrentHealth;
        long gold = rig.Gold(rig.A);
        int experience = attacker.Experience;

        // Under spawn protection: skipped by all three.
        rig.Player(rig.B).ProtectedThroughTick = rig.Runtime.CurrentServerTick + 10_000;
        AuthoritativeActionResult skipped = await StrikeAsync(rig);
        Assert.Equal(0, Assert.IsType<CombatResolutionResult>(skipped.Payload).DamageApplied);
        Assert.Equal(0, M4BRig.AssertUsed(await rig.UseAsync(rig.A, new SkillUseIntent("skill.bolt"))).TargetsHit);
        Assert.Equal(full, victim.Health.CurrentHealth);

        // Unprotected: the primary attack, then Bolt, then Cleave, each lowering the victim's health.
        rig.Player(rig.B).ProtectedThroughTick = 0;
        await rig.StepAsync(100);
        AuthoritativeActionResult strike = await StrikeAsync(rig);
        CombatResolutionResult struck = Assert.IsType<CombatResolutionResult>(strike.Payload);
        Assert.True(struck.DamageApplied > 0);
        Assert.Equal(victim.Actor.Id, struck.TargetActorId);
        Assert.Equal(full - struck.DamageApplied, victim.Health.CurrentHealth);

        int afterStrike = victim.Health.CurrentHealth;
        Assert.Equal(1, M4BRig.AssertUsed(await rig.UseAsync(rig.A, new SkillUseIntent("skill.bolt"))).TargetsHit);
        int afterBolt = victim.Health.CurrentHealth;
        Assert.True(afterBolt < afterStrike);
        Assert.Equal(1, M4BRig.AssertUsed(await rig.UseAsync(rig.A, new SkillUseIntent("skill.cleave"))).TargetsHit);
        Assert.True(victim.Health.CurrentHealth < afterBolt);

        Assert.Equal(gold, rig.Gold(rig.A));
        Assert.Equal(experience, attacker.Experience);
    }

    [Fact]
    public async Task PvpOn_AWallBetweenTheAttackerAndThePlayer_BlocksThePrimaryAttack()
    {
        using MigrationTestDatabase db = new();
        await using M4BRig rig = await FaceOffAsync(db, pvp: true);
        PlayerCombatant victim = rig.Combatant(rig.B);
        victim.Health.SetAuthoritativeState(10_000, 10_000);
        int full = victim.Health.CurrentHealth;
        int y = (int)MathF.Floor(rig.Target(rig.A).EyePosition.Y);

        // Control: with nothing in between, the victim two metres ahead is hit.
        Assert.Equal(victim.Actor.Id, Assert.IsType<CombatResolutionResult>((await StrikeAsync(rig)).Payload).TargetActorId);
        int afterControl = victim.Health.CurrentHealth;
        Assert.True(afterControl < full);

        // The victim steps back to 3.4 m; a static wall, three cells high, stands on the ray 2.5 m ahead of A.
        // The ray runs from the eye (about 1.85) to the body centre (about 2.7), so it crosses the wall at
        // the cell above eye height; the cells at and below it keep the test independent of the exact heights.
        await rig.PlaceAsync(rig.B, new Vector3(20.5f, 1.5f, 23.4f));
        rig.Player(rig.B).ProtectedThroughTick = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            // A cell below the eye may already be solid terrain, so only the eye-height cell must be added.
            Cube? placed = rig.Runtime.Map.TryAddCube(new Vector3(20.5f, y + dy + 0.5f, 22.5f), isStatic: true, withPhysics: false);
            Assert.True(placed != null || dy != 0);
        }
        await rig.StepAsync(100);

        AuthoritativeActionResult strike = await StrikeAsync(rig);

        Assert.Equal(afterControl, victim.Health.CurrentHealth);
        Assert.NotEqual(victim.Actor.Id, (strike.Payload as CombatResolutionResult)?.TargetActorId);
    }

    [Fact]
    public async Task PvpOn_AKilledPlayerDies_AndRespawnsLikeAfterAnyDeath()
    {
        using MigrationTestDatabase db = new();
        await using M4BRig rig = await FaceOffAsync(db, pvp: true);
        PlayerCombatant victim = rig.Combatant(rig.B);
        victim.Health.SetAuthoritativeState(1, victim.Health.MaxHealth);

        AuthoritativeActionResult strike = await StrikeAsync(rig);

        Assert.True(Assert.IsType<CombatResolutionResult>(strike.Payload).TargetDied);
        Assert.False(victim.Health.IsAlive);
        await rig.StepAsync(PlayerRespawnRules.DelayTicks + 8);
        Assert.True(victim.Health.IsAlive);
        Assert.Equal(victim.Health.MaxHealth, victim.Health.CurrentHealth);
        Assert.True(rig.Player(rig.B).ProtectedThroughTick > rig.Runtime.CurrentServerTick, "a respawned player is protected");
    }

    [Fact]
    public async Task PvpOn_AnEnemyOnTheRayTakesTheHitBeforeAPlayer()
    {
        using MigrationTestDatabase db = new();
        await using M4BRig rig = await FaceOffAsync(db, pvp: true);
        PlayerCombatant victim = rig.Combatant(rig.B);
        int full = victim.Health.CurrentHealth;
        rig.PutEnemyInFront(rig.A, 1.2f);

        Assert.Equal(1, M4BRig.AssertUsed(await rig.UseAsync(rig.A, new SkillUseIntent("skill.bolt"))).TargetsHit);

        Assert.Equal(full, victim.Health.CurrentHealth);
        Assert.True(rig.Held!.Combatant.Health.CurrentHealth < rig.Held.Combatant.Health.MaxHealth);
    }
}
