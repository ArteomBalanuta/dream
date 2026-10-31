using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Combat;
using Dream.Simulation.Persistence;
using Dream.Simulation.Determinism;
using Dream.Simulation.Inventory;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using Dream.Tests.Support;

namespace Dream.Tests.Simulation.Runtime;

// Wave H1: ActorSnapshot.ModifierDigest, the digest of the rolled modifiers of what a player has
// equipped. It is zero with no rolled item equipped, never holds an item id, survives a restart, and
// SimulationStateChecksum.HashActor mixes it in only when it is not zero.
public sealed class RolledItemDigestTests
{
    private const string Name = "Digesta";

    private static WorldSessionRuntime CreateRuntime(EquipmentTestWorld world) => new(
        16,
        new Vector3(5f, 1.5f, 5f),
        new SceneEventHub(),
        playerProfiles: new EquipmentTestProfiles(),
        worldPersistence: world,
        persistenceWrites: PersistenceWriteQueue.Inline());

    private static EquipmentTestWorld WorldWithRolledBlade(long itemId, bool equipped)
    {
        EquipmentTestWorld world = new();
        world.PutItem(EquipmentTestWorld.MakeItem(
            itemId,
            Name,
            "gear.iron-blade",
            new ItemRoll(ItemRarity.Rare, [new StatModifier(StatId.Strength, StatOp.Flat, 6), new StatModifier(StatId.Armor, StatOp.Flat, 4)])));
        world.PutSlot(Name, 0, itemId);
        if (equipped)
        {
            world.PutSlot(Name, 100, itemId);
        }

        world.PutGrant(Name, ToolCatalog.CurrentStarterKitVersion);
        return world;
    }

    private static ActorSnapshot PlayerActor(WorldSessionRuntime runtime)
        => runtime.CaptureActorSnapshots().Single(actor => actor.Kind == CreatureKind.Player);

    private static ActorSnapshot JoinedActor(EquipmentTestWorld world, out WorldSessionRuntime runtime)
    {
        runtime = CreateRuntime(world);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("s1", Name);
        return PlayerActor(runtime);
    }

    [Fact]
    public void Digest_IsZeroWithNoRolledItemEquipped_AndWithOneInTheBackpackOnly()
    {
        ActorSnapshot none = JoinedActor(WorldWithRolledBlade(40, equipped: false), out WorldSessionRuntime a);
        using (a)
        {
            Assert.Equal(0UL, none.ModifierDigest);
        }

        EquipmentTestWorld unrolled = new();
        ActorSnapshot starter = JoinedActor(unrolled, out WorldSessionRuntime b);
        using (b)
        {
            Assert.Equal(0UL, starter.ModifierDigest);
        }
    }

    [Fact]
    public void Digest_IsEqualAcrossTwoRuns_AcrossARestart_AndDoesNotDependOnTheItemId()
    {
        ActorSnapshot first = JoinedActor(WorldWithRolledBlade(40, equipped: true), out WorldSessionRuntime a);
        using (a)
        {
            Assert.NotEqual(0UL, first.ModifierDigest);
        }

        ActorSnapshot second = JoinedActor(WorldWithRolledBlade(40, equipped: true), out WorldSessionRuntime b);
        using (b)
        {
            Assert.Equal(first.ModifierDigest, second.ModifierDigest);
            Assert.Equal(SimulationStateChecksum.HashActor(first), SimulationStateChecksum.HashActor(second));
        }

        // A restart: a new runtime on the same rows.
        EquipmentTestWorld shared = WorldWithRolledBlade(40, equipped: true);
        ActorSnapshot before = JoinedActor(shared, out WorldSessionRuntime c);
        c.Dispose();
        ActorSnapshot after = JoinedActor(shared, out WorldSessionRuntime d);
        using (d)
        {
            Assert.Equal(before.ModifierDigest, after.ModifierDigest);
        }

        // Another id for the same roll: same digest.
        ActorSnapshot otherId = JoinedActor(WorldWithRolledBlade(987_654, equipped: true), out WorldSessionRuntime e);
        using (e)
        {
            Assert.Equal(first.ModifierDigest, otherId.ModifierDigest);
        }
    }

    [Fact]
    public void HashActor_MixesTheDigestInOnlyWhenItIsNotZero()
    {
        ActorSnapshot actor = new(
            7,
            CreatureKind.Player,
            "s1",
            new Vector3(1f, 2f, 3f),
            Vector3.UnitZ,
            90,
            100,
            true);

        ulong plain = SimulationStateChecksum.HashActor(actor);
        Assert.Equal(plain, SimulationStateChecksum.HashActor(actor with { ModifierDigest = 0UL }));
        ulong one = SimulationStateChecksum.HashActor(actor with { ModifierDigest = 1UL });
        ulong two = SimulationStateChecksum.HashActor(actor with { ModifierDigest = 2UL });
        Assert.NotEqual(plain, one);
        Assert.NotEqual(one, two);
        // The same actor, ignoring the digest, is hashed as it was before wave H.
        Assert.Equal(plain, SimulationStateChecksum.HashActor(actor with { HeldLight = HeldLightKind.Torch }));
    }

    [Fact]
    public void Digest_ChangesWhenTheRolledItemIsUnequipped()
    {
        EquipmentTestWorld world = WorldWithRolledBlade(40, equipped: true);
        using WorldSessionRuntime runtime = CreateRuntime(world);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("s1", Name);
        Assert.NotEqual(0UL, PlayerActor(runtime).ModifierDigest);

        Assert.True(runtime.ProcessAuthoritativeAction(new(
            new Dream.Multiplayer.Sessions.PlayerSessionId("s1"),
            1,
            new Dream.Multiplayer.Protocol.InventoryUnequipIntent(100))).Accepted);

        Assert.Equal(0UL, PlayerActor(runtime).ModifierDigest);
    }
}
