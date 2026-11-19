using Dream.Rendering.Core;
using Dream.Simulation.World;
using OpenTK.Mathematics;

namespace Dream.Rendering.Shadows;

// What a caster draw needs to know about the target it is drawing into: the matrices the depth program is given, the
// light-space frustum its casters are culled against, and the focus the distance term is measured from. One value per
// target (the sun map, or one face of one fire), built by ShadowCasterPass and handed to every caster renderer.
public readonly struct ShadowCasterView
{
    public ShadowCasterView(
        in Matrix4 view, in Matrix4 projection, in ViewFrustum frustum, Vector3 focus, int excludeOwner = 0)
    {
        ExcludeOwner = excludeOwner;
        View = view;
        Projection = projection;
        Frustum = frustum;
        Focus = focus;
    }

    public readonly Matrix4 View;

    public readonly Matrix4 Projection;

    public readonly ViewFrustum Frustum;

    public readonly Vector3 Focus;

    // The actor id whose skinned body is left out of this view (0 = none): a fire tile does not draw the actor that
    // carries the fire, whose own body sits a hand's width from the light and would shadow the whole pool.
    public readonly int ExcludeOwner;

    public bool Accepts(Vector3 min, Vector3 max, float maxDistance = ShadowCasterSelection.CasterDistance) =>
        ShadowCasterSelection.Accepts(min, max, Focus, in Frustum, maxDistance);
}

// The one caster-acceptance predicate: pure, so the culling decision is testable without GL.
//
// The camera-direction half of the visible pass's cull (WorldVisibility.IsCubeVisible, StaticWorldChunkRenderer.
// IsChunkVisible) is deliberately NOT used: it rejects geometry behind the camera, and a caster behind the camera can
// still cast into view. What replaces it is a light-space frustum test, which is correct for any caster position, plus
// a distance term that keeps the passes consistent.
public static class ShadowCasterSelection
{
    // A caster the visible pass never draws must not cast either, or the shadow and its object disagree.
    public const float CasterDistance = WorldVisibility.MaxRenderDistance;

    /// <summary>
    /// True when the box is within <paramref name="maxDistance"/> of the focus on the ground plane AND touches the light
    /// frustum. The frustum must be the light's FULL volume, near plane pulled back included (a tall caster outside the
    /// receiver box still casts into it); see <see cref="SunFrustum"/>.
    /// </summary>
    public static bool Accepts(Vector3 min, Vector3 max, Vector3 focus, in ViewFrustum frustum, float maxDistance = CasterDistance)
    {
        return HorizontalDistanceSquaredToBox(min, max, focus) <= maxDistance * maxDistance
            && frustum.IntersectsAabb(min, max);
    }

    /// <summary>Squared ground-plane distance from a point to the nearest point of a box (0 inside its footprint).</summary>
    public static float HorizontalDistanceSquaredToBox(Vector3 min, Vector3 max, Vector3 point)
    {
        float dx = MathF.Max(0f, MathF.Max(min.X - point.X, point.X - max.X));
        float dz = MathF.Max(0f, MathF.Max(min.Z - point.Z, point.Z - max.Z));
        return (dx * dx) + (dz * dz);
    }

    /// <summary>
    /// The sun map's culling frustum from the combined world-to-light-clip matrix SunShadowVolume.Build returns.
    /// FromViewProjection takes the combined matrix as it is (view * projection), which is exactly what
    /// FromViewAndProjection(identity, combined) computes, without the identity multiply. The orthographic matrix already
    /// has the caster-extension near plane in it (Build pulls the near plane toward the light by CasterExtensionFor), so a
    /// tall caster beyond the receiver box is inside this frustum, which a receiver-box test would have cut.
    /// </summary>
    public static ViewFrustum SunFrustum(in Matrix4 lightViewProjection) => ViewFrustum.FromViewProjection(in lightViewProjection);

    /// <summary>One fire face's culling frustum.</summary>
    public static ViewFrustum FireFaceFrustum(in Matrix4 faceView, in Matrix4 faceProjection) =>
        ViewFrustum.FromViewAndProjection(in faceView, in faceProjection);

    /// <summary>
    /// A conservative cube around a skinned actor from its model matrix (origin at the feet, scale in the rows). Actors have
    /// no authored bounds on the client; a radius of three metres times the matrix scale holds the biggest of them, and
    /// being generous only costs a draw.
    /// </summary>
    public static (Vector3 Min, Vector3 Max) ActorBounds(in Matrix4 model)
    {
        Vector3 origin = model.Row3.Xyz;
        float scale = MathF.Max(1f, model.Row1.Xyz.Length);
        float radius = ActorRadius * scale;
        return (origin - new Vector3(radius), origin + new Vector3(radius));
    }

    public const float ActorRadius = 3f;
}
