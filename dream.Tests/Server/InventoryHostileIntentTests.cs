using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Integration;
using Dream.Tests.Support;

namespace Dream.Tests.Server;

// Wave H1: every hostile inventory intent is rejected with no state, row or write change, an action
// only ever touches the sender's own inventory, and a flood of moves is held to the action budget.
public sealed class InventoryHostileIntentTests(ITestOutputHelper output)
{
    private const string Alice = "Hostalice";
    private const string Bob = "Hostbob";

    private static WorldSessionRuntime CreateRuntime(EquipmentTestWorld world) => new(
        16,
        new Vector3(5f, 1.5f, 5f),
        new SceneEventHub(),
        playerProfiles: new EquipmentTestProfiles(),
        worldPersistence: world,
        persistenceWrites: PersistenceWriteQueue.Inline());

    private static AuthoritativeActionResult Act(WorldSessionRuntime runtime, string session, ref ulong sequence, IPlayerActionPayload payload)
        => runtime.ProcessAuthoritativeAction(new PlayerActionMessage(new PlayerSessionId(session), ++sequence, payload));

    [Fact]
    public void EveryHostileIntent_IsRejected_WithNoRowOrWriteChange()
    {
        EquipmentTestWorld world = new();
        using WorldSessionRuntime runtime = CreateRuntime(world);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("a", Alice);
        host.JoinSession("b", Bob);
        long bobsBlade = world.SlotsOf(Bob)[1];
        long aliceBlade = world.SlotsOf(Alice)[1];
        Dictionary<int, long> aliceBefore = world.SlotsOf(Alice);
        Dictionary<int, long> bobBefore = world.SlotsOf(Bob);
        int writesBefore = world.SlotWrites;
        long itemsBefore = world.LoadItemInstances().Count;

        List<IPlayerActionPayload> hostile = [];
        foreach (int bad in new[] { -1, 16, 99, 104, int.MaxValue, int.MinValue })
        {
            hostile.Add(new InventoryEquipIntent(bad));
            hostile.Add(new InventoryEquipIntent(1, bad));
            hostile.Add(new InventoryUnequipIntent(bad));
            hostile.Add(new InventoryMoveIntent(bad, 3));
            hostile.Add(new InventoryMoveIntent(3, bad));
            hostile.Add(new InventoryDestroyIntent(bad, aliceBlade));
        }

        hostile.Add(new InventoryEquipIntent(12));                         // empty slot
        hostile.Add(new InventoryEquipIntent(2, 101));                     // maul in the off hand
        hostile.Add(new InventoryEquipIntent(1, 102));                     // blade on the head
        hostile.Add(new InventoryUnequipIntent(103));                      // nothing there
        hostile.Add(new InventoryMoveIntent(12, 13));                      // empty source
        hostile.Add(new InventoryDestroyIntent(12, 1));                    // empty slot
        hostile.Add(new InventoryDestroyIntent(1, bobsBlade));             // another player's item id
        hostile.Add(new InventoryDestroyIntent(1, aliceBlade + 1000));     // a stale id
        hostile.Add(new InventoryDestroyIntent(0, world.SlotsOf(Alice)[100])); // the equipped item

        ulong sequence = 0;
        foreach (IPlayerActionPayload payload in hostile)
        {
            AuthoritativeActionResult result = Act(runtime, "a", ref sequence, payload);
            Assert.False(result.Accepted, $"{payload} was accepted");
            Assert.NotNull(result.Rejection);
        }

        Assert.Equal(aliceBefore, world.SlotsOf(Alice));
        Assert.Equal(bobBefore, world.SlotsOf(Bob));
        Assert.Equal(writesBefore, world.SlotWrites);
        Assert.Equal(itemsBefore, world.LoadItemInstances().Count);
        // Rejections spent none of the budget: a legitimate action still goes through.
        Assert.True(Act(runtime, "a", ref sequence, new InventoryMoveIntent(1, 9)).Accepted);
    }

    [Fact]
    public void AnAction_OnlyEverTouchesTheSendersOwnInventory()
    {
        EquipmentTestWorld world = new();
        using WorldSessionRuntime runtime = CreateRuntime(world);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("a", Alice);
        host.JoinSession("b", Bob);
        Dictionary<int, long> bobBefore = world.SlotsOf(Bob);
        ulong sequence = 0;

        Assert.True(Act(runtime, "a", ref sequence, new InventoryEquipIntent(4)).Accepted);
        Assert.True(Act(runtime, "a", ref sequence, new InventoryMoveIntent(0, 10)).Accepted);
        Assert.True(Act(runtime, "a", ref sequence, new InventoryUnequipIntent(100)).Accepted);

        Assert.Equal(bobBefore, world.SlotsOf(Bob));
        Assert.NotEqual(bobBefore, world.SlotsOf(Alice));
        // An unknown session is rejected outright.
        Assert.False(Act(runtime, "nobody", ref sequence, new InventoryMoveIntent(0, 10)).Accepted);
    }

