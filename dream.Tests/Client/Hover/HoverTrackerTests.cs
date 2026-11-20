using Dream.Client.Hover;
using Dream.Rendering.Ui;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Hover;

// One test per row of the lifecycle table (design 4.4), with a synthetic dt.
public sealed class HoverTrackerTests
{
    private const float Dt = 0.05f;

    private static readonly HoverTargetId A = new(HoverKind.Enemy, 1);
    private static readonly HoverTargetId B = new(HoverKind.Enemy, 2);

    private static HoverFrame Step(HoverTracker t, HoverTargetId current, bool gone = false, bool gated = false, float dt = Dt) =>
        t.Advance(current, Vector3.One, current.IsNone ? HoverOrigin.None : HoverOrigin.Ray, dt, gone, gated);

    private static HoverTracker ShownOnA()
    {
        HoverTracker t = new();
        for (int i = 0; i < 5; i++)
        {
            Step(t, A);
        }

        Assert.Equal(A, t.Frame.PanelTarget);
        Assert.Equal(1f, t.Frame.PanelAlpha);
        return t;
    }

    [Fact]
    public void TheTuningMatchesTheDesignTable()
    {
        Assert.Equal(0.10f, HoverTuning.PanelDwellSeconds);
        Assert.Equal(0.08f, HoverTuning.PanelFadeInSeconds);
        Assert.Equal(0.15f, HoverTuning.LingerSeconds);
        Assert.Equal(0.10f, HoverTuning.FadeOutSeconds);
        Assert.Equal(0.25f, HoverTuning.DefeatedHoldSeconds);
        Assert.Equal(2f, HoverTuning.RepickPixels);
        Assert.Equal(0.10f, HoverTuning.RefreshSeconds);
        Assert.Equal(1.25f, HoverTuning.StickyInflate);
    }

    [Fact]
    public void NoneToA_CurrentChangesAtOnce_PanelShowsAfterTheDwell_AndFadesIn()
    {
        HoverTracker t = new();

        HoverFrame f = Step(t, A);
        Assert.Equal(A, f.Current);
        Assert.Equal(HoverPhase.Entered, f.Phase);
        Assert.Equal(HoverTargetId.None, f.Previous);
        Assert.False(f.PanelVisible);

        f = Step(t, A);
        Assert.Equal(HoverPhase.Stay, f.Phase);
        Assert.False(f.PanelVisible);
        Assert.Equal(Dt, f.DwellSeconds, 3);

        f = Step(t, A);
        Assert.Equal(HoverTuning.PanelDwellSeconds, f.DwellSeconds, 3);
        Assert.True(f.PanelVisible);
        Assert.Equal(A, f.PanelTarget);
        Assert.InRange(f.PanelAlpha, 0.01f, 0.99f);

        f = Step(t, A);
        Assert.Equal(1f, f.PanelAlpha);
    }

    [Fact]
    public void SweepingAcrossAPack_ShowsNoPanel()
    {
        HoverTracker t = new();
        HoverTargetId[] sweep = [A, B, A, B, HoverTargetId.None, A, B];
        foreach (HoverTargetId id in sweep)
        {
            Assert.False(Step(t, id).PanelVisible);
        }
    }

    [Fact]
    public void AHeld_StaysAndCountsDwell()
    {
        HoverTracker t = ShownOnA();

        HoverFrame f = Step(t, A);
        Assert.Equal(HoverPhase.Stay, f.Phase);
        Assert.Equal(A, f.Previous);
        Assert.Equal(5 * Dt, f.DwellSeconds, 3);
        Assert.Equal(HoverOrigin.Ray, f.Origin);
    }

    [Fact]
    public void AToB_WhileVisible_SwapsAtOnce_WithNoNewDwellAndNoFade()
    {
        HoverTracker t = ShownOnA();

        HoverFrame f = Step(t, B);
        Assert.Equal(B, f.Current);
        Assert.Equal(HoverPhase.Entered, f.Phase);
        Assert.Equal(A, f.Previous);
        Assert.Equal(B, f.PanelTarget);
        Assert.Equal(1f, f.PanelAlpha);
        Assert.Equal(0f, f.DwellSeconds);
    }

