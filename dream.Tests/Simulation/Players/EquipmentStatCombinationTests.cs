using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Stats;

namespace Dream.Tests.Simulation.Players;

// Wave H tester: every combination of what the four equipment slots can hold, at the extremes of the
// authored roll ranges, against a grid of base stats, checked against a plain oracle. Effective stats
// are base plus the flat sum of the four sources, clamped to the caps; the base stats never move; and
// taking everything off again restores the bare stats bit for bit.
public sealed class EquipmentStatCombinationTests
{
    private sealed record Option(string Name, StatModifier[] Modifiers);

    private static ItemDefinition Def(string key) =>
        ContentCatalog.Shipped.TryGetItem(key, out ItemDefinition item) ? item : throw new InvalidOperationException(key);

    private static Option Authored(string key) => new(key, [.. Def(key).Modifiers]);

    // A rolled item at the bottom or the top of its authored range for the rarity.
    private static Option Rolled(string key, ItemRarity rarity, bool top) => new(
        $"{key}:{rarity}:{(top ? "max" : "min")}",
        [.. Def(key).Rolls!.For(rarity).Select(r => new StatModifier(r.Stat, StatOp.Flat, top ? r.Max : r.Min))]);

    private static readonly Option None = new("empty", []);

    private static IEnumerable<Option> MainHand() =>
    [
        None, Authored("tool.field-blade"), Authored("tool.stone-maul"), Authored("tool.builder-hammer"),
        Rolled("gear.iron-blade", ItemRarity.Common, false), Rolled("gear.iron-blade", ItemRarity.Rare, true)
    ];

    private static IEnumerable<Option> OffHand() =>
    [
        None, Authored("tool.torch"), Authored("tool.survey-lantern"),
        Rolled("gear.ward-lantern", ItemRarity.Common, false), Rolled("gear.ward-lantern", ItemRarity.Rare, true)
    ];

    private static IEnumerable<Option> Head() =>
    [
        None, Rolled("gear.leather-cap", ItemRarity.Common, false), Rolled("gear.leather-cap", ItemRarity.Rare, true)
    ];

    private static IEnumerable<Option> Body() =>
    [
        None, Rolled("gear.leather-vest", ItemRarity.Common, false), Rolled("gear.leather-vest", ItemRarity.Rare, true)
    ];

    private static readonly int[] StrengthGrid = [PlayerCombatStats.MinStrength, 10, 49, PlayerCombatStats.MaxStrength];
    private static readonly int[] VitalityGrid = [PlayerCombatStats.MinVitality, 10, 45, PlayerCombatStats.MaxVitality];
    private static readonly float[] SpeedGrid = [PlayerCombatStats.MinAttackSpeed, 2f, PlayerCombatStats.MaxAttackSpeed];

    // The unseeded sheet (10) and one value that is not, so the Agility term in the attack speed is exercised and
    // cannot be satisfied by a single point on the grid. 37 also spans the Defense step (37 / 4 = 9).
    private static readonly int[] AgilityGrid = [PlayerCombatStats.UnseededAttribute, 37];

    // Effective stats, worked out from the constants rather than by calling the production rule: the
    // sheet sums the flats and clamps, then attack speed gains AttackSpeedPerAgility per point of
    // effective Agility and is clamped again. Calling PlayerStatRules.EffectiveAttackSpeed here would
    // make the comparison tautological, so the two clamps and the multiply are written out.
    private static (float Strength, float Speed, float Vitality, float Armor, int Agility) Oracle(
        int strength, float speed, int vitality, int agility, Option[] worn)
    {
        float s = strength;
        float a = speed;
        float v = vitality;
        float g = agility;
        float armor = 0f;
        foreach (Option option in worn)
        {
            foreach (StatModifier m in option.Modifiers)
            {
                Assert.Equal(StatOp.Flat, m.Op);
                switch (m.Stat)
                {
                    case StatId.Strength: s += m.Value; break;
                    case StatId.AttackSpeed: a += m.Value; break;
                    case StatId.Vitality: v += m.Value; break;
                    case StatId.Agility: g += m.Value; break;
                    case StatId.Armor: armor += m.Value; break;
                }
            }
        }

        int effectiveAgility = (int)Math.Clamp(g, PlayerCombatStats.MinAgility, PlayerCombatStats.MaxAgility);
        float sheetSpeed = Math.Clamp(a, PlayerCombatStats.MinAttackSpeed, PlayerCombatStats.MaxAttackSpeed);
        float withAgility = Math.Clamp(
            sheetSpeed + (PlayerStatRules.AttackSpeedPerAgility * effectiveAgility),
            PlayerCombatStats.MinAttackSpeed,
            PlayerCombatStats.MaxAttackSpeed);

        return (
            Math.Clamp(s, PlayerCombatStats.MinStrength, PlayerCombatStats.MaxStrength),
            withAgility,
            Math.Clamp(v, PlayerCombatStats.MinVitality, PlayerCombatStats.MaxVitality),
            Math.Clamp(armor, 0f, StatRules.DefaultMaxUnused),
            effectiveAgility);
    }

