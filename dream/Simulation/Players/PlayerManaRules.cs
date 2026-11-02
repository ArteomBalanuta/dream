namespace Dream.Simulation.Players;

// The mana pool: 50 at level 1, 5 more per level after it and 5 per point of effective Energy
// (base plus items).
public static class PlayerManaRules
{
    public const int RegenIntervalTicks = 32;      // 2 mana a second at 1 per interval (64 ticks/s)

    public const int ManaPerEnergy = 5;

    public static int MaxMana(int level, int energy = 0) => 50 + 5 * (Math.Max(1, level) - 1) + ManaPerEnergy * Math.Max(0, energy);

    public static int RegenAmount(int level) => 1 + (Math.Max(1, level) - 1) / 10;
}
