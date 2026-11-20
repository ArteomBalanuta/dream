using OpenTK.Mathematics;

namespace Dream.Rendering.Ui;

// Where the hover info panel goes. Pure: numbers in, a whole-pixel top-left out, no canvas and no state.
// The panel sits down and right of the pointer, flips to the other side of it where it would leave the screen,
// stays inside a margin, steps out of the status bar and (with the inventory open) the windows, and never
// covers the cursor sprite.
public static class HoverPanelPlacement
{
    // Pointer to panel corner, and the margin kept to the screen edge, in UI units (times the layout scale).
    public const float OffsetXUnits = 18f;
    public const float OffsetYUnits = 22f;
    public const float MarginUnits = 6f;

    // Gap between an anchor and the panel in Anchored mode.
    public const float AnchorGapUnits = 4f;

    private const int Passes = 4;

    // Top-left of a w x h panel for a pointer at `pointer`. cursor is the sprite rect drawn at the pointer
    // (UiCursor.Rect); the panel never overlaps it while there is room.
    public static Vector2 AtPointer(Vector2 pointer, float w, float h, in UiLayout l, bool inventoryOpen, UiRect cursor)
    {
        if (!IsFinite(pointer) || !float.IsFinite(w) || !float.IsFinite(h))
        {
            return Fallback(w, h, in l);
        }

        float s = l.Scale;
        float margin = MarginUnits * s;
        float x = pointer.X + (OffsetXUnits * s);
        float y = pointer.Y + (OffsetYUnits * s);
        if (x + w > l.ViewportWidth - margin)
        {
            x = pointer.X - (OffsetXUnits * s) - w;
        }

        if (y + h > l.ViewportHeight - margin)
        {
            y = pointer.Y - (OffsetYUnits * s) - h;
        }

        return Settle(x, y, w, h, in l, inventoryOpen, cursor, hasCursor: true);
    }

    // Top-left of a w x h panel centred above anchor (a screen point such as the top of an actor), below it when
    // there is no room above.
    public static Vector2 Anchored(Vector2 anchor, float w, float h, in UiLayout l, bool inventoryOpen)
    {
        if (!IsFinite(anchor) || !float.IsFinite(w) || !float.IsFinite(h))
        {
            return Fallback(w, h, in l);
        }

        float s = l.Scale;
        float x = anchor.X - (w * 0.5f);
        float y = anchor.Y - (AnchorGapUnits * s) - h;
        if (y < MarginUnits * s)
        {
            y = anchor.Y + (AnchorGapUnits * s);
        }

        return Settle(x, y, w, h, in l, inventoryOpen, default, hasCursor: false);
    }

    private static Vector2 Settle(float x, float y, float w, float h, in UiLayout l, bool inventoryOpen, UiRect cursor, bool hasCursor)
    {
        float margin = MarginUnits * l.Scale;
        Vector2 home = Fit(x, y, w, h, in l, inventoryOpen, margin);
        if (hasCursor && Overlaps(home.X, home.Y, w, h, cursor))
        {
            // Four ways off the cursor: below it, above it, right of it, left of it. Each is clamped and pushed out
            // of the bar and the windows like the first try; the nearest one that is clear of everything wins, else
            // the nearest one clear of the cursor.
            Span<Vector2> options =
            [
                Fit(home.X, cursor.Bottom, w, h, in l, inventoryOpen, margin),
                Fit(home.X, cursor.Y - h, w, h, in l, inventoryOpen, margin),
                Fit(cursor.Right, home.Y, w, h, in l, inventoryOpen, margin),
                Fit(cursor.X - w, home.Y, w, h, in l, inventoryOpen, margin)
            ];
            int best = -1;
            int bestClearOfCursor = -1;
            float bestCost = float.MaxValue;
            float bestClearCost = float.MaxValue;
            for (int k = 0; k < options.Length; k++)
            {
                Vector2 o = options[k];
                if (Overlaps(o.X, o.Y, w, h, cursor))
                {
                    continue;
                }

                float cost = MathF.Abs(o.X - home.X) + MathF.Abs(o.Y - home.Y);
                if (cost < bestClearCost)
                {
                    bestClearCost = cost;
                    bestClearOfCursor = k;
                }

                if (cost < bestCost && !HitsObstacle(o.X, o.Y, w, h, in l, inventoryOpen))
                {
                    bestCost = cost;
                    best = k;
                }
            }

            if (best >= 0)
            {
                home = options[best];
            }
            else if (bestClearOfCursor >= 0)
            {
                home = options[bestClearOfCursor];
            }
        }

        return home;
    }

