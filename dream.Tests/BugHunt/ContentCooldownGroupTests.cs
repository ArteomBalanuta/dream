using System.Text.Json.Nodes;
using Dream.Rendering.Hud;
using Dream.Simulation.Content;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-CONTENT-2: use.cooldownGroup was the one authored key read with ReadNonEmptyString - no length
// cap, no control-character check, no trim. It is looked up by value (AbilityCooldowns.IsReady) and
// travels on the wire as AbilityCooldown.Key, and QuickbarHudState drops any cooldown whose key is
// longer than QuickbarHudState.MaxKeyChars, so a 65-character group left the tile showing ready while
// the server refused every click. It is now read with ReadKey, like every other content key.
//
// BH-CONTENT-3: ContentDigest left use.cooldownGroup out, so two catalogs whose consumables share or
// do not share a cooldown pool hashed identically - and the group is simulation-visible, since it
// decides whether a use is accepted. The digest is a lockstep input and the only diagnostic for content
// drift between two hosts, so it now covers the group and the items section is items.v6.
public sealed class ContentCooldownGroupTests
{
    private const string Consumable = "consumable.weak-heal";

    private static ContentCatalog LoadWithGroup(string group) =>
        ContentBugHunt.Load(itemsJson: ContentBugHunt.ItemsWithUseField(Consumable, "cooldownGroup", JsonValue.Create(group)));

    // The authoring cap is the client's key cap, so the two numbers must stay equal.
    [Fact]
    public void TheKeyCap_IsTheQuickbarsKeyCap()
    {
        Assert.Equal(QuickbarHudState.MaxKeyChars, ContentCatalog.MaxKeyLength);
    }

    // Exactly at the limit: accepted, and the quickbar keeps a cooldown under that key.
    [Fact]
    public void ACooldownGroupAtTheKeyLimit_LoadsAndTheQuickbarKeepsIt()
    {
        string group = new('g', ContentCatalog.MaxKeyLength);

        ContentCatalog catalog = LoadWithGroup(group);

        Assert.True(catalog.TryGetItem(Consumable, out ItemDefinition? item));
        Assert.Equal(group, item!.Use!.CooldownGroup);
        Assert.Equal(group, item.Use.EffectiveCooldownGroup);
        Assert.True(item.Use.EffectiveCooldownGroup.Length <= QuickbarHudState.MaxKeyChars);
    }

    // One character past the limit: refused at load instead of silently losing the client's cooldown.
    [Fact]
    public void ACooldownGroupPastTheKeyLimit_IsRefusedAtLoad()
    {
        ContentValidationException failure = Assert.Throws<ContentValidationException>(
            () => LoadWithGroup(new string('g', ContentCatalog.MaxKeyLength + 1)));

        Assert.Contains(ContentCatalog.ItemsFileName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("cooldownGroup", failure.Message, StringComparison.Ordinal);
        Assert.Contains("at most", failure.Message, StringComparison.Ordinal);
    }

    // The other two key rules come with ReadKey: a control character would have reached the wire, and a
    // padded value would have been a group no lookup folds back together.
    [Theory]
    [InlineData("potion\theal", "control characters")]
    [InlineData(" potion.heal", "whitespace")]
    [InlineData("potion.heal ", "whitespace")]
    public void ACooldownGroupThatIsNotAKey_IsRefusedAtLoad(string group, string reason)
    {
        ContentValidationException failure = Assert.Throws<ContentValidationException>(() => LoadWithGroup(group));

        Assert.Contains("cooldownGroup", failure.Message, StringComparison.Ordinal);
        Assert.Contains(reason, failure.Message, StringComparison.Ordinal);
    }

    // Control: an ordinary group still loads, still reaches the definition, and still gates the use -
    // two items sharing a group share a cooldown, which is the whole point of the field.
    [Fact]
    public void AnOrdinaryCooldownGroup_LoadsAndIsTheGroupTheSimulationUses()
    {
        ContentCatalog catalog = LoadWithGroup("potion.restore");

        Assert.True(catalog.TryGetItem(Consumable, out ItemDefinition? item));
        Assert.Equal("potion.restore", item!.Use!.EffectiveCooldownGroup);
        // An item without an authored group still falls back to its own key, unchanged.
        Assert.True(catalog.TryGetItem("potion.health", out ItemDefinition? plain));
        Assert.Null(plain!.Use!.CooldownGroup);
        Assert.Equal("potion.health", plain.Use.EffectiveCooldownGroup);
    }

    // BH-CONTENT-3: two catalogs differing only in the cooldown pool must not hash alike.
    [Fact]
    public void TheDigest_ChangesWithACooldownGroup()
    {
        string shipped = ContentBugHunt.Load().ContentDigest;
        Assert.Equal(ContentCatalog.Shipped.ContentDigest, shipped);
        string grouped = LoadWithGroup("potion.restore").ContentDigest;
        string otherGroup = LoadWithGroup("potion.other").ContentDigest;

        Assert.NotEqual(shipped, grouped);
        Assert.NotEqual(grouped, otherGroup);
    }

    // BH-CONTENT-3 control: the digest is still a pure function of the parsed values - the same content
    // read twice hashes the same, and it is still a 64-hex string.
    [Fact]
    public void TheDigest_IsStillStableForTheSameContent()
    {
        Assert.Equal(LoadWithGroup("potion.restore").ContentDigest, LoadWithGroup("potion.restore").ContentDigest);
        Assert.Matches("^[0-9a-f]{64}$", LoadWithGroup("potion.restore").ContentDigest);
    }
}
