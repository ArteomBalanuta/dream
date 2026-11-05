using Dream.Rendering.Actors;
using Dream.Simulation.Inventory;
using Xunit;

namespace Dream.Tests.Simulation.Inventory;

// The nine-field player VisualKey (protocol v21): "main|off|head|body|hands|feet|legs|back|companion".
public sealed class PlayerWornVisualKeyTests
{
    private const string Full =
        "held.weapon.sword|held.shield.kite|worn.head.iron-helm|worn.body.plate-cuirass|worn.hands.iron-gauntlets"
        + "|worn.feet.iron-boots|worn.legs.plate-greaves|worn.back.grave-mantle|worn.companion.ember-wisp";

    [Fact]
    public void AllNineFields_RoundTrip()
    {
        PlayerEquipmentVisuals visuals = PlayerEquipmentVisuals.Parse(Full);

        Assert.Equal("held.weapon.sword", visuals.MainHand);
        Assert.Equal("held.shield.kite", visuals.OffHand);
        Assert.Equal("worn.head.iron-helm", visuals.Head);
        Assert.Equal("worn.body.plate-cuirass", visuals.Body);
        Assert.Equal("worn.hands.iron-gauntlets", visuals.Hands);
        Assert.Equal("worn.feet.iron-boots", visuals.Feet);
        Assert.Equal("worn.legs.plate-greaves", visuals.Legs);
        Assert.Equal("worn.back.grave-mantle", visuals.Back);
        Assert.Equal("worn.companion.ember-wisp", visuals.Companion);
        Assert.True(visuals.HasHeld);
        Assert.True(visuals.HasWorn);
        Assert.Equal(Full, PlayerEquipmentVisuals.Format(visuals));
        foreach (EquipmentSlot slot in PlayerEquipmentVisuals.KeyFields)
        {
            Assert.StartsWith(PlayerEquipmentVisuals.PrefixFor(slot), visuals.Get(slot));
        }

        Assert.Null(visuals.Get(EquipmentSlot.Neck));
        Assert.Null(visuals.Get(EquipmentSlot.RingLeft));
    }

    [Fact]
    public void Format_TrimsTrailingEmptyFields_ButAlwaysKeepsMainAndOff()
    {
        Assert.Equal("held.weapon.bow|", PlayerEquipmentVisuals.Format(new PlayerEquipmentVisuals("held.weapon.bow")));
        Assert.Equal(
            "held.weapon.bow|held.shield.kite",
            PlayerEquipmentVisuals.Format(new PlayerEquipmentVisuals("held.weapon.bow", "held.shield.kite")));
        Assert.Equal("||worn.head.leather-cap", PlayerEquipmentVisuals.Format(new PlayerEquipmentVisuals(Head: "worn.head.leather-cap")));
        Assert.Equal(
            "|held.tool.torch|||worn.hands.leather-gloves",
            PlayerEquipmentVisuals.Format(new PlayerEquipmentVisuals(OffHand: "held.tool.torch", Hands: "worn.hands.leather-gloves")));
        Assert.Null(PlayerEquipmentVisuals.Format(PlayerEquipmentVisuals.Empty));
    }

    [Fact]
    public void Parse_AcceptsOneToNineFields_AsASupersetOfV19()
    {
        Assert.Equal("held.weapon.sword", PlayerEquipmentVisuals.Parse("held.weapon.sword").MainHand);
        Assert.Equal(
            new PlayerEquipmentVisuals("held.weapon.sword", "held.shield.kite"),
            PlayerEquipmentVisuals.Parse("held.weapon.sword|held.shield.kite"));
        Assert.Equal("worn.legs.plate-greaves", PlayerEquipmentVisuals.Parse("||||||worn.legs.plate-greaves").Legs);
        Assert.Equal("worn.companion.ember-wisp", PlayerEquipmentVisuals.Parse("||||||||worn.companion.ember-wisp").Companion);
    }

    [Fact]
    public void ABootIdInTheHeadField_IsDroppedAlone()
    {
        PlayerEquipmentVisuals visuals = PlayerEquipmentVisuals.Parse(
            "held.weapon.sword||worn.feet.iron-boots|worn.body.plate-cuirass");

        Assert.Null(visuals.Head);
        Assert.Equal("held.weapon.sword", visuals.MainHand);
        Assert.Equal("worn.body.plate-cuirass", visuals.Body);
        Assert.Null(visuals.Feet);
    }

    [Fact]
    public void AWornIdInAHandField_AndAHeldIdInAWornField_AreDropped()
    {
        PlayerEquipmentVisuals visuals = PlayerEquipmentVisuals.Parse("worn.head.leather-cap|held.none|held.weapon.sword");

        Assert.True(visuals.IsEmpty);
    }

    [Fact]
    public void OneBadCharacter_KillsOneFieldOnly()
    {
        PlayerEquipmentVisuals visuals = PlayerEquipmentVisuals.Parse(
            "held.weapon.sword|held.shield.kite|worn.head.IRON-HELM|worn.body.plate-cuirass");

        Assert.Null(visuals.Head);
        Assert.Equal("held.weapon.sword", visuals.MainHand);
        Assert.Equal("held.shield.kite", visuals.OffHand);
        Assert.Equal("worn.body.plate-cuirass", visuals.Body);
    }

    [Fact]
    public void AnOverlongKey_AndTooManySeparators_ReadAsEmpty()
    {
        string tooLong = "held.weapon.sword|" + new string('a', PlayerEquipmentVisuals.MaxKeyLength);
        Assert.True(tooLong.Length > PlayerEquipmentVisuals.MaxKeyLength);
        Assert.True(PlayerEquipmentVisuals.Parse(tooLong).IsEmpty);

        string nineSeparators = "held.weapon.sword" + new string('|', PlayerEquipmentVisuals.FieldCount);
        Assert.True(PlayerEquipmentVisuals.Parse(nineSeparators).IsEmpty);
        Assert.True(PlayerEquipmentVisuals.Parse(Full + "|worn.head.iron-helm").IsEmpty);
    }

    [Fact]
    public void AnOverlongField_IsDroppedAlone()
    {
        string longId = "worn.head." + new string('a', PlayerEquipmentVisuals.MaxVisualIdLength);

        PlayerEquipmentVisuals visuals = PlayerEquipmentVisuals.Parse("held.weapon.sword||" + longId);

        Assert.Equal("held.weapon.sword", visuals.MainHand);
        Assert.Null(visuals.Head);
    }

    [Fact]
    public void TheServerAndClientKeyLimitsAreTheSameNumber()
    {
        Assert.Equal(PlayerEquipmentVisuals.MaxKeyLength, PlayerInventoryService.MaxPlayerVisualKeyLength);
        Assert.Equal(584, PlayerEquipmentVisuals.MaxKeyLength);
    }
}