    // Clamped to the screen, pushed out of the bar and the open windows, whole pixels, still on screen.
    private static Vector2 Fit(float x, float y, float w, float h, in UiLayout l, bool inventoryOpen, float margin)
    {
        x = ClampAxis(x, w, l.ViewportWidth, margin);
        y = ClampAxis(y, h, l.ViewportHeight, margin);
        for (int pass = 0; pass < Passes; pass++)
        {
            bool moved = PushOut(ref y, x, w, h, l.Bar, in l, margin);
            if (inventoryOpen)
            {
                moved |= PushOut(ref y, x, w, h, l.Inventory, in l, margin);
                moved |= PushOut(ref y, x, w, h, l.Character, in l, margin);
            }

            if (!moved)
            {
                break;
            }
        }

        x = ClampAxis(MathF.Round(x), w, l.ViewportWidth, margin);
        y = ClampAxis(MathF.Round(y), h, l.ViewportHeight, margin);
        return new Vector2(MathF.Round(x), MathF.Round(y));
    }

    private static bool HitsObstacle(float x, float y, float w, float h, in UiLayout l, bool inventoryOpen) =>
        Overlaps(x, y, w, h, l.Bar)
        || (inventoryOpen && (Overlaps(x, y, w, h, l.Inventory) || Overlaps(x, y, w, h, l.Character)));

    private static bool Overlaps(float x, float y, float w, float h, UiRect r) =>
        r.W > 0f && r.H > 0f && x < r.Right && r.X < x + w && y < r.Bottom && r.Y < y + h;

    // Moves the panel vertically off `obstacle` when they overlap: away from the side its centre is on, or to the
    // other side when that one would leave the screen. True when it moved.
    private static bool PushOut(ref float y, float x, float w, float h, UiRect obstacle, in UiLayout l, float margin)
    {
        if (obstacle.W <= 0f || obstacle.H <= 0f ||
            !(x < obstacle.Right && obstacle.X < x + w && y < obstacle.Bottom && obstacle.Y < y + h))
        {
            return false;
        }

        float above = obstacle.Y - h;
        float below = obstacle.Bottom;
        bool aboveFits = above >= margin;
        bool belowFits = below + h <= l.ViewportHeight - margin;
        bool preferAbove = y + (h * 0.5f) < obstacle.Y + (obstacle.H * 0.5f);
        float target;
        if (aboveFits && belowFits)
        {
            target = preferAbove ? above : below;
        }
        else if (aboveFits)
        {
            target = above;
        }
        else if (belowFits)
        {
            target = below;
        }
        else
        {
            // No room either side: the one that keeps more of the panel on screen.
            target = obstacle.Y >= l.ViewportHeight - below ? above : below;
        }

        target = ClampAxis(target, h, l.ViewportHeight, margin);
        if (target == y)
        {
            return false;
        }

        y = target;
        return true;
    }

    // Keeps [v, v + size] inside [margin, extent - margin]; a panel too big for that is centred.
    private static float ClampAxis(float v, float size, float extent, float margin)
    {
        float hi = extent - margin - size;
        if (hi < margin)
        {
            return (extent - size) * 0.5f;
        }

        return Math.Clamp(v, margin, hi);
    }

    private static Vector2 Fallback(float w, float h, in UiLayout l)
    {
        float margin = MarginUnits * l.Scale;
        float fw = float.IsFinite(w) ? w : 0f;
        float fh = float.IsFinite(h) ? h : 0f;
        return new Vector2(
            MathF.Round(ClampAxis(margin, fw, l.ViewportWidth, margin)),
            MathF.Round(ClampAxis(margin, fh, l.ViewportHeight, margin)));
    }

    private static bool IsFinite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
}
