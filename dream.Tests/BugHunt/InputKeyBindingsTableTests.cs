using Dream.Rendering.Ui.Menu;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-INPUT-5: the Key Bindings screen is the only binding reference the game has, and the bindings are
// not configurable, so the table IS the contract. It named "Q / E" for the potions (E has been
// interact / pick up since the economy pass, and the mana potion is R) and "1 / 2" for the modes (the
// quickbar took the digits; QuickbarInput.RemapModeKeys moved the modes onto B and G).
//
// The resolvers are read where they are written: PlayScene.Consumables (Q, R), PlayScene.Economy and
// PlayScene.Replication (E), QuickbarInput.RemapModeKeys (B, G), PlayScene.Update (Z), PlayerController
// (LeftShift), ChatInput (Enter, Tab). The keys below are named with the OpenTK constants the resolvers
// themselves use, so a remap that renames a constant breaks this test rather than lying to the player.
public class InputKeyBindingsTableTests
{
    private static string Effect(string input)
    {
        foreach ((string Input, string Effect) row in KeyBindingsPanel.Entries)
        {
            if (row.Input == input)
            {
                return row.Effect;
            }
        }

        return string.Empty;
    }

    private static bool Lists(string input) => Effect(input).Length > 0;

    [Fact]
    public void ThePotionRow_NamesTheKeysTheConsumableHotkeysRead()
    {
        // PlayScene.HandleConsumableHotkeys: Q is the healing potion, R the mana potion.
        string row = $"{Keys.Q} / {Keys.R}";

        Assert.Equal("Q / R", row);
        Assert.True(Lists(row), "the potion row must name Q and R");
        Assert.Contains("potion", Effect(row), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheModeRow_NamesTheKeysRemapModeKeysReads_NotTheDigitsTheQuickbarTook()
    {
        // QuickbarInput.RemapModeKeys: B is Building, G is Combat; D1/D2 never reach the wire.
        string row = $"{Keys.B} / {Keys.G}";

        Assert.Equal("B / G", row);
        Assert.Contains("mode", Effect(row), StringComparison.OrdinalIgnoreCase);
        Assert.False(Lists("1 / 2"), "the digits are quickbar slots, not the mode switch");
    }

    [Fact]
    public void TheInteractAndQuickbarRows_AreListedAndNotClaimedForPotions()
    {
        // PlayScene.Economy / PlayScene.Replication read E for interact and pick up.
        Assert.Contains("Interact", Effect(Keys.E.ToString()), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Quickbar", Effect("1 - 6"), StringComparison.OrdinalIgnoreCase);
        Assert.False(Lists("Q / E"), "E is interact, never a potion");
    }

    [Fact]
    public void TheKeysThatWereMissingAltogether_AreListed()
    {
        Assert.Equal("Walk", Effect(Keys.Z.ToString()));
        Assert.Equal("Sprint", Effect("Shift"));
        Assert.Equal("Chat", Effect(Keys.Enter.ToString()));
        Assert.Equal("Who is online", Effect(Keys.Tab.ToString()));
    }

    [Fact]
    public void EveryRowIsNamedOnce_AndNothingIsBlank()
    {
        HashSet<string> seen = [];
        foreach ((string Input, string Effect) row in KeyBindingsPanel.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Input));
            Assert.False(string.IsNullOrWhiteSpace(row.Effect));
            Assert.True(seen.Add(row.Input), $"{row.Input} is listed twice");
        }
    }
}
