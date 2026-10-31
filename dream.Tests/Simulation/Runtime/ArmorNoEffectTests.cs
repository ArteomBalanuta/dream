using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Runtime;

// Wave H1, H-D11: Armor is resolved and readable (PlayerCombatant.EffectiveArmor) but changes no
// combat result in wave H. This pins that on purpose; wave I adds mitigation and changes this test
// deliberately.
public sealed class ArmorNoEffectTests
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
    public void ApplyDamage_IsTheSame_WithArmor40AsWithArmor0()
    {
        PlayerCombatant bare = WithArmor(0f);
        PlayerCombatant armored = WithArmor(40f);

        Assert.Equal(0, bare.EffectiveArmor);
        Assert.Equal(40, armored.EffectiveArmor);
        Assert.Equal(bare.Health.ApplyDamage(6).AppliedAmount, armored.Health.ApplyDamage(6).AppliedAmount);
        Assert.Equal(bare.ApplyDamage(9).AppliedAmount, armored.ApplyDamage(9).AppliedAmount);
        Assert.Equal(bare.Health.CurrentHealth, armored.Health.CurrentHealth);
    }

    [Fact]
    public void AWolfBitingAPlayerWithArmor40_DoesTheSameDamageAsOneWithArmor0()
    {
        int bareHealth = HealthAfterWolfAttacks(WithArmor(0f));
        int armoredHealth = HealthAfterWolfAttacks(WithArmor(40f));

        Assert.True(bareHealth < new PlayerCombatant(() => Vector3.Zero).Health.MaxHealth, "the wolf never bit");
        Assert.Equal(bareHealth, armoredHealth);
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