    [Fact]
    public void EveryFourSlotCombination_ResolvesToTheOracle_NeverPassesACap_AndLeavesTheBaseAlone()
    {
        EquipmentLoadout loadout = new();
        EquipmentLoadout bare = new();
        int cases = 0;
        int capped = 0;
        foreach (Option main in MainHand())
        foreach (Option off in OffHand())
        foreach (Option head in Head())
        foreach (Option body in Body())
        {
            Option[] worn = [main, off, head, body];
            loadout.Set(EquipmentSlot.MainHand, main.Modifiers);
            loadout.Set(EquipmentSlot.OffHand, off.Modifiers);
            loadout.Set(EquipmentSlot.Head, head.Modifiers);
            loadout.Set(EquipmentSlot.Body, body.Modifiers);
            foreach (int strength in StrengthGrid)
            foreach (int vitality in VitalityGrid)
            foreach (float speed in SpeedGrid)
            foreach (int agility in AgilityGrid)
            {
                PlayerCombatant combatant = new(() => Vector3.Zero, stats: new PlayerCombatStats
                {
                    Strength = strength,
                    Vitality = vitality,
                    AttackSpeed = speed,
                    Agility = agility
                });
                string where = $"[{string.Join(" | ", worn.Select(w => w.Name))}] base {strength}/{speed}/{vitality}/ag {agility}";
                PlayerCombatStatsSnapshot baseBefore = PlayerCombatStatsSnapshot.From(combatant.BaseStats);
                int bareMax = combatant.Health.MaxHealth;
                int bareArmor = combatant.EffectiveArmor;
                float bareSpeedBits = combatant.Stats.AttackSpeed;

                combatant.ApplyEquipment(loadout);
                (float s, float a, float v, float armor, int ag) = Oracle(strength, speed, vitality, agility, worn);

                Assert.True(s == combatant.Stats.Strength, $"{where}: strength {combatant.Stats.Strength} vs {s}");
                Assert.True(
                    BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(combatant.Stats.AttackSpeed),
                    $"{where}: attack speed {combatant.Stats.AttackSpeed} vs {a}");
                Assert.True(v == combatant.Stats.Vitality, $"{where}: vitality {combatant.Stats.Vitality} vs {v}");
                Assert.True((int)armor == combatant.EffectiveArmor, $"{where}: armor {combatant.EffectiveArmor} vs {armor}");
                Assert.True(ag == combatant.Stats.Agility, $"{where}: agility {combatant.Stats.Agility} vs {ag}");

                Assert.InRange(combatant.Stats.Strength, PlayerCombatStats.MinStrength, PlayerCombatStats.MaxStrength);
                Assert.InRange(combatant.Stats.Vitality, PlayerCombatStats.MinVitality, PlayerCombatStats.MaxVitality);
                Assert.InRange(combatant.Stats.AttackSpeed, PlayerCombatStats.MinAttackSpeed, PlayerCombatStats.MaxAttackSpeed);
                Assert.True(combatant.Health.MaxHealth >= bareMax || v < vitality, $"{where}: max health fell with more vitality");
                Assert.InRange(combatant.Health.CurrentHealth, 1, combatant.Health.MaxHealth);

                // Correction 6, server side: the base stats are what a stats update saves, and gear never edits them.
                Assert.Equal(baseBefore, PlayerCombatStatsSnapshot.From(combatant.BaseStats));

                if (s != strength + worn.Sum(w => w.Modifiers.Where(m => m.Stat == StatId.Strength).Sum(m => m.Value))
                    || v != vitality + worn.Sum(w => w.Modifiers.Where(m => m.Stat == StatId.Vitality).Sum(m => m.Value)))
                {
                    capped++;
                }

                // The same gear again changes nothing; then everything off restores the bare stats exactly.
                int maxWith = combatant.Health.MaxHealth;
                combatant.ApplyEquipment(loadout);
                Assert.Equal(maxWith, combatant.Health.MaxHealth);
                combatant.ApplyEquipment(bare);
                Assert.True(strength == combatant.Stats.Strength, $"{where}: bare strength");
                Assert.True(vitality == combatant.Stats.Vitality, $"{where}: bare vitality");
                Assert.Equal(BitConverter.SingleToInt32Bits(bareSpeedBits), BitConverter.SingleToInt32Bits(combatant.Stats.AttackSpeed));
                Assert.Equal(bareArmor, combatant.EffectiveArmor);
                Assert.Equal(bareMax, combatant.Health.MaxHealth);
                cases++;
            }
        }

        Assert.True(cases >= 12_000, $"{cases} cases");
        Assert.True(capped > 0, "the grid must reach a cap, or it proves nothing about caps");
    }

    [Fact]
    public void AnItemInTheWrongSlot_NeverReachesTheSheet_ThroughTheLoadout()
    {
        // A loadout holds what Set is given for the slot; the slot rule lives in the inventory service. Here:
        // a non-finite, unknown-stat or unknown-op modifier is dropped, never thrown, whichever slot gets it.
        EquipmentLoadout loadout = new();
        foreach (EquipmentSlot slot in Enum.GetValues<EquipmentSlot>())
        {
            loadout.Set(slot,
            [
                new StatModifier(StatId.Strength, StatOp.Flat, float.NaN),
                new StatModifier(StatId.Strength, StatOp.Flat, float.PositiveInfinity),
                new StatModifier((StatId)99, StatOp.Flat, 5f),
                new StatModifier(StatId.Strength, (StatOp)9, 5f),
                new StatModifier(StatId.Strength, StatOp.Flat, 3f)
            ]);
            Assert.Single(loadout.Get(slot).ToArray());
        }

        PlayerCombatant combatant = new(() => Vector3.Zero);
        int before = combatant.Stats.Strength;
        combatant.ApplyEquipment(loadout);
        Assert.Equal(before + (3 * EquipmentSlots.Count), combatant.Stats.Strength);
    }
}
