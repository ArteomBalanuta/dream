namespace Dream.Simulation.Players;

// Mana is derived from level only: the content catalog refuses Energy as an item modifier.
public static class PlayerManaRules
{
    public const int RegenIntervalTicks = 32;      // 2 mana a second at 1 per interval (64 ticks/s)

    public static int MaxMana(int level) => 50 + 5 * (Math.Max(1, level) - 1);

    public static int RegenAmount(int level) => 1 + (Math.Max(1, level) - 1) / 10;
}
