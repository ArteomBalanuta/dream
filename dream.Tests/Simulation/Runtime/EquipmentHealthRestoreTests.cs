using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using Dream.Tests.Support;

namespace Dream.Tests.Simulation.Runtime;

// Wave H1, H-D17: a player's saved health is applied again against the max health with equipment,
// so vitality gear neither heals nor hurts on a rejoin; and toggling vitality gear does not ratchet
// health upwards.
public sealed class EquipmentHealthRestoreTests
{
    private const string Name = "Vesta";
    private const float VestVitality = 10f;

    private static EquipmentTestWorld WorldWithVest(int? savedHealth)
    {
        EquipmentTestWorld world = new();
        world.PutItem(EquipmentTestWorld.MakeItem(
            20,
            Name,
            "gear.leather-vest",
            new ItemRoll(ItemRarity.Rare, [new StatModifier(StatId.Vitality, StatOp.Flat, VestVitality)])));
        world.PutSlot(Name, 0, 20);
        world.PutSlot(Name, 103, 20);
        world.PutGrant(Name, ToolCatalog.CurrentStarterKitVersion);
        if (savedHealth is int health)
        {
            world.SavePlayerWorldState(new PersistedPlayerWorldState(
                Name,
                new Vector3(5f, 1.5f, 5f),
                Vector3.UnitZ,
                health,
                0,
                DateTime.UtcNow));
        }

        return world;
    }

    private static WorldSessionRuntime CreateRuntime(EquipmentTestWorld world) => new(
        16,
        new Vector3(5f, 1.5f, 5f),
        new SceneEventHub(),
        playerProfiles: new EquipmentTestProfiles(),
        worldPersistence: world,
        persistenceWrites: PersistenceWriteQueue.Inline());

    private static (int Current, int Max) Join(EquipmentTestWorld world, out WorldSessionRuntime runtime, out RuntimeAuthoritativeSessionHost host)
    {
        runtime = CreateRuntime(world);
        host = new RuntimeAuthoritativeSessionHost(runtime);
        host.JoinSession("s1", Name);
        return (runtime.Player.Combatant.Health.CurrentHealth, runtime.Player.Combatant.Health.MaxHealth);
    }

    [Fact]
    public void RejoinWithAVest_RestoresTheSavedHealth_AtAnyRatio()
    {
        // The vest's max health, read from a join with no saved row (arrives at full).
        (int fullCurrent, int max) = Join(WorldWithVest(savedHealth: null), out WorldSessionRuntime fullRuntime, out RuntimeAuthoritativeSessionHost fullHost);
        fullHost.Dispose();
        fullRuntime.Dispose();
        Assert.Equal(max, fullCurrent);

        // A player without the vest has a smaller max, so the vest does change the max.
        EquipmentTestWorld bare = new();
        bare.PutGrant(Name, ToolCatalog.CurrentStarterKitVersion);
        (_, int bareMax) = Join(bare, out WorldSessionRuntime bareRuntime, out RuntimeAuthoritativeSessionHost bareHost);
        bareHost.Dispose();
        bareRuntime.Dispose();
        Assert.True(max > bareMax, $"{max} vs {bareMax}");

        foreach (int saved in new[] { max - 30, max / 2, 1, max - 1 })
        {
            (int current, int joinedMax) = Join(WorldWithVest(saved), out WorldSessionRuntime runtime, out RuntimeAuthoritativeSessionHost host);
            host.Dispose();
            runtime.Dispose();
            Assert.Equal(max, joinedMax);
            Assert.Equal(saved, current);
        }

        // Dead stays dead.
        (int dead, _) = Join(WorldWithVest(0), out WorldSessionRuntime deadRuntime, out RuntimeAuthoritativeSessionHost deadHost);
        deadHost.Dispose();
        deadRuntime.Dispose();
        Assert.Equal(0, dead);
    }

    [Fact]
    public void UnequippingTheVest_ScalesHealthByRatio()
    {
        (int _, int max) = Join(WorldWithVest(savedHealth: null), out WorldSessionRuntime probe, out RuntimeAuthoritativeSessionHost probeHost);
        probeHost.Dispose();
        probe.Dispose();

        int saved = max / 2;
        _ = Join(WorldWithVest(saved), out WorldSessionRuntime runtime, out RuntimeAuthoritativeSessionHost host);
        using (runtime)
        using (host)
        {
            Assert.True(runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
                new PlayerSessionId("s1"),
                1,
                new InventoryUnequipIntent(103))).Accepted);

            int bareMax = runtime.Player.Combatant.Health.MaxHealth;
            Assert.True(bareMax < max);
            Assert.InRange(runtime.Player.Combatant.Health.CurrentHealth, (int)Math.Round(bareMax * (saved / (double)max)) - 1, (int)Math.Round(bareMax * (saved / (double)max)) + 1);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(50)]
    [InlineData(63)]
    [InlineData(99)]
    [InlineData(100)]
    public void TogglingAVest100Times_NeverRaisesHealthByMoreThanTheFirstRound(int startHealth)
    {
        PlayerCombatant combatant = new(() => Vector3.Zero);
        EquipmentLoadout bare = new();
        EquipmentLoadout vest = new();
        vest.Set(EquipmentSlot.Body, [new StatModifier(StatId.Vitality, StatOp.Flat, VestVitality)]);
        int bareMax = combatant.Health.MaxHealth;
        combatant.Health.SetAuthoritativeState(Math.Min(startHealth, bareMax), bareMax);
        int start = combatant.Health.CurrentHealth;

        combatant.ApplyEquipment(vest);
        combatant.ApplyEquipment(bare);
        int afterFirstRound = combatant.Health.CurrentHealth;
        int ceiling = Math.Max(afterFirstRound, start);
        for (int round = 0; round < 99; round++)
        {
            combatant.ApplyEquipment(vest);
            combatant.ApplyEquipment(bare);
            Assert.True(
                combatant.Health.CurrentHealth <= ceiling,
                $"round {round + 2}: {combatant.Health.CurrentHealth} > {ceiling} (start {start}, first round {afterFirstRound})");
        }
    }
}
