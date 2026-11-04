using Xunit;
using Xunit.Abstractions;
using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Inventory;
using Dream.Simulation.World;
using Dream.Tests.Support;

namespace Dream.Tests.Integration;

// T2f, protocol v6, end to end: the real runtime-backed DedicatedServerService and two real
// RemoteClientSessions over the loopback rig. One player equips the light-casting starter item;
// the server derives the actor's held light from the inventory (ResolveEquippedHeldLight), the
// delta frame carries the change (the Equipment group), the other client's ActorStateCache folds it
// in, and its ClientScenePresenter shows it on the RemotePlayerPresentationState. Unequipping is
// covered by equipping a different item.
public sealed class HeldLightLoopbackTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EquippedLight_ReachesTheOtherClientsRemotePlayerPresentation()
    {
        await using Wave12DeltaActorLoopbackTests.Rig rig = await Wave12DeltaActorLoopbackTests.Rig.StartAsync(output);
        Wave12DeltaActorLoopbackTests.Peer holder = await rig.JoinAsync("t2f-holder");
        Wave12DeltaActorLoopbackTests.Peer observer = await rig.JoinAsync("t2f-observer");
        // The kit wears the torch in the off hand, whose light wins over the main hand's
        // (EquipmentSlots.ResolveHeldLight); it goes back to the bag so this test still watches one
        // hand at a time.
        Assert.Equal(
            InventoryEquipOutcome.Equipped,
            rig.Runtime.InventoryService.TryUnequip(
                rig.Runtime.TryCaptureInventorySnapshot(new PlayerSessionId(holder.SessionId))!.Value.PlayerName,
                EquipmentSlots.ToSlotIndex(EquipmentSlot.OffHand)));
        PlayerInventorySnapshot inventory = rig.Runtime.TryCaptureInventorySnapshot(new PlayerSessionId(holder.SessionId))!.Value;

        // The starter item that casts a light (the torch, once T2b adds it), else the lantern; the
        // expectation is whatever the catalog says, so this pins the plumbing either way.
        InventorySlotSnapshot[] items = inventory.Slots.Where(slot => slot.Item is not null).ToArray();
        InventorySlotSnapshot lightSlot = items.FirstOrDefault(slot => ToolCatalog.Resolve(slot.Item!.Value.ItemKind).HeldLight != HeldLightKind.None);
        if (lightSlot.Item is null)
        {
            lightSlot = items.First(slot => slot.Item!.Value.ItemKind == "tool.survey-lantern");
        }

        InventorySlotSnapshot darkSlot = items.First(slot => ToolCatalog.Resolve(slot.Item!.Value.ItemKind).HeldLight == HeldLightKind.None && slot.SlotIndex != lightSlot.SlotIndex);
        HeldLightKind expected = ToolCatalog.Resolve(lightSlot.Item!.Value.ItemKind).HeldLight;
        output.WriteLine($"equipping {lightSlot.Item.Value.ItemKind} (slot {lightSlot.SlotIndex}), expecting {expected}");

        // Equipping puts the item it displaces in the bag, so the anchors move: each step finds the item by id.
        int AnchorOf(long itemInstanceId) =>
            rig.Runtime.TryCaptureInventorySnapshot(new PlayerSessionId(holder.SessionId))!.Value.AnchorOfId(itemInstanceId);

        await EquipAndWaitAsync(rig, holder, 1, lightSlot.SlotIndex, expected);
        Assert.Equal(expected, RemoteLightOf(observer, holder));

        await EquipAndWaitAsync(rig, holder, 2, AnchorOf(darkSlot.Item!.Value.ItemInstanceId), HeldLightKind.None);
        Assert.Equal(HeldLightKind.None, RemoteLightOf(observer, holder));

        await EquipAndWaitAsync(rig, holder, 3, AnchorOf(lightSlot.Item.Value.ItemInstanceId), expected);
        Assert.Equal(expected, RemoteLightOf(observer, holder));
        rig.AssertNoChainBreaksOrWithheldActors();
    }

    private static async Task EquipAndWaitAsync(Wave12DeltaActorLoopbackTests.Rig rig, Wave12DeltaActorLoopbackTests.Peer peer, ulong sequence, int slotIndex, HeldLightKind expected)
    {
        peer.Session.QueuePlayerAction(new PlayerActionMessage(new PlayerSessionId(peer.SessionId), sequence, new InventoryEquipIntent(slotIndex)));
        await rig.WaitUntilAsync(
            () => rig.Host.TryGetConnectionContext(peer.Connection.ConnectionId, out ServerRoutingContext? context) && context?.LastActionMessage?.ActionSequence >= sequence,
            $"the server to read {peer.SessionId}'s action {sequence}");
        for (int i = 0; i < 64; i++)
        {
            await rig.StepAsync();
            await rig.DrainAllAsync();
            if (rig.Actors().Any(actor => actor.SessionId == peer.SessionId && actor.HeldLight == expected)
                && rig.Peers.All(other => other.Received.Count > 0 && other.Received[^1].Shown.Any(actor => actor.SessionId == peer.SessionId && actor.HeldLight == expected)))
            {
                return;
            }
        }

        Assert.Fail($"{peer.SessionId}'s held light {expected} never reached every client");
    }

    // The observer's presenter fed with every snapshot its session delivered, in order.
    private static HeldLightKind RemoteLightOf(Wave12DeltaActorLoopbackTests.Peer observer, Wave12DeltaActorLoopbackTests.Peer holder)
    {
        ClientScenePresenter presenter = new(observer.SessionId);
        ulong lastTick = 0;
        foreach (Wave12DeltaActorLoopbackTests.Snapshot snapshot in observer.Received)
        {
            presenter.ApplySnapshot(new ServerSnapshotEnvelope(
                snapshot.Tick,
                new Dictionary<string, ulong>(),
                snapshot.Shown,
                Dream.Simulation.Players.PlayerInteractionMode.Building,
                null,
                Array.Empty<WorldMutationDelta>(),
                Array.Empty<DamageEventDelta>()));
            lastTick = snapshot.Tick;
        }

        RemotePlayerPresentationState remote = Assert.Single(
            presenter.BuildRemotePlayerStates(lastTick),
            state => state.SessionId == holder.SessionId);
        return remote.HeldLight;
    }
}
