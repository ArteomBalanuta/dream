using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Runtime;

// The timed MeleeArc swing at the runtime: damage only in ticks [cast + WindupTicks, cast + WindupTicks +
// ActiveTicks), once per target per cast, and only while the caster still has the right to swing.
public sealed class MeleeSwingWindowTests
{
    private const string Cleave = "skill.cleave";
    private static readonly PlayerSessionId A = new("swing-a");

    private sealed class Rig : IDisposable
    {
        private int _nextEnemyId = 9100;

        public Rig()
        {
            Runtime = new WorldSessionRuntime(WorldDefaults.StartupFloorSize, WaveIRuntimeKit.FarStart, Events);
            Runtime.AttachRemoteSession(A, "Alice");
            WaveIRuntimeKit.Tick(Runtime, ref Now, 10);
            Target = WaveIRuntimeKit.Target(Runtime, A.Value);
            WaveIRuntimeKit.EnterCombat(Runtime, A);
            Events.Subscribe<DamageAppliedEvent>(damage => Hits.Add((Now, damage.Actor.Id)));
        }

        public SceneEventHub Events { get; } = new();

        public WorldSessionRuntime Runtime { get; }

        public IAuthoritativePlayerTarget Target { get; }

        public ulong Now;

        // (tick the hit landed in, enemy actor id)
        public List<(ulong Tick, int Actor)> Hits { get; } = [];

        public Vector3 InFront(float distance, float side = 0f)
        {
            Vector3 facing = new(Target.Facing.X, 0f, Target.Facing.Z);
            facing.Normalize();
            Vector3 right = new(-facing.Z, 0f, facing.X);
            return Target.EyePosition + (facing * distance) + (right * side);
        }

        public EnemyActor AddEnemy(Vector3 anchor)
        {
            Vector3 position = anchor - new Vector3(0f, WolfDefinition.CombatAnchorHeight, 0f);
            EnemyCombatant combatant = new(() => position, Events, actorId: _nextEnemyId++, maxHealth: 1_000_000, attackDamage: 1, attackIntervalSeconds: 1f);
            EnemyActor enemy = new(combatant, position, aggroRange: 0f, attackRange: 0f, moveSpeed: 0f, displayName: "Wolf", level: 1);
            Runtime.Enemies.Add(enemy);
            return enemy;
        }

        public static void Place(EnemyActor enemy, Vector3 anchor)
            => enemy.SetPosition(anchor - new Vector3(0f, WolfDefinition.CombatAnchorHeight, 0f));

        public AuthoritativeActionResult Cast(ulong sequence = 1)
            => Runtime.ProcessAuthoritativeAction(new PlayerActionMessage(A, sequence, new SkillUseIntent(Cleave)));

        public void Tick(int count = 1) => WaveIRuntimeKit.Tick(Runtime, ref Now, count);

        public void Dispose() => Runtime.Dispose();
    }

    private static int Window => MeleeArcSwing.WindupTicks + MeleeArcSwing.ActiveTicks;

    [Fact]
    public void TwentyEnemiesInTheArc_AreAllHitExactlyOnce_OnlyInsideTheActiveWindow()
    {
        using Rig rig = new();
        List<EnemyActor> crowd = [];
        for (int i = 0; i < 20; i++)
        {
            crowd.Add(rig.AddEnemy(rig.InFront(1.2f + ((i % 5) * 0.3f), ((i / 5) - 1.5f) * 0.3f)));
        }

        ulong cast = rig.Now;
        AbilityUsedResult used = Assert.IsType<AbilityUsedResult>(rig.Cast().Payload);
        Assert.Equal(0, used.TargetsHit);
        rig.Tick(Window + 20);

        Assert.Equal(20, rig.Hits.Count);
        Assert.Equal(20, rig.Hits.Select(h => h.Actor).Distinct().Count());
        Assert.All(rig.Hits, h => Assert.InRange(h.Tick, cast + (ulong)MeleeArcSwing.WindupTicks, cast + (ulong)Window - 1));
        Assert.All(crowd, e => Assert.Contains(rig.Hits, h => h.Actor == e.Combatant.Actor.Id));
    }

    [Fact]
    public void NothingIsHitBeforeTheWindowOpens_AndTheFirstHitLandsOnItsFirstTick()
    {
        using Rig rig = new();
        EnemyActor enemy = rig.AddEnemy(rig.InFront(1.5f));
        ulong cast = rig.Now;
        Assert.True(rig.Cast().Accepted);

        rig.Tick(MeleeArcSwing.WindupTicks - 1);
        Assert.Empty(rig.Hits);
        Assert.Equal(enemy.Combatant.Health.MaxHealth, enemy.Combatant.Health.CurrentHealth);

        rig.Tick();
        Assert.Equal(cast + (ulong)MeleeArcSwing.WindupTicks, rig.Now);
        Assert.Single(rig.Hits);
    }

