using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// Optional: an IUiInput that also knows where the pointer is. IUiInput has no position of its own
// (C0 is frozen), so the router implements this next to it. Without it no cursor is drawn.
public interface IUiPointerSource
{
    UiPointer Pointer { get; }
}

// The gauntlet cursor, drawn last at its pivot, only while the UI is interactive (the mouse is free).
// Grab while something is dragged, pressed while the left button is down, otherwise the pointer.
public static class CursorPanel
{
    private const float ArtPixelsPerUnit = 4f;

    public static void Build(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s)
    {
        if (i is not IUiPointerSource source)
        {
            return;
        }

        UiPointer p = source.Pointer;
        if (!p.Interactive || !float.IsFinite(p.Position.X) || !float.IsFinite(p.Position.Y))
        {
            return;
        }

        string name = i.ActiveDragPayload >= 0
            ? UiSpriteNames.CursorGrab
            : p.LeftDown ? UiSpriteNames.CursorPressed : UiSpriteNames.CursorPointer;
        if (!s.TryGet(name, out UiSprite sprite))
        {
            return;
        }

        float k = l.Scale / ArtPixelsPerUnit;
        Vector2 origin = new(p.Position.X - sprite.PivotX * k, p.Position.Y - sprite.PivotY * k);
        c.Sprite(sprite, new UiRect(MathF.Round(origin.X), MathF.Round(origin.Y), sprite.W * k, sprite.H * k), UiColor.White);
    }
}
