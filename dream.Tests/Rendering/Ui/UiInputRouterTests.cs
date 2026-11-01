using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Ui;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.Ui;

public class UiInputRouterTests
{
    private static readonly UiRect Box = new(10, 10, 40, 40);
    private static readonly UiRect Other = new(100, 10, 40, 40);

    private static UiPointer At(float x, float y, bool left = false, bool right = false, bool interactive = true) =>
        new(new Vector2(x, y), left, right, interactive);

    // One frame: begin, ask the widgets, end. The widgets see the hot id of the frame before.
    private static (bool Click, bool Hover) Frame(UiInputRouter router, UiPointer pointer, int id = 1, UiRect? rect = null)
    {
        router.BeginFrame(pointer);
        bool click = router.Button(id, rect ?? Box);
        bool hover = router.Hovered(id, rect ?? Box);
        router.EndFrame();
        return (click, hover);
    }

    [Fact]
    public void Button_ClicksOnReleaseOverTheSameWidget()
    {
        UiInputRouter router = new();

        Frame(router, At(20, 20));
        Frame(router, At(20, 20));
        (bool downClick, _) = Frame(router, At(20, 20, left: true));
        (bool releaseClick, _) = Frame(router, At(20, 20));

        Assert.False(downClick);
        Assert.True(releaseClick);
    }

    [Fact]
    public void Button_DoesNotClickWhenReleasedElsewhere()
    {
        UiInputRouter router = new();
        Frame(router, At(20, 20));
        Frame(router, At(20, 20));
        Frame(router, At(20, 20, left: true));
        Frame(router, At(200, 200, left: true));

        (bool click, _) = Frame(router, At(200, 200));

        Assert.False(click);
    }

    [Fact]
    public void Hovered_ReportsThePointerFromTheFrameBefore()
    {
        UiInputRouter router = new();

        Assert.False(Frame(router, At(20, 20)).Hover);
        Assert.True(Frame(router, At(20, 20)).Hover);
        // Hover is the previous frame's answer, so it lingers one frame after the pointer leaves.
        Assert.True(Frame(router, At(300, 300)).Hover);
        Assert.False(Frame(router, At(300, 300)).Hover);
    }

    [Fact]
    public void NotInteractive_NothingIsHoveredOrClicked()
    {
        UiInputRouter router = new();

        for (int i = 0; i < 4; i++)
        {
            (bool click, bool hover) = Frame(router, At(20, 20, left: i == 2, interactive: false));
            Assert.False(click);
            Assert.False(hover);
        }
    }

    [Fact]
    public void ButtonHeldWhenTheUiBecomesInteractive_IsNotAPress()
    {
        UiInputRouter router = new();
        Frame(router, At(20, 20, left: true, interactive: false));
        Frame(router, At(20, 20, left: true));
        Frame(router, At(20, 20, left: true));

        (bool click, _) = Frame(router, At(20, 20));

        Assert.False(click);
    }

    [Fact]
    public void LaterWidgetsSitOnTopOfEarlierOnes()
    {
        UiInputRouter router = new();
        UiPointer over = At(20, 20);
        for (int i = 0; i < 2; i++)
        {
            router.BeginFrame(over);
            router.Hovered(1, Box);
            router.Hovered(2, Box);
            router.EndFrame();
        }

        Assert.Equal(2, router.HotId);
    }

    [Fact]
    public void RightClick_FiresOnReleaseOverTheWidget()
    {
        UiInputRouter router = new();
        Frame(router, At(20, 20));
        Frame(router, At(20, 20));
        router.BeginFrame(At(20, 20, right: true));
        bool down = router.RightClicked(1, Box);
        router.EndFrame();
        router.BeginFrame(At(20, 20));
        bool up = router.RightClicked(1, Box);
        router.EndFrame();

        Assert.False(down);
        Assert.True(up);
    }

    private static void PressAndDrag(UiInputRouter router, out bool started)
    {
        started = false;
        Frame(router, At(20, 20));
        Frame(router, At(20, 20));
        router.BeginFrame(At(20, 20, left: true));
        router.BeginDrag(1, Box, payload: 7);
        router.EndFrame();
        router.BeginFrame(At(60, 20, left: true));
        started = router.BeginDrag(1, Box, payload: 7);
        router.EndFrame();
    }

