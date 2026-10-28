using OpenTK.Mathematics;

namespace Dream.Rendering.Core;

// The six clip planes of a view-projection matrix (Gribb-Hartmann), for per-frame culling.
//
// OpenTK uses row vectors: a point is transformed as clip = (x, y, z, 1) * (view * projection),
// so clip.x is the dot product of the point with COLUMN 0 of the matrix, clip.w with column 3,
// and so on. The planes are therefore built from columns, not rows:
//   left  = c3 + c0   (clip.x >= -clip.w)
//   right = c3 - c0   (clip.x <=  clip.w)
//   bottom= c3 + c1, top = c3 - c1
//   near  = c3 + c2   (OpenGL clip space, clip.z >= -clip.w), far = c3 - c2
// Each plane (a, b, c, d) has a*x + b*y + c*z + d >= 0 on the inside and is normalized so
// (a, b, c) is a unit normal and the plane value is a signed distance.
//
// A degenerate matrix (for example an all-zero projection before the window size is known)
// yields an invalid frustum that accepts every box, so culling can never hide the world.
public readonly struct ViewFrustum
{
    private const float MinimumPlaneNormalLength = 1e-6f;

    private readonly Vector4 _left;
    private readonly Vector4 _right;
    private readonly Vector4 _bottom;
    private readonly Vector4 _top;
    private readonly Vector4 _near;
    private readonly Vector4 _far;

    private ViewFrustum(Vector4 left, Vector4 right, Vector4 bottom, Vector4 top, Vector4 near, Vector4 far)
    {
        _left = left;
        _right = right;
        _bottom = bottom;
        _top = top;
        _near = near;
        _far = far;
        IsValid = true;
    }

    // False for the default value and for a degenerate matrix; then every test passes.
    public bool IsValid { get; }

    public Vector4 Left => _left;

    public Vector4 Right => _right;

    public Vector4 Bottom => _bottom;

    public Vector4 Top => _top;

    public Vector4 Near => _near;

    public Vector4 Far => _far;

    public static ViewFrustum FromViewProjection(in Matrix4 viewProjection)
    {
        Vector4 c0 = viewProjection.Column0;
        Vector4 c1 = viewProjection.Column1;
        Vector4 c2 = viewProjection.Column2;
        Vector4 c3 = viewProjection.Column3;

        if (!TryNormalize(c3 + c0, out Vector4 left) ||
            !TryNormalize(c3 - c0, out Vector4 right) ||
            !TryNormalize(c3 + c1, out Vector4 bottom) ||
            !TryNormalize(c3 - c1, out Vector4 top) ||
            !TryNormalize(c3 + c2, out Vector4 near) ||
            !TryNormalize(c3 - c2, out Vector4 far))
        {
            return default;
        }

        return new ViewFrustum(left, right, bottom, top, near, far);
    }

    public static ViewFrustum FromViewAndProjection(in Matrix4 view, in Matrix4 projection)
    {
        // Row vectors: the view transform applies first, so it is on the left.
        return FromViewProjection(view * projection);
    }

    public bool ContainsPoint(Vector3 point)
    {
        if (!IsValid)
        {
            return true;
        }

        return Distance(_left, point) >= 0f &&
            Distance(_right, point) >= 0f &&
            Distance(_bottom, point) >= 0f &&
            Distance(_top, point) >= 0f &&
            Distance(_near, point) >= 0f &&
            Distance(_far, point) >= 0f;
    }

    // P-vertex test: for each plane, the box corner furthest along the plane normal is checked.
    // If that corner is outside any plane the whole box is outside. Boxes near a frustum corner
    // can be reported as intersecting while outside (conservative), never the other way round.
    public bool IntersectsAabb(Vector3 min, Vector3 max)
    {
        if (!IsValid)
        {
            return true;
        }

        return IsInsideOrOnPlane(_left, min, max) &&
            IsInsideOrOnPlane(_right, min, max) &&
            IsInsideOrOnPlane(_bottom, min, max) &&
            IsInsideOrOnPlane(_top, min, max) &&
            IsInsideOrOnPlane(_near, min, max) &&
            IsInsideOrOnPlane(_far, min, max);
    }

    public bool IntersectsBox(Vector3 center, Vector3 halfExtents)
    {
        return IntersectsAabb(center - halfExtents, center + halfExtents);
    }

    private static bool IsInsideOrOnPlane(in Vector4 plane, Vector3 min, Vector3 max)
    {
        float x = plane.X >= 0f ? max.X : min.X;
        float y = plane.Y >= 0f ? max.Y : min.Y;
        float z = plane.Z >= 0f ? max.Z : min.Z;
        return (plane.X * x) + (plane.Y * y) + (plane.Z * z) + plane.W >= 0f;
    }

    private static float Distance(in Vector4 plane, Vector3 point)
    {
        return (plane.X * point.X) + (plane.Y * point.Y) + (plane.Z * point.Z) + plane.W;
    }

    private static bool TryNormalize(Vector4 plane, out Vector4 normalized)
    {
        float length = MathF.Sqrt((plane.X * plane.X) + (plane.Y * plane.Y) + (plane.Z * plane.Z));
        if (!float.IsFinite(length) || length < MinimumPlaneNormalLength || !float.IsFinite(plane.W))
        {
            normalized = default;
            return false;
        }

        normalized = plane / length;
        return true;
    }
}
