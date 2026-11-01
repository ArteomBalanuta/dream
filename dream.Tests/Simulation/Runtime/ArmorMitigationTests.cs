using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Enemies;
using Dream.Simulation.Combat;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Simulation.Runtime;

// Wave I1, I-D1 to I-D3: Armor reduces damage taken (player.damage_taken_factor = 100 / (100 + armor),
// rounded away from zero, at least 1), and Armor 0 stays identical to the raw hit. This replaces the
// wave H test that pinned Armor as having no effect.
public sealed class ArmorMitigationTests
{
    private static PlayerCombatant WithArmor(float armor)
    {
        PlayerCombatant combatant = new(() => new Vector3(1.0f, 1f, 0.5f));
        EquipmentLoadout loadout = new();
        if (armor > 0f)
        {
            loadout.Set(EquipmentSlot.Body, [new StatModifier(StatId.Armor, StatOp.Flat, armor)]);
        }

        combatant.ApplyEquipment(loadout);
        return combatant;
    }

    // The same wolf at the same place bites for the same ticks.
    private static int HealthAfterWolfAttacks(PlayerCombatant player)
    {
        EnemyActor wolf = new(
            new EnemyCombatant(() => new Vector3(0.5f, 1f, 0.5f), actorId: 2900, maxHealth: 30, attackDamage: 6, attackIntervalSeconds: 1f),
            new Vector3(0.5f, 1f, 0.5f),
            aggroRange: 5f,
            attackRange: 1.25f,
            moveSpeed: 3f);
        EnemyRegistry registry = new();
        registry.Add(wolf);
        SceneEventHub events = new();
        for (int step = 0; step < 40; step++)
        {
            registry.UpdateAll(
                deltaTime: 0.25f,
                playerPosition: new Vector3(1.0f, 1f, 0.5f),
                playerCombatant: player,
                events: events,
                map: null);
            wolf.SetPosition(new Vector3(0.5f, 1f, 0.5f));
        }

        return player.Health.CurrentHealth;
    }

    [Fact]
    public void Armor0_ApplyDamage_EqualsTheRawHit_ForEveryAmount1To1000()
    {
        for (int amount = 1; amount <= 1000; amount++)
        {
            PlayerCombatant mitigated = WithArmor(0f);
            HealthState raw = new(mitigated.Health.MaxHealth);
            // A player with 1000 health would end the loop early; give both the same large pool.
            mitigated.Health.SetAuthoritativeState(100000, 100000);
            raw.SetAuthoritativeState(100000, 100000);

            DamageReceipt actual = mitigated.ApplyDamage(amount);
            DamageReceipt expected = raw.ApplyDamage(amount);

            Assert.Equal(expected, actual);
            Assert.Equal(amount, mitigated.MitigateDamage(amount));
        }

        Assert.Equal(1.0, WithArmor(0f).DamageTakenFactor);
        Assert.Equal(0, WithArmor(0f).MitigateDamage(0));
        Assert.Equal(-5, WithArmor(40f).MitigateDamage(-5));
        Assert.Equal(0, WithArmor(40f).MitigateDamage(0));
    }

    [Theory]
    [InlineData(11, 12, 11)]
    [InlineData(11, 18, 16)]
    [InlineData(25, 12, 10)]
    [InlineData(25, 18, 14)]
    [InlineData(40, 12, 9)]
    [InlineData(40, 18, 13)]
    [InlineData(100, 12, 6)]
    [InlineData(100, 18, 9)]
    public void Armor_MitigatesWolfAndDireBites_AsTheDesignTable(int armor, int bite, int expected)
    {
        PlayerCombatant player = WithArmor(armor);
        int before = player.Health.CurrentHealth;

        DamageReceipt receipt = player.ApplyDamage(bite);

        Assert.Equal(expected, receipt.AppliedAmount);
        Assert.Equal(before - expected, player.Health.CurrentHealth);
        Assert.Equal(expected, player.MitigateDamage(bite));
    }

    [Fact]
    public void ADamageOf1_AtArmor1000_StillDoes1()
    {
        PlayerCombatant player = WithArmor(1000f);

        Assert.Equal(1000, player.EffectiveArmor);
        Assert.Equal(1, player.ApplyDamage(1).AppliedAmount);
        Assert.Equal(1, player.MitigateDamage(1));
        Assert.Equal(1, player.MitigateDamage(5));
    }

