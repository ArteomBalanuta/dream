using Dream.Client.Audio;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Dream.Client.Scenes;

// Q drinks the first healing potion in the bag and E the first mana potion (the lowest bag anchor).
// The scene only asks: the server removes the item and applies the effect, or rejects it.
public partial class PlayScene
{
    private bool _wasConsumableQDown;
    private bool _wasConsumableEDown;

    private void HandleConsumableHotkeys(KeyboardState keyboardState, bool allowGameplayInput)
    {
        bool qDown = keyboardState.IsKeyDown(Keys.Q) || CaptureKeyHeld(Keys.Q);
        bool eDown = keyboardState.IsKeyDown(Keys.E) || CaptureKeyHeld(Keys.E);
        bool qPressed = qDown && !_wasConsumableQDown;
        bool ePressed = eDown && !_wasConsumableEDown;
        _wasConsumableQDown = qDown;
        _wasConsumableEDown = eDown;
        if (!allowGameplayInput || !_windowFocused || _settings.InventoryVisible)
        {
            return;
        }

        if (qPressed)
        {
            UseFirstConsumable(ItemUseEffect.RestoreHealth);
        }

        if (ePressed)
        {
            UseFirstConsumable(ItemUseEffect.RestoreMana);
        }
    }

    private void UseFirstConsumable(ItemUseEffect effect)
    {
        if (_inventory is not PlayerInventorySnapshot inventory
            || !_remoteClient.IsConnected
            || !CharacterPanelModel.TryFindConsumable(inventory, effect, out int anchor, out _, out _))
        {
            return;
        }

        _remoteClient.QueuePlayerAction(new PlayerActionMessage(
            new PlayerSessionId(_remoteClient.SessionId),
            _actionSequences.NextActionSequence(),
            new InventoryEquipIntent(anchor)));
        CurrentGameAudio.Instance.Play(SoundId.PotionDrink);
    }
}
