using Dream.Simulation.Inventory;
using Dream.Simulation.Stats;
using Xunit;

namespace Dream.Tests.Simulation.Inventory;

// Wave H C0: the stored form of a roll. Row content is hostile, so Parse never throws and anything
// it does not accept is the inert Damaged roll.
public sealed class ItemRollCodecTests
{
    private static ItemRoll Roll(ItemRarity rarity, params StatModifier[] modifiers) => new(rarity, modifiers);

    [Fact]
    public void Serialize_WritesTheCompactOrderPreservingForm()
    {
        ItemRoll roll = Roll(
            ItemRarity.Rare,
            new StatModifier(StatId.Strength, StatOp.Flat, 6f),
            new StatModifier(StatId.AttackSpeed, StatOp.Flat, 1.75f),
            new StatModifier(StatId.Armor, StatOp.Flat, 4f));

        Assert.Equal(
            """{"v":1,"r":"Rare","m":[["Strength","Flat",6],["AttackSpeed","Flat",1.75],["Armor","Flat",4]]}""",
            ItemRollCodec.Serialize(roll));
    }

    [Theory]
    [InlineData(ItemRarity.Common, 0)]
    [InlineData(ItemRarity.Uncommon, 1)]
    [InlineData(ItemRarity.Rare, 4)]
    public void RoundTrip_KeepsEveryFloatBit_AndTheOrder(ItemRarity rarity, int count)
    {
        float[] values = [0.05f, 1.15f, 1.7000000476837158f, -4.95f, 0.1f + 0.2f, 49.999996f, -0f];
        StatId[] stats = [StatId.AttackSpeed, StatId.Strength, StatId.Armor, StatId.Vitality];
        StatModifier[] modifiers = new StatModifier[count];
        for (int i = 0; i < count; i++)
        {
            StatId stat = stats[i];
            float value = stat == StatId.AttackSpeed ? values[i] : (stat == StatId.Armor ? 13f : (i % 2 == 0 ? 7f : -3f));
            modifiers[i] = new StatModifier(stat, StatOp.Flat, value);
        }

        ItemRoll parsed = ItemRollCodec.Parse(ItemRollCodec.Serialize(Roll(rarity, modifiers)))!;

        Assert.False(parsed.IsDamaged);
        Assert.Equal(rarity, parsed.Rarity);
        Assert.Equal(count, parsed.Modifiers.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(modifiers[i].Stat, parsed.Modifiers[i].Stat);
            Assert.Equal(modifiers[i].Op, parsed.Modifiers[i].Op);
            Assert.Equal(BitConverter.SingleToUInt32Bits(modifiers[i].Value), BitConverter.SingleToUInt32Bits(parsed.Modifiers[i].Value));
        }
    }

    [Fact]
    public void RoundTrip_OfEverySingleAttackSpeedOnTheGrid_IsBitExact()
    {
        for (int k = 0; k <= 200; k++)
        {
            float value = -5f + (k * 0.05f);
            if (value > 5f)
            {
                value = 5f;
            }

            ItemRoll parsed = ItemRollCodec.Parse(ItemRollCodec.Serialize(Roll(ItemRarity.Common, new StatModifier(StatId.AttackSpeed, StatOp.Flat, value))))!;
            Assert.Equal(BitConverter.SingleToUInt32Bits(value), BitConverter.SingleToUInt32Bits(parsed.Modifiers[0].Value));
        }
    }

