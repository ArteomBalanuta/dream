using Xunit;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Stats;
using Dream.Tests.Support;
using Dream.Tests.Utilities;

namespace Dream.Tests.Simulation.Stats;

public class StatSheetTests
{
    private static StatBlock BlockOf(float strength, float attackSpeed, float vitality)
    {
        StatBlock block = default;
        block[StatId.Strength] = strength;
        block[StatId.AttackSpeed] = attackSpeed;
        block[StatId.Vitality] = vitality;
        return block;
    }

    private static StatModifier[] ModifiersOf(LegacyToolStats tool) =>
    [
        new(StatId.Strength, StatOp.Flat, tool.StrengthBonus),
        new(StatId.AttackSpeed, StatOp.Flat, tool.AttackSpeedBonus),
        new(StatId.Vitality, StatOp.Flat, tool.VitalityBonus)
    ];

    // Each starter tool as the legacy three-stat modifier (the oracle) and as the definition's modifiers
    // (what production hands to PlayerCombatant.ApplyEquipment).
    private static IEnumerable<(LegacyToolStats Legacy, IReadOnlyList<StatModifier> Definition)> AllTools()
    {
        yield return (default, []);
        foreach (string kind in ToolCatalog.StarterItemKinds)
        {
            IReadOnlyList<StatModifier> modifiers = ToolCatalog.Resolve(kind).Modifiers;
            yield return (LegacyToolStats.From(modifiers), modifiers);
        }
    }

    private static void EquipInMainHand(PlayerCombatant combatant, IReadOnlyList<StatModifier> definition)
    {
        EquipmentLoadout loadout = new();
        loadout.Set(EquipmentSlot.MainHand, definition);
        combatant.ApplyEquipment(loadout);
    }

    [Fact]
    public void Resolution_EqualsLegacyCombine_ForEveryToolAcrossBaseRange_BitForBit()
    {
        // A coarse stride plus both range endpoints (the loop always ends on highBits): the clamp and
        // rounding paths are the same at every stride, so a fine stride added time, not coverage.
        const int stride = 4099;
        int lowBits = BitConverter.SingleToInt32Bits(PlayerCombatStats.MinAttackSpeed);
        int highBits = BitConverter.SingleToInt32Bits(PlayerCombatStats.MaxAttackSpeed);
        StatSheet sheet = new();
        long compared = 0;

        foreach ((LegacyToolStats tool, IReadOnlyList<StatModifier> definition) in AllTools())
        {
            StatModifier[] modifiers = ModifiersOf(tool);
            int step = 0;
            for (int bits = lowBits; ; bits = Math.Min(highBits, bits + stride))
            {
                float attackSpeed = BitConverter.Int32BitsToSingle(bits);
                int strength = 1 + step % 50;
                int vitality = 1 + step * 7 % 50;
                step++;

                PlayerCombatStats baseStats = new() { Strength = strength, AttackSpeed = attackSpeed, Vitality = vitality };
                PlayerCombatStats legacy = LegacyToolStats.Combine(baseStats, tool);

                sheet.SetBase(BlockOf(strength, attackSpeed, vitality));
                sheet.SetSource(0, modifiers);

                Assert.Equal(BitConverter.SingleToInt32Bits(legacy.AttackSpeed), BitConverter.SingleToInt32Bits(sheet.Effective[StatId.AttackSpeed]));
                Assert.Equal(legacy.Strength, (int)sheet.Effective[StatId.Strength]);
                Assert.Equal(legacy.Vitality, (int)sheet.Effective[StatId.Vitality]);
                compared++;

                if (bits == highBits)
                {
                    break;
                }
            }
        }

        Assert.True(compared > 0);
    }