    [Fact]
    public void AWolfBitingAPlayerWithArmor40_LeavesMoreHealthThanArmor0()
    {
        int bareHealth = HealthAfterWolfAttacks(WithArmor(0f));
        int armoredHealth = HealthAfterWolfAttacks(WithArmor(40f));

        Assert.True(bareHealth < new PlayerCombatant(() => Vector3.Zero).Health.MaxHealth, "the wolf never bit");
        Assert.True(armoredHealth > bareHealth, $"armored {armoredHealth} vs bare {bareHealth}");
    }

    [Fact]
    public void AHitWithArmor40_AllocatesNothing()
    {
        PlayerCombatant player = WithArmor(40f);
        player.Health.SetAuthoritativeState(1_000_000, 1_000_000);

        AllocationAssert.AtMost(0, () => player.ApplyDamage(12));
    }

    // The formula service builds a dictionary per evaluation, so an evaluation shows as allocation. The
    // factor is cached: re-deriving with the same Armor (every client snapshot calls ApplyBaseStats)
    // allocates exactly what re-deriving at Armor 0 does, which evaluates nothing.
    [Fact]
    public void TheFactor_IsEvaluatedOnArmorChangeOnly_NotOnApplyBaseStats()
    {
        PlayerCombatStats stats = new() { Strength = 12, Vitality = 12, AttackSpeed = 1.1f };
        PlayerCombatant bare = WithArmor(0f);
        PlayerCombatant armored = WithArmor(40f);

        long bareBytes = AllocationAssert.MeasureBytesPerIteration(() => bare.ApplyBaseStats(stats), iterations: 100);
        long armoredBytes = AllocationAssert.MeasureBytesPerIteration(() => armored.ApplyBaseStats(stats), iterations: 100);

        Assert.Equal(bareBytes, armoredBytes);

        // An actual change does evaluate: the armor 60 factor replaces the armor 40 one.
        Assert.Equal(100.0 / 140.0, armored.DamageTakenFactor, 12);
        armored.ApplyEquipment(LoadoutWithArmor(60f));
        Assert.Equal(100.0 / 160.0, armored.DamageTakenFactor, 12);
        armored.ApplyEquipment(new EquipmentLoadout());
        Assert.Equal(1.0, armored.DamageTakenFactor);
        Assert.Equal(12, armored.MitigateDamage(12));
    }

    [Fact]
    public void EnemyActorTryAttack_AppliesTheMitigatedDamage()
    {
        PlayerCombatant player = WithArmor(25f);
        EnemyActor wolf = new(
            new EnemyCombatant(() => new Vector3(0.5f, 1f, 0.5f), actorId: 2901, maxHealth: 100, attackDamage: 12, attackIntervalSeconds: 1f),
            new Vector3(0.5f, 1f, 0.5f),
            aggroRange: 5f,
            attackRange: 1.25f,
            moveSpeed: 3f);
        int before = player.Health.CurrentHealth;
        EnemyRegistry registry = new();
        registry.Add(wolf);
        for (int step = 0; step < 4; step++)
        {
            registry.UpdateAll(0.25f, new Vector3(1.0f, 1f, 0.5f), player, new SceneEventHub(), map: null);
        }

        Assert.Equal(before - 10, player.Health.CurrentHealth);
    }

    private static EquipmentLoadout LoadoutWithArmor(float armor)
    {
        EquipmentLoadout loadout = new();
        if (armor > 0f)
        {
            loadout.Set(EquipmentSlot.Body, [new StatModifier(StatId.Armor, StatOp.Flat, armor)]);
        }

        return loadout;
    }

    [Fact]
    public void EffectiveArmor_IsTheSumOfTheEquippedModifiers()
    {
        PlayerCombatant combatant = new(() => Vector3.Zero);
        EquipmentLoadout loadout = new();
        loadout.Set(EquipmentSlot.Head, [new StatModifier(StatId.Armor, StatOp.Flat, 2)]);
        loadout.Set(EquipmentSlot.Body, [new StatModifier(StatId.Armor, StatOp.Flat, 11)]);
        loadout.Set(EquipmentSlot.OffHand, [new StatModifier(StatId.Armor, StatOp.Flat, 5)]);

        combatant.ApplyEquipment(loadout);

        Assert.Equal(18, combatant.EffectiveArmor);
    }
}
