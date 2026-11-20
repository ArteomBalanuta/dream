using OpenTK.Mathematics;

namespace Dream.Client.Hover;

// Ray against a vertical capsule (a vertical segment with a radius), allocation free. A sphere is the
// zero-length case.
internal static class HoverGeometry
{
    // The capsule whose feet are at `feet`, `height` tall, `radius` wide; the hit distance along the unit
    // direction. A capsule shorter than a sphere of that radius is the sphere at its mid height.
    public static bool RayCapsule(Vector3 origin, Vector3 direction, Vector3 feet, float height, float radius, out float depth)
    {
        float segmentLength = MathF.Max(0f, height - (2f * radius));
        float baseLift = height >= 2f * radius ? radius : height * 0.5f;
        Vector3 segmentStart = new(feet.X, feet.Y + baseLift, feet.Z);
        return RayVerticalSegment(origin, direction, segmentStart, segmentLength, radius, out depth);
    }

    public static bool RayVerticalSegment(Vector3 origin, Vector3 direction, Vector3 start, float length, float radius, out float depth)
    {
        depth = 0f;
        Vector3 w = origin - start;
        float b = direction.Y;
        float dw = Vector3.Dot(direction, w);
        float e = w.Y;

        float bestSq = float.PositiveInfinity;
        float bestT = 0f;

        float den = 1f - (b * b);
        if (den > 1e-6f)
        {
            float t = ((b * e) - dw) / den;
            float s = (e - (b * dw)) / den;
            if (t >= 0f && s >= 0f && s <= length)
            {
                Consider(w, direction, t, s, ref bestSq, ref bestT);
            }
        }

        Consider(w, direction, 0f, Math.Clamp(e, 0f, length), ref bestSq, ref bestT);
        Consider(w, direction, MathF.Max(0f, -dw), 0f, ref bestSq, ref bestT);
        Consider(w, direction, MathF.Max(0f, (b * length) - dw), length, ref bestSq, ref bestT);

        float radiusSq = radius * radius;
        if (!(bestSq <= radiusSq))
        {
            return false;
        }

        depth = MathF.Max(0f, bestT - MathF.Sqrt(radiusSq - bestSq));
        return true;
    }

    private static void Consider(Vector3 w, Vector3 direction, float t, float s, ref float bestSq, ref float bestT)
    {
        float x = w.X + (direction.X * t);
        float y = w.Y + (direction.Y * t) - s;
        float z = w.Z + (direction.Z * t);
        float sq = (x * x) + (y * y) + (z * z);
        if (sq < bestSq)
        {
            bestSq = sq;
            bestT = t;
        }
    }
}
