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
// rounded away from zero, at least 1). This replaces the wave H test that pinned Armor as having no
// effect.
//
// The armor the formula is evaluated for is PlayerCombatant.MitigationArmor = gear Armor + Defense, and
// Defense = Agility / 4, so a default character (Agility 10, Defense 2) mitigates ~2% with no gear at
// all. The pure-Armor design table below therefore builds its player with Agility 0; every such test is
// paired with a default-Agility case asserting a *different* number, so zeroing Agility can never turn
// the Defense term into dead code without a test going red.
public sealed class ArmorMitigationTests
{
    private const int NoAgility = PlayerCombatStats.MinAgility;

    // Defense of the unseeded sheet (PlayerCombatStats.UnseededAttribute = 10): 10 / 4 = 2, not the
    // starting Agility, which is 1000.
    private const int DefaultDefense = PlayerCombatStats.UnseededAttribute / 4;

    private static PlayerCombatant WithArmor(float armor, int agility = PlayerCombatStats.UnseededAttribute)
    {
        PlayerCombatant combatant = new(() => new Vector3(1.0f, 1f, 0.5f), stats: new PlayerCombatStats { Agility = agility });
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

    // Pure armor: Agility 0 so MitigationArmor is 0 too, which is the only state that takes a hit
    // unchanged. Paired with Armor0_AtTheDefaultAgility_StillMitigates below.
    [Fact]
    public void Armor0_AndAgility0_ApplyDamage_EqualsTheRawHit_ForEveryAmount1To1000()
    {
        for (int amount = 1; amount <= 1000; amount++)
        {
            PlayerCombatant mitigated = WithArmor(0f, NoAgility);
            HealthState raw = new(mitigated.Health.MaxHealth);
            // A player with 1000 health would end the loop early; give both the same large pool.
            mitigated.Health.SetAuthoritativeState(100000, 100000);
            raw.SetAuthoritativeState(100000, 100000);

            DamageReceipt actual = mitigated.ApplyDamage(amount);
            DamageReceipt expected = raw.ApplyDamage(amount);

            Assert.Equal(expected, actual);
            Assert.Equal(amount, mitigated.MitigateDamage(amount));
        }

        Assert.Equal(0, WithArmor(0f, NoAgility).MitigationArmor);
        Assert.Equal(1.0, WithArmor(0f, NoAgility).DamageTakenFactor);
        Assert.Equal(0, WithArmor(0f, NoAgility).MitigateDamage(0));
        Assert.Equal(-5, WithArmor(40f, NoAgility).MitigateDamage(-5));
        Assert.Equal(0, WithArmor(40f, NoAgility).MitigateDamage(0));
    }

    // The pairing for the test above: the same gear (none) at the default Agility is *not* the raw hit,
    // because Defense 2 is still armor. factor = 100 / (100 + 2); 26 * 100 / 102 = 25.49 -> 25,
    // 100 * 100 / 102 = 98.04 -> 98, and 1 * 100 / 102 = 0.98 -> the floor of 1.
    [Fact]
    public void Armor0_AtTheDefaultAgility_StillMitigates_BecauseDefenseIsArmor()
    {
        PlayerCombatant player = WithArmor(0f);

        Assert.Equal(0, player.EffectiveArmor);
        Assert.Equal(DefaultDefense, player.Defense);
        Assert.Equal(DefaultDefense, player.MitigationArmor);
        Assert.Equal(100.0 / 102.0, player.DamageTakenFactor, 12);
        Assert.Equal(25, player.MitigateDamage(26));
        Assert.Equal(98, player.MitigateDamage(100));
        Assert.Equal(1, player.MitigateDamage(1));
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
        // Agility 0, so the table is pure Armor. The paired default-Agility rows are the theory below.
        PlayerCombatant player = WithArmor(armor, NoAgility);
        int before = player.Health.CurrentHealth;

        DamageReceipt receipt = player.ApplyDamage(bite);

        Assert.Equal(expected, receipt.AppliedAmount);
        Assert.Equal(before - expected, player.Health.CurrentHealth);
        Assert.Equal(expected, player.MitigateDamage(bite));
    }

    // The pairing for the design table: the same gear at the default Agility mitigates one point more,
    // because MitigationArmor is Armor + 2. 12 * 100 / 127 = 9.45 -> 9 (the table's 10 at armor 25) and
    // 12 * 100 / 142 = 8.45 -> 8 (the table's 9 at armor 40). If the Defense term were dropped these
    // would read 10 and 9 and this test would fail.
    [Theory]
    [InlineData(25, 12, 9)]
    [InlineData(40, 12, 8)]
    public void Armor_AtTheDefaultAgility_MitigatesOneMoreThanTheDesignTable(int armor, int bite, int expected)
    {
        PlayerCombatant player = WithArmor(armor);
        int before = player.Health.CurrentHealth;

        Assert.Equal(armor + DefaultDefense, player.MitigationArmor);
        Assert.Equal(expected, player.ApplyDamage(bite).AppliedAmount);
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

        // An actual change does evaluate: the armor 60 factor replaces the armor 40 one. Default Agility
        // here, so the mitigation armor is gear + Defense 2: 42, then 62, then 2 with the gear off.
        Assert.Equal(100.0 / 142.0, armored.DamageTakenFactor, 12);
        armored.ApplyEquipment(LoadoutWithArmor(60f));
        Assert.Equal(100.0 / 162.0, armored.DamageTakenFactor, 12);
        armored.ApplyEquipment(new EquipmentLoadout());
        Assert.Equal(100.0 / 102.0, armored.DamageTakenFactor, 12);
        // 12 * 100 / 102 = 11.76, rounded away from zero, so a 12 bite is still a 12 at Defense 2.
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

        // Armor 25 + Defense 2 = 27; 12 * 100 / 127 = 9.45, rounded away from zero -> 9.
        Assert.Equal(before - 9, player.Health.CurrentHealth);
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

        // EffectiveArmor is gear only; MitigationArmor is what the formula divides by.
        Assert.Equal(18, combatant.EffectiveArmor);
        Assert.Equal(DefaultDefense, combatant.Defense);
        Assert.Equal(18 + DefaultDefense, combatant.MitigationArmor);
    }
}