    [Fact]
    public void AToNone_LingersOnA_ThenFades_ThenHides()
    {
        HoverTracker t = ShownOnA();

        HoverFrame f = Step(t, HoverTargetId.None);
        Assert.Equal(HoverPhase.Exited, f.Phase);
        Assert.True(f.Current.IsNone);
        Assert.Equal(A, f.Previous);
        Assert.Equal(A, f.PanelTarget);
        Assert.Equal(1f, f.PanelAlpha);

        // 0.05 and 0.10 s in: still fully shown.
        f = Step(t, HoverTargetId.None);
        f = Step(t, HoverTargetId.None);
        Assert.Equal(HoverPhase.None, f.Phase);
        Assert.Equal(A, f.PanelTarget);
        Assert.Equal(1f, f.PanelAlpha);

        // 0.15 s: the linger is over; the next frame fades.
        f = Step(t, HoverTargetId.None);
        f = Step(t, HoverTargetId.None);
        Assert.Equal(A, f.PanelTarget);
        Assert.InRange(f.PanelAlpha, 0.01f, 0.99f);

        f = Step(t, HoverTargetId.None);
        Assert.False(f.PanelVisible);
        Assert.True(f.PanelTarget.IsNone);
    }

    [Fact]
    public void ReEnteringAInsideTheLinger_CancelsIt()
    {
        HoverTracker t = ShownOnA();
        Step(t, HoverTargetId.None);
        Step(t, HoverTargetId.None);

        HoverFrame f = Step(t, A);
        Assert.Equal(HoverPhase.Entered, f.Phase);
        Assert.Equal(A, f.PanelTarget);
        Assert.Equal(1f, f.PanelAlpha);

        // Far past the old linger it is still there.
        for (int i = 0; i < 10; i++)
        {
            f = Step(t, A);
        }

        Assert.True(f.PanelVisible);
        Assert.Equal(1f, f.PanelAlpha);
    }

    [Fact]
    public void AGoneWhileShown_StaysOnThePanelForTheDefeatedHold_ThenFades()
    {
        HoverTracker t = ShownOnA();

        HoverFrame f = Step(t, HoverTargetId.None, gone: true);
        Assert.Equal(HoverPhase.Exited, f.Phase);
        Assert.Equal(A, f.PanelTarget);

        // Held for the defeated hold (0.25 s), well past the linger (0.15 s).
        for (int i = 0; i < 4; i++)
        {
            f = Step(t, HoverTargetId.None, gone: true);
            Assert.Equal(A, f.PanelTarget);
            Assert.Equal(1f, f.PanelAlpha);
        }

        f = Step(t, HoverTargetId.None, gone: true);
        f = Step(t, HoverTargetId.None, gone: true);
        Assert.Equal(A, f.PanelTarget);
        Assert.InRange(f.PanelAlpha, 0.01f, 0.99f);

        f = Step(t, HoverTargetId.None, gone: true);
        Assert.False(f.PanelVisible);
    }

    [Fact]
    public void ATargetThatDiesDuringTheLinger_BecomesTheDefeatedHold()
    {
        HoverTracker t = ShownOnA();
        Step(t, HoverTargetId.None);
        Step(t, HoverTargetId.None);

        // The linger would have ended at 0.15 s; the hold restarts from the death instead.
        HoverFrame f = Step(t, HoverTargetId.None, gone: true);
        for (int i = 0; i < 4; i++)
        {
            f = Step(t, HoverTargetId.None, gone: true);
        }

        Assert.Equal(A, f.PanelTarget);
        Assert.Equal(1f, f.PanelAlpha);
    }

    [Fact]
    public void AGateClosing_HidesEverythingAtOnce_WithNoLinger()
    {
        HoverTracker t = ShownOnA();

        HoverFrame f = Step(t, A, gated: true);
        Assert.True(f.Current.IsNone);
        Assert.Equal(HoverPhase.Exited, f.Phase);
        Assert.False(f.PanelVisible);
        Assert.True(f.PanelTarget.IsNone);
        Assert.Equal(HoverOrigin.None, f.Origin);
    }

    [Fact]
    public void Clear_DropsTheHoverAndThePanel()
    {
        HoverTracker t = ShownOnA();
        t.Clear();

        Assert.Equal(default, t.Frame);
        HoverFrame f = Step(t, HoverTargetId.None);
        Assert.Equal(HoverPhase.None, f.Phase);
        Assert.False(f.PanelVisible);
    }
}
