using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Xunit;

namespace Dream.Tests.Simulation.Content;

// Classic HUD iteration 2, C0a: the optional "footprint" of an item and the eight appended equipment
// slot names. The fixtures are inline, so they do not move when the shipped items.json does.
public sealed class ItemFootprintContentTests
{
    private const string Enemies = """
        {"version":2,"enemies":[{"key":"Wolf","displayName":"Wolf","level":1,"strength":10,"vitality":10,"attackSpeed":1.0,
        "aggroRange":10,"attackRange":1.35,"moveSpeed":2.8,"visualKey":"enemy.wolf","spawnRole":"Primary"}]}
        """;

    private static string Items(string extra = "", string slots = "\"MainHand\"", string key = "gear.test") => $$"""
        {"version":2,"starterKitVersion":1,"unrecordedStarterKitVersion":0,"starterKit":[],"items":[
        {"key":"{{key}}","displayName":"Test","description":"d","visualId":"held.test","detailLines":[],"heldLight":"None",
        "modifiers":[],"equipSlots":[{{slots}}]{{extra}}
        }]}
        """;

    private static ContentCatalog Load(string items) => ContentCatalog.Load(items, Enemies);

    private static ItemDefinition Only(ContentCatalog catalog)
    {
        Assert.True(catalog.TryGetItem("gear.test", out ItemDefinition definition));
        return definition;
    }

    [Fact]
    public void AnItemWithNoFootprint_IsOneByOne()
    {
        Assert.Equal(ItemFootprint.One, Only(Load(Items())).Footprint);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    [InlineData(2, 4)]
    [InlineData(4, 1)]
    [InlineData(4, 4)]
    public void AFootprint_IsReadAsColumnsThenRows(int cols, int rows)
    {
        ItemDefinition definition = Only(Load(Items($",\"footprint\":[{cols},{rows}]")));
        Assert.Equal(new ItemFootprint((byte)cols, (byte)rows), definition.Footprint);
    }

    [Theory]
    [InlineData(",\"footprint\":[0,1]", "outside the range 1..4")]
    [InlineData(",\"footprint\":[1,0]", "outside the range 1..4")]
    [InlineData(",\"footprint\":[5,1]", "outside the range 1..4")]
    [InlineData(",\"footprint\":[1,5]", "outside the range 1..4")]
    [InlineData(",\"footprint\":[-1,2]", "outside the range 1..4")]
    [InlineData(",\"footprint\":[300,2]", "outside the range 1..4")]
    [InlineData(",\"footprint\":[2]", "must be [columns, rows]")]
    [InlineData(",\"footprint\":[]", "must be [columns, rows]")]
    [InlineData(",\"footprint\":[1,2,3]", "must be [columns, rows]")]
    [InlineData(",\"footprint\":2", "expected an array")]
    [InlineData(",\"footprint\":\"2x3\"", "expected an array")]
    [InlineData(",\"footprint\":null", "expected an array")]
    [InlineData(",\"footprint\":[1.5,2]", "expected an integer")]
    [InlineData(",\"footprint\":[\"2\",\"3\"]", "expected an integer")]
    [InlineData(",\"footprint\":{\"cols\":2,\"rows\":3}", "expected an array")]
    public void AMalformedFootprint_IsRejected_NamingThePath(string extra, string problem)
    {
        ContentValidationException exception = Assert.Throws<ContentValidationException>(() => Load(Items(extra)));
        Assert.Contains("$.items[0].footprint", exception.Message);
        Assert.Contains(problem, exception.Message);
    }

    [Fact]
    public void AFootprintTwice_IsRejected()
    {
        ContentValidationException exception = Assert.Throws<ContentValidationException>(
            () => Load(Items(",\"footprint\":[1,1],\"footprint\":[2,2]")));
        Assert.Contains("duplicate property", exception.Message);
    }

    [Fact]
    public void TheDigest_ChangesWithTheFootprint_AndNotWithWhitespace()
    {
        string none = Load(Items()).ContentDigest;
        string oneByOne = Load(Items(",\"footprint\":[1,1]")).ContentDigest;
        string twoByThree = Load(Items(",\"footprint\":[2,3]")).ContentDigest;
        string threeByTwo = Load(Items(",\"footprint\":[3,2]")).ContentDigest;
        string spaced = Load(Items(",  \"footprint\" :  [ 2 ,\n 3 ]")).ContentDigest;

        Assert.Equal(none, oneByOne);
        Assert.NotEqual(none, twoByThree);
        Assert.NotEqual(twoByThree, threeByTwo);
        Assert.Equal(twoByThree, spaced);
    }

    [Theory]
    [InlineData("Hands", EquipmentSlot.Hands)]
    [InlineData("Feet", EquipmentSlot.Feet)]
    [InlineData("Legs", EquipmentSlot.Legs)]
    [InlineData("Neck", EquipmentSlot.Neck)]
    [InlineData("RingLeft", EquipmentSlot.RingLeft)]
    [InlineData("RingRight", EquipmentSlot.RingRight)]
    [InlineData("Back", EquipmentSlot.Back)]
    [InlineData("Companion", EquipmentSlot.Companion)]
    [InlineData("MainHand", EquipmentSlot.MainHand)]
    [InlineData("OffHand", EquipmentSlot.OffHand)]
    [InlineData("Head", EquipmentSlot.Head)]
    [InlineData("Body", EquipmentSlot.Body)]
    public void EachSlotName_Parses_ToItsSlot(string name, EquipmentSlot expected)
    {
        ItemDefinition definition = Only(Load(Items(slots: $"\"{name}\"")));
        Assert.Equal([expected], definition.EquipSlots);
        Assert.Equal(EquipmentSlots.MaskOf(expected), definition.EquipSlotMask);
    }

    [Fact]
    public void ARing_MayListBothRingSlots_InFileOrder()
    {
        ItemDefinition definition = Only(Load(Items(slots: "\"RingLeft\",\"RingRight\"")));
        Assert.Equal([EquipmentSlot.RingLeft, EquipmentSlot.RingRight], definition.EquipSlots);
        Assert.Equal((1 << 8) | (1 << 9), definition.EquipSlotMask);
    }

    [Theory]
    [InlineData("\"Ring\"")]
    [InlineData("\"hands\"")]
    [InlineData("\"Pet\"")]
    [InlineData("\"Hands\",\"Hands\"")]
    public void AnUnknownOrRepeatedSlotName_IsStillRejected(string slots)
    {
        Assert.Throws<ContentValidationException>(() => Load(Items(slots: slots)));
    }

    [Fact]
    public void TheShippedCatalog_StillLoads_WithEveryFootprintInRange()
    {
        foreach (ItemDefinition definition in ContentCatalog.Shipped.Items)
        {
            Assert.InRange(definition.Footprint.Cols, 1, BagGrid.MaxFootprint);
            Assert.InRange(definition.Footprint.Rows, 1, BagGrid.MaxFootprint);
        }
    }
}
