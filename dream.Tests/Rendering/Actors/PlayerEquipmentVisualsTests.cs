using Xunit;
using Dream.Rendering.Actors;

namespace Dream.Tests.Rendering.Actors;

public class PlayerEquipmentVisualsTests
{
    [Fact]
    public void Parse_ReadsMainAndOffHand()
    {
        PlayerEquipmentVisuals visuals = PlayerEquipmentVisuals.Parse("held.weapon.sword|held.shield.kite");

        Assert.Equal("held.weapon.sword", visuals.MainHand);
        Assert.Equal("held.shield.kite", visuals.OffHand);
    }

    [Fact]
    public void Parse_EmptySideIsAnEmptyHand()
    {
        PlayerEquipmentVisuals mainOnly = PlayerEquipmentVisuals.Parse("held.weapon.bow|");
        PlayerEquipmentVisuals offOnly = PlayerEquipmentVisuals.Parse("|held.shield.tower");

        Assert.Equal("held.weapon.bow", mainOnly.MainHand);
        Assert.Null(mainOnly.OffHand);
        Assert.Null(offOnly.MainHand);
        Assert.Equal("held.shield.tower", offOnly.OffHand);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("|")]
    [InlineData("a|b|c")]
    [InlineData("HELD.WEAPON.SWORD|")]
    [InlineData("held.weapon.sw ord|")]
    [InlineData("held.weapon.sword\n|")]
    [InlineData("held.none|held.none")]
    [InlineData("worn.body.plate|")]
    public void Parse_MalformedOrNonHeldKeysShowNothing(string? key)
    {
        PlayerEquipmentVisuals visuals = PlayerEquipmentVisuals.Parse(key);

        Assert.True(visuals.IsEmpty);
    }

    // Contract 1: Parse accepts 1..9 fields, so a lone field is the main hand (the v19 parser read it as
    // malformed; no v20 sender emits it, as Format always keeps the first two fields).
    [Fact]
    public void Parse_ALoneFieldIsTheMainHand()
    {
        PlayerEquipmentVisuals visuals = PlayerEquipmentVisuals.Parse("held.weapon.sword");

        Assert.Equal(new PlayerEquipmentVisuals("held.weapon.sword"), visuals);
        Assert.False(visuals.HasWorn);
        Assert.True(visuals.HasHeld);
    }

    [Fact]
    public void Normalize_BySlot_RequiresTheSlotsPrefix()
    {
        Assert.Equal("worn.head.leather-cap", PlayerEquipmentVisuals.Normalize(Dream.Simulation.Inventory.EquipmentSlot.Head, "worn.head.leather-cap"));
        Assert.Null(PlayerEquipmentVisuals.Normalize(Dream.Simulation.Inventory.EquipmentSlot.Head, "worn.feet.iron-boots"));
        Assert.Null(PlayerEquipmentVisuals.Normalize(Dream.Simulation.Inventory.EquipmentSlot.MainHand, "held.none"));
        Assert.Null(PlayerEquipmentVisuals.Normalize(Dream.Simulation.Inventory.EquipmentSlot.Neck, "worn.neck.iron-torc"));
        Assert.Null(PlayerEquipmentVisuals.Normalize(Dream.Simulation.Inventory.EquipmentSlot.Back, "worn.back.Bad"));
    }

    [Fact]
    public void Parse_DropsOnlyTheBadSide()
    {
        PlayerEquipmentVisuals visuals = PlayerEquipmentVisuals.Parse("held.weapon.axe|HELD SHIELD");

        Assert.Equal("held.weapon.axe", visuals.MainHand);
        Assert.Null(visuals.OffHand);
    }

    [Fact]
    public void Parse_RejectsOverlongKeysAndIds()
    {
        string longId = "held." + new string('a', PlayerEquipmentVisuals.MaxVisualIdLength);

        Assert.True(PlayerEquipmentVisuals.Parse(longId + "|").IsEmpty);
        Assert.True(PlayerEquipmentVisuals.Parse(new string('a', 500)).IsEmpty);
    }

    [Fact]
    public void Format_RoundTripsThroughParse_AndIsNullWhenBothHandsAreEmpty()
    {
        string? key = PlayerEquipmentVisuals.Format("held.weapon.mace", "held.shield.buckler");

        Assert.Equal("held.weapon.mace|held.shield.buckler", key);
        Assert.Equal(
            new PlayerEquipmentVisuals("held.weapon.mace", "held.shield.buckler"),
            PlayerEquipmentVisuals.Parse(key));
        Assert.Null(PlayerEquipmentVisuals.Format(null, ""));
    }

    [Theory]
    [InlineData("held.none", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("held.tool.torch", "held.tool.torch")]
    public void Normalize_KeepsOnlyHeldVisuals(string? id, string? expected)
    {
        Assert.Equal(expected, PlayerEquipmentVisuals.Normalize(id));
    }
}
