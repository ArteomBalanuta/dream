using Dream.Rendering.Hud;
using Dream.Simulation.Players;
using Xunit;

namespace Dream.Tests.Rendering.Hud;

public sealed class HudStatusSnapshotTests
{
    [Fact]
    public void ProgressionLabels_FormatLevelPercentAndRawExperience()
    {
        HudStatusSnapshot snapshot = new(
            CurrentHealth: 100,
            MaxHealth: 120,
            InteractionMode: PlayerInteractionMode.Combat,
            MinDamage: 25,
            MaxDamage: 50,
            Strength: 10,
            AttackSpeed: 2f,
            Vitality: 10,
            Level: 1,
            Experience: 14,
            ExperienceToNextLevel: 1023);

        Assert.Equal("LVL: 1", snapshot.LevelLabel);
        Assert.Equal("EXPERIENCE: 1%", snapshot.ExperiencePercentLabel);
        Assert.Equal("14/1023", snapshot.ExperienceProgressLabel);
    }
}
