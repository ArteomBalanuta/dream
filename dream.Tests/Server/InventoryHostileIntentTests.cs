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
// Classic HUD iteration 2: the same holds for the footprint bag's own hostile input - an overlap, a
// position off the board, huge integers, a "from" that is not the anchor of an item (an empty cell, a cell
// an item only covers, a held-back row, a worn slot) and a "to" that is a worn slot. A new player's kit:
// the hammer in the main hand, the blade at anchor 2, the maul at 4, the lantern at 6 and the torch at 7.
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

    // Slot values that are never a bag cell: below zero, past the board, in the gap before the worn
    // rows, past the worn rows, the held-back rows and the extremes of an int.
    private static readonly int[] NeverABagCell = [-1, 64, 99, 112, 199, 200, 215, 216, int.MaxValue, int.MinValue];

    // Values that are never a worn slot: the same, without the held-back rows being special.
    private static readonly int[] NeverAWornSlot = [-1, 0, 63, 64, 99, 112, 199, 200, 215, int.MaxValue, int.MinValue];

    private static string Layout(PlayerInventorySnapshot snapshot)
        => string.Join(
            ";",
            snapshot.Slots.Select(slot => $"{slot.SlotIndex}={slot.Item?.ItemInstanceId}")
                .Append($"worn100={snapshot.EquippedSlot?.Item?.ItemInstanceId}")
                .Concat((snapshot.Equipment ?? []).Select(slot => $"worn{slot.SlotIndex}={slot.Item?.ItemInstanceId}")));

    [Fact]
    public void EveryHostileIntent_IsRejected_WithNoRowOrWriteChange()
    {
        EquipmentTestWorld world = new();
        using WorldSessionRuntime runtime = CreateRuntime(world);
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("a", Alice);
        host.JoinSession("b", Bob);
        long bobsBlade = world.SlotsOf(Bob)[2];
        long aliceBlade = world.SlotsOf(Alice)[2];
        long aliceHammer = world.SlotsOf(Alice)[100];
        Dictionary<int, long> aliceBefore = world.SlotsOf(Alice);
        Dictionary<int, long> bobBefore = world.SlotsOf(Bob);
        string aliceLayout = Layout(runtime.TryCaptureInventorySnapshot(new PlayerSessionId("a"))!.Value);
        int writesBefore = world.SlotWrites;
        long itemsBefore = world.LoadItemInstances().Count;

        List<IPlayerActionPayload> hostile = [];
        foreach (int bad in NeverABagCell)
        {
            hostile.Add(new InventoryEquipIntent(bad));
            hostile.Add(new InventoryEquipIntent(bad, 101));
            hostile.Add(new InventoryMoveIntent(bad, 3));
            hostile.Add(new InventoryMoveIntent(2, bad));
            hostile.Add(new InventoryMoveIntent(bad, bad));
            hostile.Add(new InventoryDestroyIntent(bad, aliceBlade));
        }

        foreach (int bad in NeverAWornSlot)
        {
            hostile.Add(new InventoryEquipIntent(2, bad));
            hostile.Add(new InventoryUnequipIntent(bad));
            hostile.Add(new InventoryUnequipIntent(bad, 40));
        }

        // A target cell that is not a bag cell, for a real worn item.
        foreach (int badCell in new[] { -1, 64, 100, 216, int.MaxValue, int.MinValue })
        {
            hostile.Add(new InventoryUnequipIntent(100, badCell));
        }

        hostile.Add(new InventoryEquipIntent(12));                         // a cell the maul covers, not its anchor
        hostile.Add(new InventoryEquipIntent(4, 101));                     // maul in the off hand
        hostile.Add(new InventoryEquipIntent(2, 102));                     // blade on the head
        hostile.Add(new InventoryEquipIntent(100));                        // a worn item with no target slot
        hostile.Add(new InventoryEquipIntent(100, 102));                   // the hammer on the head
        hostile.Add(new InventoryEquipIntent(111, 100));                   // an empty worn slot as the source
        hostile.Add(new InventoryUnequipIntent(103));                      // nothing there
        hostile.Add(new InventoryMoveIntent(12, 13));                      // empty source
        hostile.Add(new InventoryDestroyIntent(12, 1));                    // empty slot
        hostile.Add(new InventoryDestroyIntent(2, bobsBlade));             // another player's item id
        hostile.Add(new InventoryDestroyIntent(2, aliceBlade + 1000));     // a stale id
        hostile.Add(new InventoryDestroyIntent(0, aliceHammer));           // the worn item, by a bag cell it is not in
        hostile.Add(new InventoryDestroyIntent(100, aliceHammer));         // the worn item, by its worn slot

        // The footprint rules: an overlap of two items, a position off the board, a "from" that is not an
        // anchor, a held-back row, a worn slot as the source or as the target.
        hostile.Add(new InventoryMoveIntent(2, 5));                        // the blade over the maul and the lantern
        hostile.Add(new InventoryMoveIntent(4, 6));                        // the maul over the lantern and the torch
        hostile.Add(new InventoryMoveIntent(2, 7));                        // a 2-wide blade at the last column
        hostile.Add(new InventoryMoveIntent(2, 62));                       // a 3-tall blade at the last-but-one row
        hostile.Add(new InventoryMoveIntent(2, 63));
        hostile.Add(new InventoryMoveIntent(10, 40));                      // a cell the blade covers, not its anchor
        hostile.Add(new InventoryMoveIntent(3, 40));
        hostile.Add(new InventoryMoveIntent(200, 40));                     // a held-back row
        hostile.Add(new InventoryMoveIntent(215, 40));
        hostile.Add(new InventoryMoveIntent(100, 40));                     // a worn slot as the source
        hostile.Add(new InventoryMoveIntent(111, 40));
        hostile.Add(new InventoryMoveIntent(2, 100));                      // a worn slot as the target
        hostile.Add(new InventoryMoveIntent(2, 111));
        hostile.Add(new InventoryMoveIntent(2, 200));                      // a held-back row as the target
        hostile.Add(new InventoryUnequipIntent(100, 2));                   // onto the blade
        hostile.Add(new InventoryUnequipIntent(100, 7));                   // onto the torch
        hostile.Add(new InventoryUnequipIntent(100, 63));                  // a 2x3 hammer at the last cell

        ulong sequence = 0;
        foreach (IPlayerActionPayload payload in hostile)
        {
            AuthoritativeActionResult result = Act(runtime, "a", ref sequence, payload);
            Assert.False(result.Accepted, $"{payload} was accepted");
            Assert.NotNull(result.Rejection);
        }

        output.WriteLine($"{hostile.Count} hostile intents rejected");
        Assert.True(hostile.Count > InventoryActionBudget.Burst * 4);
        Assert.Equal(aliceBefore, world.SlotsOf(Alice));
        Assert.Equal(bobBefore, world.SlotsOf(Bob));
        Assert.Equal(aliceLayout, Layout(runtime.TryCaptureInventorySnapshot(new PlayerSessionId("a"))!.Value));
        Assert.Equal(writesBefore, world.SlotWrites);
        Assert.Equal(itemsBefore, world.LoadItemInstances().Count);
        // Rejections spent none of the budget: a legitimate action still goes through.
        Assert.True(Act(runtime, "a", ref sequence, new InventoryMoveIntent(2, 24)).Accepted);
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
        Assert.True(Act(runtime, "a", ref sequence, new InventoryMoveIntent(2, 24)).Accepted);
        Assert.True(Act(runtime, "a", ref sequence, new InventoryUnequipIntent(100)).Accepted);

        Assert.Equal(bobBefore, world.SlotsOf(Bob));
        Assert.NotEqual(bobBefore, world.SlotsOf(Alice));
        // An unknown session is rejected outright.
        Assert.False(Act(runtime, "nobody", ref sequence, new InventoryMoveIntent(2, 24)).Accepted);
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
            IPlayerActionPayload move = accepted % 2 == 0 ? new InventoryMoveIntent(2, 26) : new InventoryMoveIntent(26, 2);
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
            IPlayerActionPayload move = aliceAccepted % 2 == 0 ? new InventoryMoveIntent(2, 26) : new InventoryMoveIntent(26, 2);
            aliceAccepted += Act(runtime, "a", ref sequence, move).Accepted ? 1 : 0;
        }

        Assert.Equal(InventoryActionBudget.Burst, aliceAccepted);

        // Interleaved, Bob gets his whole burst and every one of Alice's is still refused.
        int bobAccepted = 0;
        for (int i = 0; i < InventoryActionBudget.Burst; i++)
        {
            Assert.False(Act(runtime, "a", ref sequence, new InventoryMoveIntent(2, 40)).Accepted);
            IPlayerActionPayload move = i % 2 == 0 ? new InventoryMoveIntent(2, 26) : new InventoryMoveIntent(26, 2);
            bobAccepted += Act(runtime, "b", ref sequence, move).Accepted ? 1 : 0;
        }

        Assert.Equal(InventoryActionBudget.Burst, bobAccepted);
        Assert.False(Act(runtime, "b", ref sequence, new InventoryMoveIntent(2, 40)).Accepted);
    }

    [Fact]
    public async Task AFloodOfHostileAndLegitimateIntents_OverARealConnection_LeavesTheClientConnectedAndTheInventoryIntact()
    {
        await using Wave12DeltaActorLoopbackTests.Rig rig = await Wave12DeltaActorLoopbackTests.Rig.StartAsync(output);
        Wave12DeltaActorLoopbackTests.Peer peer = await rig.JoinAsync("h1-flood");
        PlayerSessionId session = new(peer.SessionId);
        int before = rig.Runtime.TryCaptureInventorySnapshot(session)!.Value.AllItems().Count();

        ulong sequence = 0;
        for (int burst = 0; burst < 20; burst++)
        {
            for (int i = 0; i < PlayerActionQueue.MaxPendingActionsPerSession; i++)
            {
                IPlayerActionPayload payload = (i % 4) switch
                {
                    0 => new InventoryMoveIntent(2, 26),
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
        Assert.Equal(before, after.AllItems().Count());
        Assert.Equal("tool.builder-hammer", after.EquippedSlot!.Value.Item!.Value.ItemKind);
    }
}
