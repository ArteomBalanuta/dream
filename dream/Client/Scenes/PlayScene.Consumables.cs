using Dream.Client.Audio;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Dream.Client.Scenes;

// Q drinks the first healing potion in the bag and R the first mana potion (the lowest bag anchor);
// E is interact/pick up (PlayScene.Economy). Not while chat has the keys or the inventory is open.
// The scene only asks: the server removes the item and applies the effect, or rejects it.
public partial class PlayScene
{
    private bool _wasConsumableQDown;
    private bool _wasConsumableRDown;

    private void HandleConsumableHotkeys(KeyboardState keyboardState, bool allowGameplayInput)
    {
        bool qDown = keyboardState.IsKeyDown(Keys.Q) || CaptureKeyHeld(Keys.Q);
        bool rDown = keyboardState.IsKeyDown(Keys.R) || CaptureKeyHeld(Keys.R);
        bool qPressed = qDown && !_wasConsumableQDown;
        bool rPressed = rDown && !_wasConsumableRDown;
        _wasConsumableQDown = qDown;
        _wasConsumableRDown = rDown;
        if (!allowGameplayInput || _chatInput.IsTyping || (!_windowFocused && _hudCapture is null) || _settings.InventoryVisible)
        {
            return;
        }

        if (qPressed)
        {
            UseFirstConsumable(ConsumablePool.Health);
        }

        if (rPressed)
        {
            UseFirstConsumable(ConsumablePool.Mana);
        }
    }

    private void UseFirstConsumable(ConsumablePool pool)
    {
        if (_inventory is not PlayerInventorySnapshot inventory
            || !_remoteClient.IsConnected
            || !CharacterPanelModel.TryFindConsumable(inventory, pool, out int anchor, out _, out _)
            || !CharacterPanelModel.TryGetBagItemId(inventory, anchor, out long itemInstanceId))
        {
            return;
        }

        SendItemUse(anchor, itemInstanceId);
    }

    // Every item use (Q, R, a quickbar tile, the bag's Use entry) goes out here as one ItemUseIntent:
    // the server checks the item, the cooldown and whether anything would be restored.
    private void SendItemUse(int anchor, long itemInstanceId)
    {
        _remoteClient.QueuePlayerAction(new PlayerActionMessage(
            new PlayerSessionId(_remoteClient.SessionId),
            _actionSequences.NextActionSequence(),
            new ItemUseIntent(anchor, itemInstanceId)));
        CurrentGameAudio.Instance.Play(SoundId.PotionDrink);
    }
}
