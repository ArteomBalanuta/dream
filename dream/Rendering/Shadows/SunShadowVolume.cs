using Dream.Client.Presentation;
using OpenTK.Mathematics;

namespace Dream.Rendering.Shadows;

// The sun/moon shadow map's mathematics: pure, no GL. ONE orthographic map, no cascades, because the isometric
// camera is fixed (pitch, yaw, FOV, distance are constants), so the receiver region is a constant-size box that only
// translates with the focus.
//
// Conventions (this repo, OpenTK): row vectors, so a point transforms as `p * M` and matrices compose left to right
// (`world * view * projection`). All matrices here map WORLD to the named space.
public static class SunShadowVolume
{
    // Half the depth span kept behind the receiver centre, away from the light. The receivers are the visible
    // footprint plus relief and props; the light-space depth extent of that region is smaller than OrthoSide, so
    // half of it each side of the centre holds it. Casters nearer the light come in via the NEAR plane pull-back.
    private static float HalfOrthoSide => ShadowSettings.OrthoSide * 0.5f;

    /// <summary>
    /// Builds the world-to-light-clip matrix for the dominant light.
    /// <paramref name="lightDirection"/> points TOWARD the light. <paramref name="elevationSin"/> is its sine of
    /// elevation (it only sizes the caster extension). The returned DepthRange is the world-unit length of the
    /// ortho depth interval, so a world-unit bias converts to depth units as bias / DepthRange.
    /// </summary>
    public static (Matrix4 LightViewProjection, float TexelWorld, float DepthRange) Build(
        Vector3 focus, Vector3 lightDirection, float elevationSin, int resolution)
    {
        if (resolution <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }

        Vector3 toLight = lightDirection.LengthSquared > 1e-12f ? Vector3.Normalize(lightDirection) : Vector3.UnitY;
        float side = ShadowSettings.OrthoSide;
        float texel = side / resolution;
        float extension = ShadowSettings.CasterExtensionFor(elevationSin);

        Vector3 centre = focus + ViewForwardGround * ShadowSettings.ReceiverCentreOffset;
        Vector3 up = MathF.Abs(toLight.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

        // The view's ORIGIN is fixed at the world origin (not at the focus): the light-space coordinates of the
        // centre then change by exactly the focus translation projected on the light axes, which is what makes
        // snapping them to whole texels meaningful. A view recentred on the focus would put the centre at the same
        // light-space XY every frame and the snap would do nothing (the design's LookAt(centre + L*BackOff, centre)
        // has that flaw).
        Matrix4 lightView = Matrix4.LookAt(toLight * side, Vector3.Zero, up);
        Vector3 c = Vector3.TransformPosition(centre, lightView);
        float snappedX = Snap(c.X, texel);
        float snappedY = Snap(c.Y, texel);

        // View space looks down -Z, so the centre sits at distance -c.Z. The slab runs HalfOrthoSide behind it; the
        // near plane is pulled back TOWARD the light by the caster extension (costs no texels).
        float centreDistance = -c.Z;
        float near = centreDistance - HalfOrthoSide - extension;
        float far = centreDistance + HalfOrthoSide;
        Matrix4 projection = Matrix4.CreateOrthographicOffCenter(
            snappedX - HalfOrthoSide, snappedX + HalfOrthoSide,
            snappedY - HalfOrthoSide, snappedY + HalfOrthoSide,
            near, far);

        return (lightView * projection, texel, far - near);
    }

    /// <summary>Quantises <paramref name="value"/> to a whole number of <paramref name="step"/>s.</summary>
    public static float Snap(float value, float step) => MathF.Round(value / step) * step;

    /// <summary>
    /// World to shadow-map TEXTURE space: xy in 0..1 across the map, z the 0..1 depth the map stores. This is the
    /// matrix the UBO carries (FrameLighting.SunShadowMatrix), so the shader does one multiply per lookup.
    /// </summary>
    public static Matrix4 ToTextureSpace(in Matrix4 lightViewProjection)
    {
        Matrix4 scaleBias = new(
            0.5f, 0f, 0f, 0f,
            0f, 0.5f, 0f, 0f,
            0f, 0f, 0.5f, 0f,
            0.5f, 0.5f, 0.5f, 1f);
        return lightViewProjection * scaleBias;
    }

    /// <summary>Normal offset in world units: it moves the LOOKUP along the normal, never the depth, so contact points stay dark.</summary>
    public static float NormalOffsetWorld(float texelWorld) => ShadowSettings.NormalOffsetTexels * texelWorld;

    /// <summary>
    /// The CONSTANT bias term converted to stored-depth units (what the UBO carries in SunShadowParams.y). Its job:
    /// absorb depth quantisation and the hardware 2x2 comparison jitter on faces nearly facing the light. The
    /// slope-scaled term is this value times (1 + SlopeToConstantRatio * tan), so one depth range converts both.
    /// </summary>
    public static float ConstantBiasDepth(float depthRange) => ShadowSettings.ConstantBiasWorld / depthRange;

    /// <summary>
    /// The full bias in stored-depth units for a surface whose normal makes <paramref name="nDotL"/> with the light:
    /// constant + slope-scaled, where the slope term is SlopeBiasTexels x texel per unit tan. Its slope term exists
    /// because the caster pass cannot cull front faces (chunk meshes are open shells of exposed faces, so culling
    /// would delete the ground's top faces), so acne on grazing faces must be beaten by a bias that grows with
    /// tan(angle of incidence), and the PCF kernel's reach makes that growth proportional to the texel.
    /// Mirrors dreamSunVisibility in lighting.glsl, which forms it from SunShadowParams.x and .y.
    /// </summary>
    public static float BiasDepth(float nDotL, float depthRange, float texelWorld) =>
        ConstantBiasDepth(depthRange) * (1f + ShadowSettings.SlopePerInverseResolution * (texelWorld / ShadowSettings.OrthoSide) * SlopeTan(nDotL));

    /// <summary>tan of the incidence angle, with the grazing blow-up capped (cosine floor) and the result clamped.</summary>
    public static float SlopeTan(float nDotL)
    {
        float ndl = MathF.Max(nDotL, 0f);
        float tan = MathF.Sqrt(MathF.Max(1f - ndl * ndl, 0f)) / MathF.Max(ndl, ShadowSettings.GrazingCosineFloor);
        return MathF.Min(tan, ShadowSettings.SlopeTanMax);
    }

    /// <summary>The ground-plane direction the fixed camera looks along (from the camera toward the focus).</summary>
    public static Vector3 ViewForwardGround { get; } = ComputeViewForwardGround();

    private static Vector3 ComputeViewForwardGround()
    {
        Vector3 away = -IsometricView.FocusToCamera;
        return Vector3.Normalize(new Vector3(away.X, 0f, away.Z));
    }
}