    [Fact]
    public void ANullText_IsAnUnrolledItem()
    {
        Assert.Null(ItemRollCodec.Parse(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("""{"v":1,"r":"Rare"}""")]
    [InlineData("""{"v":1,"m":[]}""")]
    [InlineData("""{"r":"Rare","m":[]}""")]
    [InlineData("""{"v":2,"r":"Rare","m":[]}""")]
    [InlineData("""{"v":0,"r":"Rare","m":[]}""")]
    [InlineData("""{"v":"1","r":"Rare","m":[]}""")]
    [InlineData("""{"v":1.5,"r":"Rare","m":[]}""")]
    [InlineData("""{"v":1,"r":"Epic","m":[]}""")]
    [InlineData("""{"v":1,"r":"rare","m":[]}""")]
    [InlineData("""{"v":1,"r":2,"m":[]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":{}}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[[]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Strength","Flat"]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Strength","Flat",1,2]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[{"stat":"Strength"}]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Luck","Flat",1]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[[0,"Flat",1]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["5","Flat",1]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["strength","Flat",1]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Strength","Percent",1]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Strength","flat",1]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Strength",0,1]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Agility","Flat",1]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Energy","Flat",1]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["MovementSpeed","Flat",1]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Strength","Flat","1"]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Strength","Flat",null]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Strength","Flat",51]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Strength","Flat",-51]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Vitality","Flat",50.5]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["AttackSpeed","Flat",5.01]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["AttackSpeed","Flat",-5.01]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Armor","Flat",-1]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Armor","Flat",101]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Strength","Flat",1.5]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Vitality","Flat",-3.25]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Armor","Flat",2.5]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Armor","Flat",1e99]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Armor","Flat",-1e99]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[["Strength","Flat",1],["Strength","Flat",2]]}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[],"extra":1}""")]
    [InlineData("""{"v":1,"v":1,"r":"Rare"}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[]} trailing""")]
    [InlineData("""{"v":1,"r":"Rare","m":[],}""")]
    [InlineData("""{"v":1,"r":"Rare","m":[]""")]
    public void ARejectedText_IsTheDamagedRoll(string text)
    {
        ItemRoll? parsed = ItemRollCodec.Parse(text);

        Assert.NotNull(parsed);
        Assert.True(parsed!.IsDamaged);
        Assert.Same(ItemRoll.Damaged, parsed);
        Assert.Equal(ItemRarity.Common, parsed.Rarity);
        Assert.Empty(parsed.Modifiers);
    }

    [Fact]
    public void MoreThanEightModifiers_AreDamaged()
    {
        // Eight stat entries cannot be distinct (four live stats), so the count check sits in front of the
        // duplicate check; both reject.
        string nine = string.Join(",", Enumerable.Range(0, 9).Select(_ => """["Strength","Flat",1]"""));
        Assert.True(ItemRollCodec.Parse($$"""{"v":1,"r":"Rare","m":[{{nine}}]}""")!.IsDamaged);
    }

    [Fact]
    public void ATextOver1024Characters_IsDamaged_EvenWhenOtherwiseValid()
    {
        string padded = """{"v":1,"r":"Rare","m":[["Strength","Flat",1]],"p":""" + "\"" + new string('x', 1100) + "\"}";
        Assert.True(padded.Length > ItemRollCodec.MaxTextLength);
        Assert.True(ItemRollCodec.Parse(padded)!.IsDamaged);
        Assert.True(ItemRollCodec.Parse(new string(' ', ItemRollCodec.MaxTextLength + 1) + "{}")!.IsDamaged);
    }

    [Fact]
    public void ADeeplyNestedArray_DoesNotThrow_AndIsDamaged()
    {
        string deep = new string('[', 1500) + new string(']', 1500);
        Assert.True(ItemRollCodec.Parse(deep)!.IsDamaged);
        string deepInside = """{"v":1,"r":"Rare","m":""" + new string('[', 1500) + "}";
        Assert.True(ItemRollCodec.Parse(deepInside)!.IsDamaged);
        string withinLimit = """{"v":1,"r":"Rare","m":[[[[[[[[1]]]]]]]]}""";
        Assert.True(ItemRollCodec.Parse(withinLimit)!.IsDamaged);
    }

    [Fact]
    public void Parse_NeverThrows_OnRandomAndMutatedText()
    {
        string valid = ItemRollCodec.Serialize(Roll(
            ItemRarity.Uncommon,
            new StatModifier(StatId.Strength, StatOp.Flat, 5f),
            new StatModifier(StatId.AttackSpeed, StatOp.Flat, 1.25f)));
        Random random = new(20261101);
        const string alphabet = "{}[]\",:0123456789.-+eE tfnulrvmStrengthFlat\\\u0000\uD800é";
        for (int i = 0; i < 20_000; i++)
        {
            char[] chars = valid.ToCharArray();
            int edits = 1 + random.Next(4);
            for (int e = 0; e < edits; e++)
            {
                chars[random.Next(chars.Length)] = alphabet[random.Next(alphabet.Length)];
            }

            string text = new(chars);
            if (random.Next(5) == 0)
            {
                text = text[..random.Next(text.Length)];
            }

            ItemRoll? parsed = ItemRollCodec.Parse(text);
            Assert.NotNull(parsed);
            Assert.True(parsed!.Modifiers.Count <= StatSheet.MaxModifiersPerSource);
            foreach (StatModifier modifier in parsed.Modifiers)
            {
                Assert.True(float.IsFinite(modifier.Value));
                Assert.Equal(StatOp.Flat, modifier.Op);
            }
        }
    }

    [Fact]
    public void ADamagedRollThatIsNotTheInstance_CannotBeConfused()
    {
        ItemRoll empty = Roll(ItemRarity.Common);
        Assert.False(empty.IsDamaged);
        Assert.False(ItemRollCodec.Parse(ItemRollCodec.Serialize(empty))!.IsDamaged);
    }

    [Fact]
    public void Serialize_RefusesWhatItCouldNotParse()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemRollCodec.Serialize(Roll(ItemRarity.Common, new StatModifier(StatId.Agility, StatOp.Flat, 1f))));
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemRollCodec.Serialize(Roll(ItemRarity.Common, new StatModifier(StatId.Strength, StatOp.Percent, 1f))));
    }
}
