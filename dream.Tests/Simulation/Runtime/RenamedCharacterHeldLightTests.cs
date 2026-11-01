using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Characters;
using Dream.Simulation.Inventory;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Runtime;

// Wave H1, correction 1: the actor's held light is read from the inventory by the character key,
// not by the display name. A renamed character, or a migrated "bob~2", has a key that differs from
// the name shown, and used to cast no light for other clients.
public sealed class RenamedCharacterHeldLightTests
{
    [Theory]
    [InlineData("bob~2", "bob")]
    [InlineData("old-key", "Brand New Name")]
    public void HeldLight_IsShownForACharacterWhoseKeyDiffersFromItsDisplayName(string key, string displayName)
    {
        using WorldSessionRuntime runtime = new(12, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        PlayerSessionId session = new("session-renamed");
        ConnectedPlayer player = runtime.AttachRemoteSession(session, "placeholder");
        player.BindCharacter(new CharacterKey(key), displayName);
        Assert.NotEqual(key, player.DisplayName);

        PlayerInventorySnapshot inventory = runtime.TryCaptureInventorySnapshot(session)!.Value;
        Assert.Equal(key, inventory.PlayerName);
        int torchSlot = inventory.Slots.First(slot => slot.Item?.ItemKind == "tool.torch").SlotIndex;
        Assert.True(runtime.ProcessAuthoritativeAction(new PlayerActionMessage(session, 1, new InventoryEquipIntent(torchSlot))).Accepted);

        ActorSnapshot actor = runtime.CaptureActorSnapshots().Single(candidate => candidate.SessionId == session.Value);
        Assert.Equal(displayName, actor.DisplayName);
        Assert.Equal(HeldLightKind.Torch, actor.HeldLight);
    }
}
