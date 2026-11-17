using Xunit;
using Dream.Rendering.Hud;
using Dream.Simulation.Players;

namespace Dream.Tests.Simulation.Players;


public class PlayerInteractionModeTests
{
    // The mode crosses the wire as a bare int: ActorSnapshotJsonConverter writes
    // (int)interactionMode and ActorFrameJson writes (int?)record.InteractionMode, and both read it
    // back by casting an int straight to the enum. The numeric values therefore ARE the protocol -
    // reordering or inserting a member silently repoints every connected client's mode - while the
    // old Assert.Equal(Building, Building) compared one compile-time constant to itself and could
    // not fail for any edit that still compiled.
    [Fact]
    public void Building_Mode_Is_Available()
    {
        Assert.Equal(0, (int)PlayerInteractionMode.Building);
        Assert.Equal(1, (int)PlayerInteractionMode.Combat);
        Assert.Equal<int[]>([0, 1], [.. Enum.GetValues<PlayerInteractionMode>().Select(mode => (int)mode)]);
    }

    [Theory]
    [InlineData(PlayerInteractionMode.Building, "Building")]
    [InlineData(PlayerInteractionMode.Combat, "Combat")]
    public void ToHudLabel_Returns_Player_Facing_Name(PlayerInteractionMode mode, string expected)
    {
        Assert.Equal(expected, mode.ToHudLabel());
    }
}

public class HudStatusSnapshotTests
{
    [Fact]
    public void Create_Exposes_Current_Health_And_Mode()
    {
        HudStatusSnapshot snapshot = new(82, 100, PlayerInteractionMode.Combat, 25, 50, 10, 2.0f, 10, 1, 14, 1023);

        Assert.Equal(82, snapshot.CurrentHealth);
        Assert.Equal(100, snapshot.MaxHealth);
        Assert.Equal(PlayerInteractionMode.Combat, snapshot.InteractionMode);
        Assert.Equal("HP: 82/100", snapshot.HealthLabel);
        Assert.Equal("Mode: Combat", snapshot.ModeLabel);
        Assert.Equal("Damage: 25-50", snapshot.DamageLabel);
        Assert.Equal("LVL: 1", snapshot.LevelLabel);
    }
}
