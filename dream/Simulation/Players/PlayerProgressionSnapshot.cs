namespace Dream.Simulation.Players;

public readonly record struct PlayerProgressionSnapshot(
    int Level,
    int Experience,
    int ExperienceToNextLevel);
