namespace Dream.Simulation.Combat;

public readonly record struct CombatTargetPolicy(bool PlayerVsPlayerEnabled)
{
    public static CombatTargetPolicy DefaultPve => new(false);
}
