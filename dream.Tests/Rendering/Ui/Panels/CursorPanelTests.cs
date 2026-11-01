using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

public class CursorPanelTests
{
    private static StatusBarRecordingCanvas Run(StatusBarFakeInput input, UiLayout l)
    {
        StatusBarRecordingCanvas c = new(StatusBarTestData.Table);
        CursorPanel.Build(c, input, StatusBarTestData.View(), l, StatusBarTestData.Table);
        return c;
    }

    [Fact]
    public void Pointer_IsDrawnWithItsPivotOnThePointer()
    {
        UiLayout l = UiLayout.Create(1920, 1080, 0f);
        StatusBarFakeInput input = new() { Pointer = new UiPointer(new Vector2(400f, 300f), false, false, true) };

        StatusBarRecordingCanvas c = Run(input, l);

        StatusBarCall call = Assert.Single(c.Calls);
        Assert.Equal("cursor.pointer", call.Name);
        UiSprite sprite = StatusBarTestData.Table.Get("cursor.pointer");
        float k = l.Scale / 4f;
        Assert.Equal(400f, call.Rect.X + sprite.PivotX * k, 0);
        Assert.Equal(300f, call.Rect.Y + sprite.PivotY * k, 0);
        Assert.Equal(sprite.W * k, call.Rect.W, 3);
    }

    [Fact]
    public void PressedAndGrabSpritesFollowTheInputState()
    {
        UiLayout l = UiLayout.Create(1920, 1080, 0f);

        StatusBarFakeInput pressed = new() { Pointer = new UiPointer(new Vector2(10f, 10f), true, false, true) };
        Assert.Equal("cursor.pressed", Assert.Single(Run(pressed, l).Calls).Name);

        StatusBarFakeInput drag = new() { Pointer = new UiPointer(new Vector2(10f, 10f), true, false, true), ActiveDragPayload = 3 };
        Assert.Equal("cursor.grab", Assert.Single(Run(drag, l).Calls).Name);
    }

    [Fact]
    public void NotInteractiveOrNoPointerSourceDrawsNothing()
    {
        UiLayout l = UiLayout.Create(1920, 1080, 0f);

        Assert.Empty(Run(new StatusBarFakeInput { Pointer = new UiPointer(new Vector2(10f, 10f), false, false, false) }, l).Calls);
        Assert.Empty(Run(new StatusBarFakeInput { Pointer = new UiPointer(new Vector2(float.NaN, 10f), false, false, true) }, l).Calls);

        StatusBarRecordingCanvas c = new(StatusBarTestData.Table);
        CursorPanel.Build(c, new PlainInput(), StatusBarTestData.View(), l, StatusBarTestData.Table);
        Assert.Empty(c.Calls);
    }

    private sealed class PlainInput : IUiInput
    {
        public int ActiveDragPayload => -1;

        public bool ModalActive { get; set; }

        public UiPointer Pointer => default;

        public bool Button(int id, UiRect r) => false;

        public bool Hovered(int id, UiRect r) => false;

        public bool RightClicked(int id, UiRect r) => false;

        public bool BeginDrag(int id, UiRect r, int payload) => false;

        public bool TryEndDrag(out int payload, out Vector2 dropPosition)
        {
            payload = -1;
            dropPosition = default;
            return false;
        }
    }
}
