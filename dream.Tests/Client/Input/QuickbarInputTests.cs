using System.Reflection;
using Xunit;
using Dream.Client.Input;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Dream.Tests.Client.Input;

// M4: the digit keys press quickbar slots, and B and G took over the mode keys that D1 and D2 had.
public sealed class QuickbarInputTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static KeyboardState Keyboard(params Keys[] down)
    {
        KeyboardState state = (KeyboardState)Activator.CreateInstance(typeof(KeyboardState), nonPublic: true)!;
        // OpenTK's own window fills the state through an internal setter; its arity differs between versions.
        MethodInfo set = typeof(KeyboardState).GetMethods(Fields).Single(m => m.Name == "SetKeyState");
        foreach (Keys key in down)
        {
            object[] args = set.GetParameters().Length == 3 ? [key, 0, true] : [key, true];
            set.Invoke(state, args);
        }

        return state;
    }

    // What PlayerController makes of the keys before the remap: the old D1 and D2 mode keys, plus W and
    // Space so the rest of the frame has something in it. (CaptureCommandFrame itself needs a window's
    // mouse state; this is the same call it ends in.)
    private static PlayerCommandFrame CapturedWith(KeyboardState keys, bool allow = true) =>
        PlayerController.CreateCommandFrame(
            new PlayerSessionId("s"),
            0,
            allow,
            forwardAxis: keys.IsKeyDown(Keys.W) ? 1f : 0f,
            strafeAxis: 0f,
            verticalAxis: keys.IsKeyDown(Keys.Space) ? 1f : 0f,
            jumpPressed: keys.IsKeyDown(Keys.Space),
            freezePressed: false,
            toggleNoclipPressed: false,
            yawDelta: 0f,
            pitchDelta: 0f,
            primaryUsePressed: false,
            selectBuildingModePressed: keys.IsKeyDown(Keys.D1),
            selectCombatModePressed: keys.IsKeyDown(Keys.D2));

    [Fact]
    public void ReadMask_HasOneBitPerDigit1To6_AndNothingForOtherKeys()
    {
        Assert.Equal(0, QuickbarInput.ReadMask(Keyboard()));
        Assert.Equal(0b000001, QuickbarInput.ReadMask(Keyboard(Keys.D1)));
        Assert.Equal(0b100000, QuickbarInput.ReadMask(Keyboard(Keys.D6)));
        Assert.Equal(0b010101, QuickbarInput.ReadMask(Keyboard(Keys.D1, Keys.D3, Keys.D5)));
        // 0 is the noclip toggle, 7 to 9 are not slots, and letters are not either.
        Assert.Equal(0, QuickbarInput.ReadMask(Keyboard(Keys.D0, Keys.D7, Keys.D8, Keys.D9, Keys.B, Keys.G, Keys.Q)));
    }

    [Fact]
    public void NewPresses_AreEdges_AHeldKeyDoesNotRepeat()
    {
        Assert.Equal(0b000100, QuickbarInput.NewPresses(0, 0b000100));
        Assert.Equal(0, QuickbarInput.NewPresses(0b000100, 0b000100));
        Assert.Equal(0, QuickbarInput.NewPresses(0b000100, 0));
        Assert.Equal(0b000010, QuickbarInput.NewPresses(0b000100, 0b000110));
        // A key down while the chat line was open is recorded as held, so closing the line fires nothing.
        Assert.Equal(0, QuickbarInput.NewPresses(QuickbarInput.ReadMask(Keyboard(Keys.D2)), QuickbarInput.ReadMask(Keyboard(Keys.D2))));
        // Bits above the six slots never count.
        Assert.Equal(0, QuickbarInput.NewPresses(0, 1 << 6));
    }

    [Fact]
    public void D1AndD2_NoLongerChangeTheMode()
    {
        PlayerCommandFrame captured = CapturedWith(Keyboard(Keys.D1));
        Assert.Equal(PlayerInteractionMode.Building, captured.RequestedMode.Mode);

        Assert.Null(QuickbarInput.RemapModeKeys(captured, Keyboard(Keys.D1), allowGameplayInput: true).RequestedMode.Mode);
        Assert.Null(QuickbarInput.RemapModeKeys(CapturedWith(Keyboard(Keys.D2)), Keyboard(Keys.D2), allowGameplayInput: true).RequestedMode.Mode);
    }

    [Fact]
    public void BAndG_SelectBuildingAndCombat_BWinsWhenBothAreDown()
    {
        PlayerCommandFrame none = CapturedWith(Keyboard());
        Assert.Equal(PlayerInteractionMode.Building, QuickbarInput.RemapModeKeys(none, Keyboard(Keys.B), true).RequestedMode.Mode);
        Assert.Equal(PlayerInteractionMode.Combat, QuickbarInput.RemapModeKeys(none, Keyboard(Keys.G), true).RequestedMode.Mode);
        Assert.Equal(PlayerInteractionMode.Building, QuickbarInput.RemapModeKeys(none, Keyboard(Keys.B, Keys.G), true).RequestedMode.Mode);
        Assert.Null(QuickbarInput.RemapModeKeys(none, Keyboard(), true).RequestedMode.Mode);
    }

    [Fact]
    public void RemapModeKeys_KeepsEverythingElseOfTheFrame_AndReturnsItUntouchedWhenInputIsOff()
    {
        PlayerCommandFrame moving = CapturedWith(Keyboard(Keys.W, Keys.Space, Keys.B));
        PlayerCommandFrame remapped = QuickbarInput.RemapModeKeys(moving, Keyboard(Keys.W, Keys.Space, Keys.B), true);
        Assert.Equal(moving with { RequestedMode = remapped.RequestedMode }, remapped);
        Assert.Equal(PlayerInteractionMode.Building, remapped.RequestedMode.Mode);

        // Input off: the frame is the empty one the controller made, whatever keys are down.
        PlayerCommandFrame off = CapturedWith(Keyboard(Keys.B), allow: false);
        Assert.Equal(off, QuickbarInput.RemapModeKeys(off, Keyboard(Keys.B), allowGameplayInput: false));
        Assert.Null(off.RequestedMode.Mode);
    }
}