    [Fact]
    public void AThousandMoves_InOneBurst_AreHeldToTheBudget_AndToAtMost32Writes()
    {
        EquipmentTestWorld world = new();
        using WorldSessionRuntime runtime = CreateRuntime(world);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("a", Alice);
        int writesBefore = world.SlotWrites;
        ulong sequence = 0;

        int accepted = 0;
        for (int i = 0; i < 1000; i++)
        {
            // Alternate between two squares so every move that goes through is a real change.
            IPlayerActionPayload move = accepted % 2 == 0 ? new InventoryMoveIntent(1, 12) : new InventoryMoveIntent(12, 1);
            if (Act(runtime, "a", ref sequence, move).Accepted)
            {
                accepted++;
            }
        }

        output.WriteLine($"accepted {accepted}, writes {world.SlotWrites - writesBefore}");
        Assert.Equal(InventoryActionBudget.Burst, accepted);
        Assert.True(world.SlotWrites - writesBefore <= 32);
    }

    [Fact]
    public void TheBudget_IsSpentForTheSendingSessionOnly_WhenTwoSessionsInterleave()
    {
        EquipmentTestWorld world = new();
        using WorldSessionRuntime runtime = CreateRuntime(world);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("a", Alice);
        host.JoinSession("b", Bob);
        ulong sequence = 0;

        // Alice alone empties her budget; Bob's is untouched.
        int aliceAccepted = 0;
        for (int i = 0; i < 40; i++)
        {
            IPlayerActionPayload move = aliceAccepted % 2 == 0 ? new InventoryMoveIntent(1, 12) : new InventoryMoveIntent(12, 1);
            aliceAccepted += Act(runtime, "a", ref sequence, move).Accepted ? 1 : 0;
        }

        Assert.Equal(InventoryActionBudget.Burst, aliceAccepted);

        // Interleaved, Bob gets his whole burst and every one of Alice's is still refused.
        int bobAccepted = 0;
        for (int i = 0; i < InventoryActionBudget.Burst; i++)
        {
            Assert.False(Act(runtime, "a", ref sequence, new InventoryMoveIntent(1, 13)).Accepted);
            IPlayerActionPayload move = i % 2 == 0 ? new InventoryMoveIntent(1, 12) : new InventoryMoveIntent(12, 1);
            bobAccepted += Act(runtime, "b", ref sequence, move).Accepted ? 1 : 0;
        }

        Assert.Equal(InventoryActionBudget.Burst, bobAccepted);
        Assert.False(Act(runtime, "b", ref sequence, new InventoryMoveIntent(1, 13)).Accepted);
    }

    [Fact]
    public async Task AFloodOfHostileAndLegitimateIntents_OverARealConnection_LeavesTheClientConnectedAndTheInventoryIntact()
    {
        await using Wave12DeltaActorLoopbackTests.Rig rig = await Wave12DeltaActorLoopbackTests.Rig.StartAsync(output);
        Wave12DeltaActorLoopbackTests.Peer peer = await rig.JoinAsync("h1-flood");
        PlayerSessionId session = new(peer.SessionId);
        int before = rig.Runtime.TryCaptureInventorySnapshot(session)!.Value.Slots.Count(slot => slot.Item.HasValue);

        ulong sequence = 0;
        for (int burst = 0; burst < 20; burst++)
        {
            for (int i = 0; i < PlayerActionQueue.MaxPendingActionsPerSession; i++)
            {
                IPlayerActionPayload payload = (i % 4) switch
                {
                    0 => new InventoryMoveIntent(1, 12),
                    1 => new InventoryEquipIntent(int.MaxValue, -5),
                    2 => new InventoryDestroyIntent(1, long.MinValue),
                    _ => (IPlayerActionPayload)new InventoryUnequipIntent(104),
                };
                peer.Session.QueuePlayerAction(new PlayerActionMessage(session, ++sequence, payload));
            }

            await rig.WaitUntilAsync(
                () => rig.Host.TryGetConnectionContext(peer.Connection.ConnectionId, out ServerRoutingContext? context) && context?.LastActionMessage?.ActionSequence >= sequence,
                $"the server to read action {sequence}");
            await rig.StepAsync();
        }

        Assert.False(peer.Connection.IsFaulted);
        Assert.True(rig.Host.IsConnectionJoined(peer.Connection.ConnectionId));
        PlayerInventorySnapshot after = rig.Runtime.TryCaptureInventorySnapshot(session)!.Value;
        Assert.Equal(before, after.Slots.Count(slot => slot.Item.HasValue));
        Assert.Equal("tool.builder-hammer", after.EquippedSlot!.Value.Item!.Value.ItemKind);
    }
}