    // The attribute grid these two tests walk is a REPRESENTATIVE set, not every value, and it is
    // deliberately independent of PlayerCombatStats.AttributeCeiling. Both tests used to iterate from
    // Min to Max (one by 1, one by 7). When commit 1f422512 raised the ceiling to 10000 that became
    // 10^8 and 2x10^6 combinations per tool, so the suite hung on them and could never finish -- which
    // also hid every test ordered after them. Stepping by a constant also missed Max entirely
    // (1 + 7k never lands on 10000), so the ceiling boundary was never actually covered. This set keeps
    // both ends, the low edge cases and a spread through the middle, and stays O(1) in the ceiling.
    private static readonly int[] GridAttributes =
    [
        PlayerCombatStats.MinStrength,
        PlayerCombatStats.MinStrength + 1,
        7,
        10,
        50,
        97,
        1_000,
        5_000,
        // The starter-kit strength bonuses are +0, +1, +2, +4 and +8, so the interesting boundary is
        // base + bonus == MaxStrength EXACTLY (the largest value that does not clamp). Max-1 hits it for
        // the +1 item; Max-2 and Max-8 hit it for the +2 and +8 items. Max-1 and Max both clamp for every
        // nonzero bonus, so without these two the exact-ceiling case is never exercised. Do not tidy away.
        PlayerCombatStats.MaxStrength - 8,
        PlayerCombatStats.MaxStrength - 2,
        PlayerCombatStats.MaxStrength - 1,
        PlayerCombatStats.MaxStrength,
    ];

    // The grid is built from the strength constants but walks the vitality axis too. If the two ranges
    // ever diverge the vitality axis would be fed out-of-range values that both paths clamp identically,
    // so the grid tests would still pass while covering less; fail loudly here instead.
    [Fact]
    public void GridAttributes_SuitBothTheStrengthAndTheVitalityAxis()
    {
        Assert.Equal(PlayerCombatStats.MinStrength, PlayerCombatStats.MinVitality);
        Assert.Equal(PlayerCombatStats.MaxStrength, PlayerCombatStats.MaxVitality);
    }

    [Fact]
    public void Resolution_EqualsLegacyCombine_ForEveryStrengthAndVitalityWithEveryTool()
    {
        StatSheet sheet = new();
        foreach ((LegacyToolStats tool, IReadOnlyList<StatModifier> definition) in AllTools())
        {
            StatModifier[] modifiers = ModifiersOf(tool);
            foreach (int strength in GridAttributes)
            {
                foreach (int vitality in GridAttributes)
                {
                    foreach (float attackSpeed in new[] { 0.5f, 1.0f, 2.0f, 4.99f, 5.0f })
                    {
                        PlayerCombatStats legacy = LegacyToolStats.Combine(
                            new PlayerCombatStats { Strength = strength, AttackSpeed = attackSpeed, Vitality = vitality },
                            tool);
                        sheet.SetBase(BlockOf(strength, attackSpeed, vitality));
                        sheet.SetSource(0, modifiers);

                        string at = $"strength={strength} vitality={vitality} attackSpeed={attackSpeed} tool={tool}";
                        Assert.True((float)legacy.Strength == sheet.Effective[StatId.Strength], $"Strength differs ({legacy.Strength} vs {sheet.Effective[StatId.Strength]}) at {at}");
                        Assert.True((float)legacy.Vitality == sheet.Effective[StatId.Vitality], $"Vitality differs ({legacy.Vitality} vs {sheet.Effective[StatId.Vitality]}) at {at}");
                        Assert.True(
                            BitConverter.SingleToInt32Bits(legacy.AttackSpeed) == BitConverter.SingleToInt32Bits(sheet.Effective[StatId.AttackSpeed]),
                            $"AttackSpeed differs ({legacy.AttackSpeed} vs {sheet.Effective[StatId.AttackSpeed]}) at {at}");
                    }
                }
            }
        }
    }

