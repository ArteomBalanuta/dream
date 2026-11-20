using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The gauntlet cursor, drawn last at its pivot, only while the UI is interactive (the mouse is free).
// Grab while something is dragged, then by what the pointer rests on (attack, talk, take, use), pressed while the
// left button is down, otherwise the pointer.
public static class CursorPanel
{
    public static void Build(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s)
    {
        UiPointer p = i.Pointer;
        UiCursor.Draw(c, s, in p, l.Scale, UiCursor.Select(i.ActiveDragPayload >= 0 || v.CameraDragging, p.LeftDown, v.HoverCursorKind));
    }
}
