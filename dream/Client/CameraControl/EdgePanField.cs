using OpenTK.Mathematics;

namespace Dream.Client.CameraControl;

// The edge-pan direction for a cursor position: screen space, x right, y up, length <= 1.
public static class EdgePanField
{
    public static Vector2 Evaluate(Vector2 cursorPixel, Vector2i clientSize, float marginPixels)
    {
        if (!float.IsFinite(cursorPixel.X) || !float.IsFinite(cursorPixel.Y) ||
            clientSize.X <= 0 || clientSize.Y <= 0 || !float.IsFinite(marginPixels) || marginPixels <= 0f)
        {
            return Vector2.Zero;
        }

        float ex = Axis(cursorPixel.X, clientSize.X, Math.Min(marginPixels, clientSize.X / 3f));
        // Pixel y grows downwards; the field is positive at the top edge.
        float ey = -Axis(cursorPixel.Y, clientSize.Y, Math.Min(marginPixels, clientSize.Y / 3f));
        Vector2 result = new(ex, ey);
        float length = result.Length;
        if (length > 1f)
        {
            result /= length;
        }

        return result;
    }

    // Negative towards the low edge, positive towards the high edge.
    private static float Axis(float p, int size, float m)
    {
        if (p < m)
        {
            return -Ramp((m - p) / m);
        }

        float high = size - 1 - m;
        if (p > high)
        {
            return Ramp((p - high) / m);
        }

        return 0f;
    }

    private static float Ramp(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
