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

// Tester, wave T2f: HeldLightLoopbackTests equips the first light-casting starter item, which is
// the survey lantern, so the torch itself never crossed the wire there. Here client A equips the
// torch: B's presenter shows Torch; A swaps back to the hammer (which the torch displaced into the bag,
// since a worn item has no bag row): B shows None.
public sealed class HeldLightTorchLoopbackTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Torch_ThenHammer_ReachTheOtherClientsPresenter()
    {
        await using Wave12DeltaActorLoopbackTests.Rig rig = await Wave12DeltaActorLoopbackTests.Rig.StartAsync(output);
        Wave12DeltaActorLoopbackTests.Peer holder = await rig.JoinAsync("t2-torch-a");
        Wave12DeltaActorLoopbackTests.Peer observer = await rig.JoinAsync("t2-torch-b");
        PlayerInventorySnapshot Inventory() => rig.Runtime.TryCaptureInventorySnapshot(new PlayerSessionId(holder.SessionId))!.Value;
        // The kit wears the torch in the off hand, so it goes back to the bag first: what this test
        // watches is the torch crossing the wire when it is put in the main hand.
        Assert.Equal(
            InventoryEquipOutcome.Equipped,
            rig.Runtime.InventoryService.TryUnequip(Inventory().PlayerName, EquipmentSlots.ToSlotIndex(EquipmentSlot.OffHand)));
        int torch = Assert.Single(Inventory().Slots, slot => slot.Item?.ItemKind == "tool.torch").SlotIndex;

        await EquipAndWaitAsync(rig, holder, 1, torch, HeldLightKind.Torch);
        Assert.Equal(HeldLightKind.Torch, RemoteLightOf(observer, holder));

        // The hammer is in the bag now, wherever the torch's equip put it.
        int hammer = Assert.Single(Inventory().Slots, slot => slot.Item?.ItemKind == "tool.builder-hammer").SlotIndex;
        await EquipAndWaitAsync(rig, holder, 2, hammer, HeldLightKind.None);
        Assert.Equal(HeldLightKind.None, RemoteLightOf(observer, holder));
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
