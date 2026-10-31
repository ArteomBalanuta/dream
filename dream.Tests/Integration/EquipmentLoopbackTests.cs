using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Inventory;
using Dream.Simulation.World;

namespace Dream.Tests.Integration;

// Wave H1 over the loopback rig: a blade in the main hand and a torch in the off hand, both sent as
// client intents. The server resolves the slots, the held light reaches the other client's snapshots,
// and the unequip intent empties the hand again.
public sealed class EquipmentLoopbackTests(ITestOutputHelper output)
{
    [Fact]
    public async Task BladeAndTorch_AreHeldTogether_AndTheTorchLightReachesTheOtherClient()
    {
        await using Wave12DeltaActorLoopbackTests.Rig rig = await Wave12DeltaActorLoopbackTests.Rig.StartAsync(output);
        Wave12DeltaActorLoopbackTests.Peer holder = await rig.JoinAsync("h1-holder");
        Wave12DeltaActorLoopbackTests.Peer observer = await rig.JoinAsync("h1-observer");
        PlayerSessionId holderSession = new(holder.SessionId);
        PlayerInventorySnapshot inventory = rig.Runtime.TryCaptureInventorySnapshot(holderSession)!.Value;
        int bladeSlot = inventory.Slots.First(slot => slot.Item?.ItemKind == "tool.field-blade").SlotIndex;
        int torchSlot = inventory.Slots.First(slot => slot.Item?.ItemKind == "tool.torch").SlotIndex;

        await SendAndWaitAsync(rig, holder, 1, new InventoryEquipIntent(bladeSlot));
        await SendAndWaitAsync(rig, holder, 2, new InventoryEquipIntent(torchSlot, 101));
        await WaitForLightAsync(rig, holder, HeldLightKind.Torch);

        PlayerInventorySnapshot held = rig.Runtime.TryCaptureInventorySnapshot(holderSession)!.Value;
        Assert.Equal("tool.field-blade", held.EquippedSlot!.Value.Item!.Value.ItemKind);
        Assert.Equal("tool.torch", Assert.Single(held.Equipment!).Item!.Value.ItemKind);
        Assert.Equal(
            HeldLightKind.Torch,
            rig.Peers.Single(peer => peer == observer).Received[^1].Shown.First(actor => actor.SessionId == holder.SessionId).HeldLight);

        await SendAndWaitAsync(rig, holder, 3, new InventoryUnequipIntent(101));
        await WaitForLightAsync(rig, holder, HeldLightKind.None);
        Assert.Null(rig.Runtime.TryCaptureInventorySnapshot(holderSession)!.Value.Equipment);
        rig.AssertNoChainBreaksOrWithheldActors();
    }

    internal static async Task SendAndWaitAsync(
        Wave12DeltaActorLoopbackTests.Rig rig,
        Wave12DeltaActorLoopbackTests.Peer peer,
        ulong sequence,
        IPlayerActionPayload payload)
    {
        peer.Session.QueuePlayerAction(new PlayerActionMessage(new PlayerSessionId(peer.SessionId), sequence, payload));
        await rig.WaitUntilAsync(
            () => rig.Host.TryGetConnectionContext(peer.Connection.ConnectionId, out ServerRoutingContext? context) && context?.LastActionMessage?.ActionSequence >= sequence,
            $"the server to read {peer.SessionId}'s action {sequence}");
        await rig.StepAsync();
    }

    private static async Task WaitForLightAsync(
        Wave12DeltaActorLoopbackTests.Rig rig,
        Wave12DeltaActorLoopbackTests.Peer holder,
        HeldLightKind expected)
    {
        for (int i = 0; i < 64; i++)
        {
            await rig.StepAsync();
            await rig.DrainAllAsync();
            if (rig.Actors().Any(actor => actor.SessionId == holder.SessionId && actor.HeldLight == expected)
                && rig.Peers.All(other => other.Received.Count > 0
                    && other.Received[^1].Shown.Any(actor => actor.SessionId == holder.SessionId && actor.HeldLight == expected)))
            {
                return;
            }
        }

        Assert.Fail($"{holder.SessionId}'s held light {expected} never reached every client");
    }
}
