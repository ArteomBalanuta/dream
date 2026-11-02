using Dream.Simulation.Inventory;
using Dream.Simulation.Runtime;
using OpenTK.Mathematics;

namespace Dream.Simulation.Multiplayer;

// The numbers and the offer check of player trade (M5).
public static class TradeRules
{
    // Horizontal metres (XZ) between the two players.
    public const float Range = 6f;

    public const int MaxItems = 8;

    // A trade that has not finished 120 s after it was requested is cancelled.
    public const int TimeoutTicks = 120 * SimulationClock.AuthoritativeTicksPerSecond;

    public static bool InRange(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz) <= Range * Range;
    }

    // The shape of an offer, before anything is looked up: not null, at most MaxItems ids, all positive and
    // distinct, a gold amount from 0 to MaxGold. The count is tested before the list is walked.
    public static bool IsWellFormedOffer(IReadOnlyList<long>? itemIds, long gold)
    {
        if (itemIds == null || itemIds.Count > MaxItems || gold < 0 || gold > InventoryConstants.MaxGold)
        {
            return false;
        }

        for (int i = 0; i < itemIds.Count; i++)
        {
            if (itemIds[i] <= 0)
            {
                return false;
            }

            for (int j = 0; j < i; j++)
            {
                if (itemIds[j] == itemIds[i])
                {
                    return false;
                }
            }
        }

        return true;
    }
}