    [Fact]
    public void AnEnemyInTheArcTheWholeWindow_TakesExactlyOneDamageEvent()
    {
        using Rig rig = new();
        rig.AddEnemy(rig.InFront(1.5f));
        Assert.True(rig.Cast().Accepted);

        rig.Tick(Window + 40);

        Assert.Single(rig.Hits);
    }

    [Fact]
    public void AnEnemyWalkingInDuringTheWindow_IsHit_AndOneLeavingBeforeItIsNot()
    {
        using Rig rig = new();
        EnemyActor late = rig.AddEnemy(rig.InFront(30f));
        EnemyActor early = rig.AddEnemy(rig.InFront(1.5f, 0.2f));
        ulong cast = rig.Now;
        Assert.True(rig.Cast().Accepted);

        rig.Tick(5);
        Rig.Place(early, rig.InFront(30f));
        rig.Tick(MeleeArcSwing.WindupTicks + 3 - 5);
        Rig.Place(late, rig.InFront(1.5f));
        rig.Tick(Window + 20);

        Assert.DoesNotContain(rig.Hits, h => h.Actor == early.Combatant.Actor.Id);
        (ulong tick, int actor) = Assert.Single(rig.Hits);
        Assert.Equal(late.Combatant.Actor.Id, actor);
        Assert.True(tick >= cast + (ulong)MeleeArcSwing.WindupTicks + 3);
    }

    [Fact]
    public void AnEnemyThatLeavesAfterBeingHit_IsNotHitAgainWhenItComesBack()
    {
        using Rig rig = new();
        EnemyActor enemy = rig.AddEnemy(rig.InFront(1.5f));
        Assert.True(rig.Cast().Accepted);

        rig.Tick(MeleeArcSwing.WindupTicks);
        Assert.Single(rig.Hits);
        Rig.Place(enemy, rig.InFront(30f));
        rig.Tick(2);
        Rig.Place(enemy, rig.InFront(1.5f));
        rig.Tick(Window);

        Assert.Single(rig.Hits);
    }

    [Fact]
    public void ACasterWhoDiesInTheWindup_HitsNothing()
    {
        Assert.True(PlayerRespawnRules.DelayTicks > Window, "the caster would respawn mid-swing");
        using Rig rig = new();
        rig.AddEnemy(rig.InFront(1.5f));
        Assert.True(rig.Cast().Accepted);

        rig.Tick(5);
        WaveIRuntimeKit.Kill(rig.Runtime, A.Value);
        rig.Tick(Window + 5);

        Assert.Empty(rig.Hits);
    }

    [Fact]
    public void ACasterWhoDisconnectsInTheWindup_HitsNothing()
    {
        using Rig rig = new();
        rig.AddEnemy(rig.InFront(1.5f));
        Assert.True(rig.Cast().Accepted);

        rig.Tick(5);
        rig.Runtime.DetachRemoteSession(A);
        rig.Tick(Window + 5);

        Assert.Empty(rig.Hits);
    }

    [Fact]
    public void ACasterWhoSwitchesToBuildingInTheWindup_HitsNothing_AndGetsNoRefund()
    {
        using Rig rig = new();
        rig.AddEnemy(rig.InFront(1.5f));
        Assert.True(ContentCatalog.Shipped.TryGetSkill(Cleave, out SkillDefinition? cleave));
        Assert.True(rig.Cast().Accepted);
        Assert.True(rig.Runtime.Sessions.TryGet(A, out ConnectedPlayer? player));

        rig.Tick(5);
        rig.Runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Building);
        rig.Tick(Window + 5);