    [Fact]
    public void Drag_StartsPastTheThresholdAndEndsOnReleaseWithThePayloadAndDropPoint()
    {
        UiInputRouter router = new();

        PressAndDrag(router, out bool started);
        Assert.True(started);
        Assert.Equal(7, router.ActiveDragPayload);

        router.BeginFrame(At(120, 30));
        bool ended = router.TryEndDrag(out int payload, out Vector2 drop);
        router.EndFrame();

        Assert.True(ended);
        Assert.Equal(7, payload);
        Assert.Equal(new Vector2(120, 30), drop);
        Assert.Equal(-1, router.ActiveDragPayload);
    }

    [Fact]
    public void Drag_ReleaseIsNotAlsoAClick()
    {
        UiInputRouter router = new();
        PressAndDrag(router, out _);

        router.BeginFrame(At(20, 20));
        bool click = router.Button(1, Box);
        router.EndFrame();

        Assert.False(click);
    }

    [Fact]
    public void Drag_NobodyEndedIsDroppedAtEndFrame()
    {
        UiInputRouter router = new();
        PressAndDrag(router, out _);

        router.BeginFrame(At(300, 300));
        router.EndFrame();

        Assert.Equal(-1, router.ActiveDragPayload);
    }

    [Fact]
    public void Drag_SmallMovementIsNotADrag()
    {
        UiInputRouter router = new();
        Frame(router, At(20, 20));
        Frame(router, At(20, 20));
        router.BeginFrame(At(20, 20, left: true));
        router.EndFrame();
        router.BeginFrame(At(22, 21, left: true));
        bool started = router.BeginDrag(1, Box, 3);
        router.EndFrame();

        Assert.False(started);
        Assert.Equal(-1, router.ActiveDragPayload);
    }

    [Fact]
    public void CancelDrag_DropsItWithoutAnEnd()
    {
        UiInputRouter router = new();
        PressAndDrag(router, out _);

        router.CancelDrag();
        router.BeginFrame(At(120, 30));
        bool ended = router.TryEndDrag(out _, out _);
        router.EndFrame();

        Assert.False(ended);
        Assert.Equal(-1, router.ActiveDragPayload);
    }

    [Fact]
    public void LosingInteractivityMidDrag_CancelsIt()
    {
        UiInputRouter router = new();
        PressAndDrag(router, out _);

        router.BeginFrame(At(60, 20, left: true, interactive: false));
        router.EndFrame();

        Assert.Equal(-1, router.ActiveDragPayload);
    }

    [Fact]
    public void Modal_WidgetsUnderItCannotTakeThePointerOrClick()
    {
        UiInputRouter router = new();
        UiRect backdrop = new(0, 0, 400, 400);

        // Frames with a modal layer: the panel sets ModalActive, then builds the modal widget.
        void Modal(UiPointer p, out bool underClick, out bool modalClick)
        {
            router.BeginFrame(p);
            underClick = router.Button(1, Box);
            router.ModalActive = true;
            router.Hovered(9, backdrop);
            modalClick = router.Button(10, Box);
            router.EndFrame();
        }

        Modal(At(20, 20), out _, out _);
        Modal(At(20, 20), out _, out _);
        Modal(At(20, 20, left: true), out _, out _);
        Modal(At(20, 20), out bool underClick, out bool modalClick);

        Assert.False(underClick);
        Assert.True(modalClick);
    }

    [Fact]
    public void Modal_EndsWhenThePanelStopsSettingIt()
    {
        UiInputRouter router = new();
        router.BeginFrame(At(20, 20));
        router.ModalActive = true;
        router.EndFrame();

        router.BeginFrame(At(20, 20));
        Assert.False(router.ModalActive);
        router.Hovered(1, Box);
        router.EndFrame();
        Frame(router, At(20, 20));

        Assert.True(Frame(router, At(20, 20)).Hover);
    }

    [Fact]
    public void ARouterFrame_AllocatesNothing()
    {
        UiInputRouter router = new();
        UiPointer p = At(20, 20, left: true);

        long bytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            router.BeginFrame(p);
            router.Button(1, Box);
            router.Hovered(2, Other);
            router.RightClicked(3, Box);
            router.BeginDrag(1, Box, 4);
            router.TryEndDrag(out _, out _);
            router.EndFrame();
        });

        Assert.Equal(0, bytes);
    }
}
