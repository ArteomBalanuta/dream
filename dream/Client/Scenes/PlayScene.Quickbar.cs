using Dream.Client.Input;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Dream.Client.Scenes;

// M4 on the client: keys 1..6 act on the quickbar's six tiles, one intent per press. The server decides
// everything (mana, cooldown, mode, range); this side sends no gate of its own and shows what it was
// told. The state is built by its parameterless constructor in the headless play scene.
public partial class PlayScene
{
    private readonly QuickbarHudState _quickbarHud = new();

    // The 1..6 keys that were down at the last look (bit n = slot n).
    private int _quickbarKeysDown;

    // Once a frame, outside chat: ends a dead session's bar, shares the state with the HUD pass and fires
    // the digit keys that went down. A clicked tile arrives through the HUD request instead.
    private void HandleQuickbarKeys(KeyboardState keyboardState)
    {
        if (_render is not null)
        {
            // The HUD pass reads it; the headless play scene has no renderer.
            _render.Quickbar = _quickbarHud;
        }

        _quickbarHud.NowTick = EstimatedServerTick;
        int down = QuickbarInput.ReadMask(keyboardState);
        int presses = QuickbarInput.NewPresses(_quickbarKeysDown, down);
        _quickbarKeysDown = down;

        if (!_remoteClient.IsConnected)
        {
            // The session is over: no mana, cooldowns or binds belong to it any more.
            if (_quickbarHud.HasData)
            {
                _quickbarHud.Clear();
            }

            return;
        }

        if (presses == 0 || !_settings.CurrentRuntimeState.AllowGameplayInput)
        {
            return;
        }

        for (int slot = 0; slot < InventoryConstants.QuickbarSlots; slot++)
        {
            if ((presses & (1 << slot)) != 0)
            {
                PressQuickbarSlot(slot);
            }
        }
    }

    // The key of one slot: a skill is cast along the aim, an item of the bound kind is used from the bag.
    // False when nothing was sent (dead, empty slot, no such item in the bag).
    internal bool PressQuickbarSlot(int slot)
    {
        if (!_remoteClient.IsConnected || _deathTracker.IsDead || (uint)slot >= InventoryConstants.QuickbarSlots)
        {
            return false;
        }

        QuickbarSlotSnapshot entry = _quickbarHud.Slot(slot);
        if (entry.Kind == QuickbarEntryKind.Skill)
        {
            SendEconomyIntent(new SkillUseIntent(entry.Key, ResolveSkillAim()));
            return true;
        }

        if (entry.Kind == QuickbarEntryKind.Item && _quickbarHud.TryGetItemTarget(slot, out int bagSlot, out long itemId))
        {
            SendEconomyIntent(new ItemUseIntent(bagSlot, itemId));
            return true;
        }

        return false;
    }

    // The direction a skill is aimed in. Null is the server's own view ray (where the player faces). This
    // is the one hook for aiming from the cursor: it only has to return the eye-to-cursor direction here.
    private Vector3? ResolveSkillAim() => null;

    // A tile clicked with the inventory open moves to the next entry in the cycle (see
    // QuickbarHudState.NextEntry). One bind intent; the server validates it and answers with the
    // inventory, which carries the new bar.
    internal bool ApplyQuickbarCycle(int slot)
    {
        if (!_remoteClient.IsConnected || (uint)slot >= InventoryConstants.QuickbarSlots)
        {
            return false;
        }

        QuickbarSlotSnapshot next = _quickbarHud.NextEntry(slot);
        SendEconomyIntent(new QuickbarBindIntent(slot, next.Kind, next.Key));
        return true;
    }
}
