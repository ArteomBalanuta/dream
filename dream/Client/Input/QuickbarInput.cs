using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Dream.Client.Input;

// The quickbar's keys (M4): 1..6 press a slot, B and G select the mode. PlayerController still maps
// D1 and D2 to Building and Combat, which the quickbar took; RemapModeKeys replaces that choice after the
// capture so PlayerController itself stays as it was. Pure functions of the keys, so nothing here needs
// a window.
public static class QuickbarInput
{
    private static readonly Keys[] SlotKeys = [Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6];

    // Bit n is set while the key of slot n (the digit n + 1) is down.
    public static int ReadMask(KeyboardState keyboardState)
    {
        int mask = 0;
        for (int slot = 0; slot < SlotKeys.Length; slot++)
        {
            if (keyboardState.IsKeyDown(SlotKeys[slot]))
            {
                mask |= 1 << slot;
            }
        }

        return mask;
    }

    // Keys down now that were not down at the last look. A key held since before (or through a closed
    // chat line) is not a press; holding does not repeat.
    public static int NewPresses(int previousMask, int currentMask) => currentMask & ~previousMask & ((1 << InventoryConstants.QuickbarSlots) - 1);

    // The frame with its mode request taken from B and G instead of D1 and D2. A frame captured with
    // gameplay input off is returned as it is.
    public static PlayerCommandFrame RemapModeKeys(PlayerCommandFrame frame, KeyboardState keyboardState, bool allowGameplayInput) =>
        RemapModeKeys(frame, keyboardState.IsKeyDown(Keys.B), keyboardState.IsKeyDown(Keys.G), allowGameplayInput);

    // B wins when both are down, as the building key did over the combat key before.
    public static PlayerCommandFrame RemapModeKeys(PlayerCommandFrame frame, bool buildingDown, bool combatDown, bool allowGameplayInput)
    {
        if (!allowGameplayInput)
        {
            return frame;
        }

        PlayerInteractionMode? mode = buildingDown
            ? PlayerInteractionMode.Building
            : combatDown ? PlayerInteractionMode.Combat : null;
        return frame with { RequestedMode = new PlayerModeSwitchIntent(mode) };
    }
}