    [Fact]
    public void PlayerCombatant_StatsMatchLegacyCombine_ForEveryTool()
    {
        foreach ((LegacyToolStats tool, IReadOnlyList<StatModifier> definition) in AllTools())
        {
            // Agility 0: the legacy tool combine knows no Agility attack-speed bonus (a fresh character now starts at 10).
            PlayerCombatStats baseStats = new() { Strength = 48, AttackSpeed = 4.5f, Vitality = 3, Agility = 0 };
            PlayerCombatant combatant = new(() => OpenTK.Mathematics.Vector3.Zero, stats: baseStats);
            EquipInMainHand(combatant, definition);

            PlayerCombatStats legacy = LegacyToolStats.Combine(combatant.BaseStats, tool);
            Assert.Equal(legacy.Strength, combatant.Stats.Strength);
            Assert.Equal(legacy.Vitality, combatant.Stats.Vitality);
            Assert.Equal(BitConverter.SingleToInt32Bits(legacy.AttackSpeed), BitConverter.SingleToInt32Bits(combatant.Stats.AttackSpeed));
            Assert.Equal(combatant.Stats.Strength, (int)combatant.StatSheet.Effective[StatId.Strength]);
        }
    }

    [Fact]
    public void PlayerCombatant_GridOfBasesAndTools_MatchesLegacyCombine_SkippingZeroModifiers()
    {
        // The production path: PlayerCombatant leaves zero-valued bonuses out of the source it sets.
        float[] attackSpeeds = [0.5f, 0.75f, 1.0f, 2.5f, 4.99f, 5.0f];
        foreach ((LegacyToolStats tool, IReadOnlyList<StatModifier> definition) in AllTools())
        {
            foreach (int strength in GridAttributes)
            {
                foreach (int vitality in GridAttributes)
                {
                    foreach (float attackSpeed in attackSpeeds)
                    {
                        PlayerCombatStats baseStats = new() { Strength = strength, AttackSpeed = attackSpeed, Vitality = vitality, Agility = 0 };
                        PlayerCombatant combatant = new(() => OpenTK.Mathematics.Vector3.Zero, stats: baseStats);
                        EquipInMainHand(combatant, definition);

                        PlayerCombatStats legacy = LegacyToolStats.Combine(combatant.BaseStats, tool);
                        string at = $"strength={strength} vitality={vitality} attackSpeed={attackSpeed} tool={tool}";
                        Assert.True(legacy.Strength == combatant.Stats.Strength, $"Strength differs ({legacy.Strength} vs {combatant.Stats.Strength}) at {at}");
                        Assert.True(legacy.Vitality == combatant.Stats.Vitality, $"Vitality differs ({legacy.Vitality} vs {combatant.Stats.Vitality}) at {at}");
                        Assert.True(
                            BitConverter.SingleToInt32Bits(legacy.AttackSpeed) == BitConverter.SingleToInt32Bits(combatant.Stats.AttackSpeed),
                            $"AttackSpeed differs ({legacy.AttackSpeed} vs {combatant.Stats.AttackSpeed}) at {at}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Resolution_AppliesFlatThenPercent_InSlotOrder()
    {
        StatSheet sheet = new();
        sheet.SetBase(BlockOf(10f, 2f, 10f));
        sheet.SetSource(3, [new StatModifier(StatId.Strength, StatOp.Percent, 0.5f)]);
        sheet.SetSource(1, [new StatModifier(StatId.Strength, StatOp.Flat, 4f), new StatModifier(StatId.Strength, StatOp.Flat, 2f)]);

        Assert.Equal((10f + (0f + 4f + 2f)) * (1f + 0.5f), sheet.Effective[StatId.Strength]);

        sheet.ClearSource(1);
        Assert.Equal(15f, sheet.Effective[StatId.Strength]);
        sheet.ClearSource(3);
        Assert.Equal(10f, sheet.Effective[StatId.Strength]);
    }

    [Fact]
    public void Resolution_SumsFlatValuesInSlotOrder_NotInsertionOrder()
    {
        // Float addition is not associative: in slot order (1e8 + -1e8) + 1 = 1, while the reverse order
        // (1 + -1e8) + 1e8 loses the 1 and gives 0. Sources are set in the reverse of slot order, so a
        // sheet that summed in insertion order would give 10 instead of 11. (1e8, 1, -1e8 would not do:
        // that triple sums to 0 in both directions.)
        float big = 1e8f;
        float one = 1f;
        float inSlotOrder = 0f;
        inSlotOrder += big;
        inSlotOrder += -big;
        inSlotOrder += one;
        float reversed = 0f;
        reversed += one;
        reversed += -big;
        reversed += big;
        Assert.NotEqual(inSlotOrder, reversed);

        StatSheet sheet = new();
        sheet.SetBase(BlockOf(10f, 2f, 10f));
        sheet.SetSource(2, [new StatModifier(StatId.Strength, StatOp.Flat, one)]);
        sheet.SetSource(1, [new StatModifier(StatId.Strength, StatOp.Flat, -big)]);
        sheet.SetSource(0, [new StatModifier(StatId.Strength, StatOp.Flat, big)]);

        Assert.Equal(10f + inSlotOrder, sheet.Effective[StatId.Strength]);
        Assert.NotEqual(10f + reversed, sheet.Effective[StatId.Strength]);
    }

    [Fact]
    public void Resolution_ClampsEffectiveAndBase()
    {
        StatSheet sheet = new();
        // Over the ceiling, expressed relative to it: a literal 5000 was above the old ceiling and is
        // below today's, so after commit 1f422512 it clamped nothing and this test only still passed
        // because the hanging grid tests above it meant it never ran.
        sheet.SetBase(BlockOf(PlayerCombatStats.MaxStrength + 1f, -3f, 0f));
        Assert.Equal(PlayerCombatStats.MaxStrength, sheet.Base[StatId.Strength]);
        Assert.Equal(0.5f, sheet.Base[StatId.AttackSpeed]);
        Assert.Equal(1f, sheet.Base[StatId.Vitality]);

        sheet.SetBase(BlockOf(PlayerCombatStats.MaxStrength - 2f, 5f, 1f));
        sheet.SetSource(0, [new StatModifier(StatId.Strength, StatOp.Flat, 8f), new StatModifier(StatId.Vitality, StatOp.Percent, -2f)]);
        Assert.Equal(PlayerCombatStats.MaxStrength, sheet.Effective[StatId.Strength]);
        Assert.Equal(1f, sheet.Effective[StatId.Vitality]);
    }

    [Fact]
    public void SetSource_RejectsBadSlotsAndOversizedSources()
    {
        StatSheet sheet = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.SetSource(StatSheet.SourceCapacity, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.ClearSource(-1));
        Assert.Throws<ArgumentException>(() => sheet.SetSource(0, new StatModifier[StatSheet.MaxModifiersPerSource + 1]));
        Assert.Throws<ArgumentException>(() => sheet.SetSource(0, [new StatModifier(StatId.Count, StatOp.Flat, 1f)]));
    }

    [Fact]
    public void Resolution_AllocatesNothing()
    {
        StatSheet sheet = new();
        StatBlock block = BlockOf(20f, 2f, 20f);
        StatModifier[] modifiers = ModifiersOf(LegacyToolStats.From(ToolCatalog.Resolve("tool.field-blade").Modifiers));
        float sink = 0f;
        int cycle = 0;

        // One measured window failed once in a full run. StatSheet has no allocating statement
        // (fixed arrays, no boxing), so the stray bytes were runtime tiering or on-stack
        // replacement charged to the test thread. AllocationAssert warms past the tier-up
        // threshold and keeps the cleanest of several rounds, so a real allocation (present in
        // every round) still fails while a one-off JIT charge does not.
        long bytesPerCycle = AllocationAssert.MeasureBytesPerIteration(
            () =>
            {
                int slot = cycle++ & (StatSheet.SourceCapacity - 1);
                sheet.SetBase(block);
                sheet.SetSource(slot, modifiers);
                sink += sheet.Effective[StatId.Strength];
                sheet.ClearSource(slot);
            },
            iterations: 2_000);

        Assert.True(sink > 0f);
        Assert.Equal(0, bytesPerCycle);
    }
}
