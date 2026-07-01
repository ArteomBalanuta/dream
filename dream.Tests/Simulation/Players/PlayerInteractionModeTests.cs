using Xunit;
using Dream.Rendering.Hud;
using Dream.Simulation.Players;

namespace Dream.Tests.Simulation.Players;


public class PlayerInteractionModeTests
{
    [Fact]
    public void Building_Mode_Is_Available()
    {
        Assert.Equal(PlayerInteractionMode.Building, PlayerInteractionMode.Building);
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
        HudStatusSnapshot snapshot = new(82, 100, PlayerInteractionMode.Combat, 25, 50, 10, 2.0f, 10);

        Assert.Equal(82, snapshot.CurrentHealth);
        Assert.Equal(100, snapshot.MaxHealth);
        Assert.Equal(PlayerInteractionMode.Combat, snapshot.InteractionMode);
        Assert.Equal("HP: 82/100", snapshot.HealthLabel);
        Assert.Equal("Mode: Combat", snapshot.ModeLabel);
        Assert.Equal("Damage: 25-50", snapshot.DamageLabel);
    }
}
