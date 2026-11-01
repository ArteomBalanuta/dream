using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Simulation.Runtime;

// Wave I2 (determinism rule 2): loot uses counter seeds only, (simulation seed, tick, killer session,
// enemy actor id, drop index), so two lockstep runs give the same kinds, rarities and modifiers, and no
// ObjectId (which is not a lockstep input) enters a roll.
public sealed class LootDeterminismTests
{
    private static readonly PlayerSessionId Killer = new("det-killer");

    // Three kills 120 ticks apart, each with two drops, then the whole backpack as text.
    private static string[] Run(ulong simulationSeed)
    {
        using WorldSessionRuntime runtime = new(
            Dream.Simulation.World.WorldDefaults.StartupFloorSize,
            WaveIRuntimeKit.FarStart,
            new SceneEventHub(),
            simulationSeed: simulationSeed);
        runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 2);
        ConnectedPlayer player = runtime.AttachRemoteSession(Killer, Killer.Value);
        Assert.True(runtime.InventoryService.Ensure(player.CharacterKey.Value));
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 3);
        WaveIRuntimeKit.EnterCombat(runtime, Killer);

        for (ulong sequence = 1; sequence <= 3; sequence++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick, 120);
            AuthoritativeActionResult hit = WaveIRuntimeKit.HitWolf(runtime, Killer, sequence);
            Assert.True(hit.Accepted, hit.Rejection?.Reason);
        }

        return
        [
            .. runtime.TryCaptureInventorySnapshot(Killer)!.Value.Slots
                .Where(slot => slot.Item is { IsRolled: true })
                .Select(slot => $"{slot.SlotIndex}:{slot.Item!.Value.ItemKind}:{slot.Item.Value.Rarity}:" +
                    string.Join(',', slot.Item.Value.Modifiers!.Select(m => $"{m.Stat}/{m.Op}/{BitConverter.SingleToInt32Bits(m.Value)}")))
        ];
    }

    [Fact]
    public void TwoLockstepRuns_GiveTheSameKindsRaritiesAndModifiers()
    {
        string[] first = Run(WorldSessionRuntime.DefaultSimulationSeed);
        string[] second = Run(WorldSessionRuntime.DefaultSimulationSeed);

        Assert.Equal(6, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ADifferentSimulationSeed_GivesDifferentLoot()
    {
        Assert.NotEqual(Run(WorldSessionRuntime.DefaultSimulationSeed), Run(0x1234_5678_9ABC_DEF0UL));
    }

    [Fact]
    public void TheDropIndexIsPartOfTheSeed_SoTwoDropsOfADireWolfDiffer()
    {
        Assert.True(ContentCatalog.Shipped.TryGetLootTable("dire-wolf", out LootTable? table));
        Assert.Equal(2, table!.Drops);

        int differing = 0;
        for (ulong tick = 1; tick <= 200; tick++)
        {
            ItemRollSeed zero = new(1, tick, 7, 9, DropIndex: 0);
            ItemRollSeed one = zero with { DropIndex = 1 };
            bool a = LootRoller.TryRoll(table, in zero, out LootDrop dropA);
            bool b = LootRoller.TryRoll(table, in one, out LootDrop dropB);
            if (a != b || dropA != dropB)
            {
                differing++;
            }
        }

        Assert.True(differing > 100, $"drop 0 and drop 1 differed on only {differing} of 200 seeds");
    }
}
