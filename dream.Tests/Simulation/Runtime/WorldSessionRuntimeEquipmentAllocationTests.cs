using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Simulation.Runtime;

// Wave H1: 0 B per tick with four rolled items equipped on each of two sessions, and
// CaptureActorSnapshots reads the cached equipment summary, so it allocates no more with the gear
// than without it.
public sealed class WorldSessionRuntimeEquipmentAllocationTests(ITestOutputHelper output)
{
    private static readonly Vector3 PlayerStart = new(40f, 1.5f, 40f);
    private static readonly PlayerSessionId[] Sessions = [new("gear-a"), new("gear-b")];

    private static WorldSessionRuntime CreateSettledRuntime(bool withGear, out ulong tick)
    {
        WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, PlayerStart, new SceneEventHub());
        foreach (PlayerSessionId session in Sessions)
        {
            ConnectedPlayer player = runtime.AttachRemoteSession(session, session.Value);
            if (!withGear)
            {
                continue;
            }

            string name = player.CharacterKey.Value;
            PlayerInventoryService inventory = runtime.InventoryService;
            Assert.True(inventory.Ensure(name));
            string[] kinds = ["gear.iron-blade", "gear.ward-lantern", "gear.leather-cap", "gear.leather-vest"];
            for (int i = 0; i < kinds.Length; i++)
            {
                Assert.Equal(
                    InventoryEquipOutcome.Equipped,
                    inventory.TryGrantRolled(name, kinds[i], ItemRarity.Rare, new ItemRollSeed(3, 4, 5, 6, i)));
                // Granted into the lowest free slots 5..8; each goes to its own default slot.
                Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(name, 5 + i));
            }

            Assert.NotEqual(0UL, inventory.ResolveModifierDigest(name));
            Assert.NotNull(runtime.TryCaptureInventorySnapshot(session));
        }

        tick = 0;
        for (int i = 0; i < 240; i++)
        {
            runtime.AdvanceAuthoritativeTick(
                ++tick,
                SimulationClock.FixedDeltaSeconds,
                default(PlayerCommandFrame),
                PlayerSimulationTuning.AuthoritativeDefault);
            runtime.ExportServerSnapshotEnvelope();
        }

        return runtime;
    }

    [Fact]
    public void AdvanceAuthoritativeTick_WithFourRolledItemsOnTwoSessions_AllocatesNothing()
    {
        Assert.False(
            NetworkDiagnosticsLog.IsEnabled,
            "This test needs DREAM_NET_DIAGNOSTICS unset: the diagnostics collision report allocates.");
        using WorldSessionRuntime runtime = CreateSettledRuntime(withGear: true, out ulong tick);
        Assert.All(
            runtime.CaptureActorSnapshots().Where(actor => actor.Kind == CreatureKind.Player),
            actor => Assert.NotEqual(0UL, actor.ModifierDigest));

        long bytes = AllocationAssert.MeasureBytesPerIteration(
            () => runtime.AdvanceAuthoritativeTick(
                ++tick,
                SimulationClock.FixedDeltaSeconds,
                default(PlayerCommandFrame),
                PlayerSimulationTuning.AuthoritativeDefault),
            iterations: 50);

        output.WriteLine($"AdvanceAuthoritativeTick, 2 sessions with 4 rolled items each: {bytes} B");
        Assert.True(bytes <= 0, $"Allocated {bytes} B per tick; budget is 0 B.");
    }

    [Fact]
    public void CaptureActorSnapshots_WithFourRolledItemsOnTwoSessions_AllocatesNoMoreThanWithoutGear()
    {
        using WorldSessionRuntime geared = CreateSettledRuntime(withGear: true, out _);
        using WorldSessionRuntime bare = CreateSettledRuntime(withGear: false, out _);

        long gearedBytes = AllocationAssert.MeasureBytesPerIteration(() => geared.CaptureActorSnapshots());
        long bareBytes = AllocationAssert.MeasureBytesPerIteration(() => bare.CaptureActorSnapshots());

        output.WriteLine($"CaptureActorSnapshots: {gearedBytes} B with gear, {bareBytes} B without");
        Assert.Equal(bareBytes, gearedBytes);
    }
}
