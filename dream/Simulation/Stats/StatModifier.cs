namespace Dream.Simulation.Stats;

public readonly record struct StatModifier(StatId Stat, StatOp Op, float Value);