        Assert.Empty(rig.Hits);
        Assert.False(player!.Cooldowns.IsReady(Cleave, rig.Now));
        Assert.True(rig.Target.Combatant.CurrentMana < rig.Target.Combatant.MaxMana);
    }

    [Fact]
    public void ASwitchToBuildingAndBackBeforeTheWindow_StillSwings()
    {
        using Rig rig = new();
        rig.AddEnemy(rig.InFront(1.5f));
        Assert.True(rig.Cast().Accepted);

        rig.Tick(3);
        rig.Runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Building);
        rig.Tick(2);
        rig.Runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
        rig.Tick(Window + 5);

        Assert.Single(rig.Hits);
    }

    [Fact]
    public void ASecondCastAfterTheCooldown_SwingsAgain_WithAFreshHitSet()
    {
        using Rig rig = new();
        rig.AddEnemy(rig.InFront(1.5f));
        Assert.True(ContentCatalog.Shipped.TryGetSkill(Cleave, out SkillDefinition? cleave));

        Assert.True(rig.Cast(1).Accepted);
        rig.Tick(Math.Max(Window + 5, cleave!.CooldownTicks + 1));
        Assert.Single(rig.Hits);

        Assert.True(rig.Cast(2).Accepted);
        rig.Tick(Window + 5);
        Assert.Equal(2, rig.Hits.Count);
    }

    // ---- PvP: the same re-checks apply to a player in the cone.

    private static readonly PlayerSessionId B = new("swing-b");

    // A second seat standing in A's arc with PvP on and both seats past join protection.
    private static IAuthoritativePlayerTarget AddVictim(Rig rig)
    {
        rig.Runtime.PvpEnabled = true;
        rig.Runtime.AttachRemoteSession(B, "Bob");
        rig.Tick(10);
        WaveIRuntimeKit.EndProtection(rig.Runtime, A);
        WaveIRuntimeKit.EndProtection(rig.Runtime, B);
        IAuthoritativePlayerTarget victim = WaveIRuntimeKit.Target(rig.Runtime, B.Value);
        Vector3 facing = new(rig.Target.Facing.X, 0f, rig.Target.Facing.Z);
        facing.Normalize();
        victim.SetWorldPosition(rig.Target.Position + (facing * 1.5f));
        victim.Combatant.Health.SetAuthoritativeState(10_000, 10_000);
        return victim;
    }

    [Fact]
    public void APlayerInTheCone_IsHitInTheWindow_WhichProvesThePvpRigsBelowCanFail()
    {
        using Rig rig = new();
        IAuthoritativePlayerTarget victim = AddVictim(rig);
        Assert.True(rig.Cast().Accepted);

        rig.Tick(Window + 5);

        Assert.True(victim.Combatant.Health.CurrentHealth < 10_000);
    }

    [Fact]
    public void AVictimWhoGainsSpawnProtectionInTheWindup_IsNotHit()
    {
        using Rig rig = new();
        IAuthoritativePlayerTarget victim = AddVictim(rig);
        Assert.True(rig.Cast().Accepted);

        rig.Tick(5);
        Assert.True(rig.Runtime.Sessions.TryGet(B, out ConnectedPlayer? bob));
        bob!.ProtectedThroughTick = rig.Now + 1_000;
        rig.Tick(Window + 5);

        Assert.Equal(10_000, victim.Combatant.Health.CurrentHealth);
    }

    [Fact]
    public void AVictimWhoDiesInTheWindup_IsNotHit()
    {
        using Rig rig = new();
        IAuthoritativePlayerTarget victim = AddVictim(rig);
        Assert.True(rig.Cast().Accepted);

        rig.Tick(5);
        victim.Combatant.Health.ApplyDamage(1_000_000);
        Assert.False(victim.Combatant.Health.IsAlive);
        int hits = 0;
        rig.Events.Subscribe<DamageAppliedEvent>(damage => { if (damage.Actor.Id == victim.Combatant.Actor.Id) { hits++; } });
        rig.Tick(Window + 5);

        Assert.Equal(0, hits);
    }

    [Fact]
    public void PvpTurnedOffMidSwing_NoPlayerIsHitAfterwards()
    {
        using Rig rig = new();
        IAuthoritativePlayerTarget victim = AddVictim(rig);
        Assert.True(rig.Cast().Accepted);

        rig.Tick(MeleeArcSwing.WindupTicks - 3);
        rig.Runtime.PvpEnabled = false;
        rig.Tick(Window + 5);

        Assert.Equal(10_000, victim.Combatant.Health.CurrentHealth);
    }

    [Fact]
    public void ACasterWhoDetachesAndReattachesInsideTheWindow_HasNoSwing()
    {
        using Rig rig = new();
        rig.AddEnemy(rig.InFront(1.5f));
        Assert.True(rig.Cast().Accepted);

        rig.Tick(5);
        rig.Runtime.DetachRemoteSession(A);
        rig.Runtime.AttachRemoteSession(A, "Alice");
        WaveIRuntimeKit.EnterCombat(rig.Runtime, A);
        rig.Tick(Window + 5);

        Assert.Empty(rig.Hits);
    }
}
