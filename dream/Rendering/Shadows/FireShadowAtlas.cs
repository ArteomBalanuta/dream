using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.Sky;
using OpenTK.Mathematics;

namespace Dream.Rendering.Shadows;

// The fire shadow atlas's mathematics: which of the frame's lights cast, and the four face cameras each renders
// through. Pure, no GL. The tile layout is ShadowMapTargets.TileOrigin's (slot-major, row-major); TileRect below is an
// INDEPENDENT derivation of the same layout in UV units, and a test pins the two to each other.
//
// Faces: 0 = +X, 1 = -X, 2 = +Z, 3 = -Z. Each is a perspective frustum from the light, widened to
// ShadowSettings.FaceFovRadians so the 90 degree cone lands inside the tile minus its gutter. The polar cones (+-Y) are
// not rendered, and the lookup treats them as lit.
//
// THE LOOKUP CONTRACT (what fireShadows.glsl mirrors). For a fragment at offset d = fragPos - lightPos with
// a = abs(d), when a.y < max(a.x, a.z) the face is chosen by the major horizontal axis and the lateral coordinate is
// d projected on THAT FACE'S right axis, which is the right axis of a plain Matrix4.LookAt down the face forward with
// +Y up (Right below):
//     face 0 (+X): ndc = ( d.z, d.y) / a.x      face 1 (-X): ndc = (-d.z, d.y) / a.x
//     face 2 (+Z): ndc = (-d.x, d.y) / a.z      face 3 (-Z): ndc = ( d.x, d.y) / a.z
// and the stored depth is the perspective depth of `major` (see Depth).
//
// WHY THE PER-FACE SIGN RATHER THAN A MIRRORED CAMERA. A sign-free lookup (ndc = (d.z, d.y) / a.x for BOTH x faces and
// (d.x, d.y) / a.z for BOTH z faces) is achievable, but only by building the -X and +Z cameras with a right axis of
// +Z and +X respectively, which makes those two view matrices MIRRORED: determinant -1. A determinant-negative view
// matrix silently inverts triangle winding, so front faces come out as back faces in exactly two of the sixteen tiles.
// Today's caster pass renders depth only with culling off and does not care — but front-face culling in the caster
// pass is the standard peter-panning fix, and someone will plausibly add it; it would then quietly cull the wrong
// faces in those two tiles. So every face camera here is a plain right-handed LookAt and the four signs live in the
// GLSL, where they are visible. FireShadowList.FaceForward is the one definition of the forwards, and
// FireShadowAtlasAgreementTests pins FaceView against LookAt of it for all four faces.
public static class FireShadowAtlas
{
    public const int FaceCount = ShadowSettings.FacesPerLight;

    public const int PositiveX = 0;
    public const int NegativeX = 1;
    public const int PositiveZ = 2;
    public const int NegativeZ = 3;

    // The light's face planes. Past Radius the windowed falloff of pointLights.glsl is exactly 0, so clipping at
    // 1.15 x Radius is invisible, and the pass and the lookup both derive far from the same flickered radius.
    //
    // THE NEAR PLANE IS DERIVED FROM THE FLAME'S WANDER, because FillSlots renders every face from the FLICKERED
    // position. A surface d from the flame's BASE position is therefore tested against d minus the wander's component
    // along that face's axis, and the face axes are +-X and +-Z, so that component swings over
    // +-FireFlicker.OffsetLateral. Any surface in [NearPlane - OffsetLateral, NearPlane + OffsetLateral] enters and
    // leaves the frustum as the flame wanders and flips between occluding and lit at the wander's own frequency.
    //
    // That band is 2 x OffsetLateral wide wherever the near plane sits, so the plane cannot remove it; it only decides
    // where the band sits. Held down against zero, the only surfaces left inside it are surfaces the flame passes
    // through, whose shadow is degenerate however the pass is set up. Hence a near plane that is a small fraction of
    // the swing - WanderBandFraction, the share of the band the near plane is allowed to add - rather than, as it was
    // when the wander was two centimetres, several times it.
    //
    // The cost is perspective depth precision, which the far/near ratio governs, and it is affordable here: one step of
    // the 24 bit depth buffer is z^2 (far - near) / (2^24 - 1) / (far near) world units, about 0.5 mm at the deepest far
    // plane the atlas uses, against a ConstantBiasWorld of 6 mm and a slope bias that is a hundred times that at the
    // distances a shadow is read at. Both halves are pinned by FireShadowAtlasLayoutTests.
    public const float WanderBandFraction = 0.125f;
    public const float NearPlane = FireFlicker.OffsetLateral * WanderBandFraction;
    public const float FarRadiusScale = 1.15f;

    public static float FarPlane(float radius) => radius * FarRadiusScale;

    /// <summary>The luminance a light is scored by: the same Rec. 709 luma the sky model measures brightness in.</summary>
    public static float Luminance(Vector3 color) => SkyModelKeyframes.Luminance(color);

    /// <summary>
    /// pointLights.glsl's window, line for line: s = d^2 / r^2; f = clamp(1 - s, 0, 1); f = f * f / (1 + 1.5 s).
    /// It is exactly 0 at and beyond the radius. A non-positive radius (a dead light) contributes nothing.
    /// </summary>
    public static float WindowedFalloff(float distance, float radius)
    {
        if (!(radius > 0f))
        {
            return 0f;
        }

        float s = distance * distance / (radius * radius);
        float f = Math.Clamp(1f - s, 0f, 1f);
        return f * f / (1f + 1.5f * s);
    }

    /// <summary>A light's contribution at the focus: luminance times the windowed falloff of its distance.</summary>
    public static float Score(Vector3 color, Vector3 position, float radius, Vector3 focus) =>
        Luminance(color) * WindowedFalloff((position - focus).Length, radius);

    /// <summary>
    /// Picks up to <paramref name="maxSlots"/> entries of the frame's light list, highest score first, and writes their
    /// LIST INDICES to <paramref name="slots"/>. Ties keep the lower list index (the order is stable like LightSelection's),
    /// so a light does not swap slots with an equally bright one from frame to frame. Picks from the list the shaders
    /// light with, never from LightCollector, so the shadowing set cannot disagree with the lit set. Returns the count.
    /// A light with no score still takes a free slot (the score is a ranking, not a threshold): it is a visible light the
    /// camera selected, and over-lighting an unshadowed pool is the failure the design wants to avoid.
    /// </summary>
    public static int SelectSlots(in LightList list, Vector3 focus, int maxSlots, Span<int> slots)
    {
        int capacity = Math.Min(Math.Min(maxSlots, slots.Length), ShadowSettings.MaxCastingFireCount);
        int available = Math.Clamp(list.Count, 0, LightList.MaxLights);
        if (capacity <= 0 || available == 0)
        {
            return 0;
        }

        Span<float> scores = stackalloc float[ShadowSettings.MaxCastingFireCount];
        int count = 0;
        for (int i = 0; i < available; i++)
        {
            Vector4 positionRadius = list.PositionRadius[i];
            float score = Score(list.Color[i].Xyz, positionRadius.Xyz, positionRadius.W, focus);
            if (!float.IsFinite(score))
            {
                continue;
            }

            // Insertion into the descending kept list. A later light never displaces an earlier one at an equal score.
            int at = count;
            while (at > 0 && score > scores[at - 1])
            {
                at--;
            }

            if (at >= capacity)
            {
                continue;
            }

            int last = Math.Min(count, capacity - 1);
            for (int j = last; j > at; j--)
            {
                scores[j] = scores[j - 1];
                slots[j] = slots[j - 1];
            }

            scores[at] = score;
            slots[at] = i;
            count = Math.Min(count + 1, capacity);
        }

        return count;
    }

    /// <summary>
    /// Fills the fire half of <paramref name="frame"/> from the frame's selected lights: which entries, their FLICKERED
    /// world positions (the depth map's origin moves with the flame) and far planes (they breathe with the radius). The
    /// list entries already carry the flicker, applied when the lights were collected; nothing is sampled again here.
    /// </summary>
    public static void FillSlots(in LightList list, Vector3 focus, int maxSlots, float strength, ref ShadowFrame frame)
    {
        Span<int> chosen = stackalloc int[ShadowSettings.MaxCastingFireCount];
        int count = SelectSlots(in list, focus, maxSlots, chosen);
        Span<FireShadowSlot> fires = frame.Fires;
        for (int s = 0; s < count; s++)
        {
            Vector4 positionRadius = list.PositionRadius[chosen[s]];
            fires[s] = new FireShadowSlot
            {
                LightIndex = chosen[s],
                Position = positionRadius.Xyz,
                Near = NearPlane,
                Far = FarPlane(positionRadius.W),
                Strength = strength,
            };
        }

        for (int s = count; s < fires.Length; s++)
        {
            fires[s] = default;
        }

        frame.FireCount = count;
    }

    /// <summary>
    /// Direction the face looks along. Delegates to <see cref="FireShadowList.FaceForward"/>, the one definition, so the
    /// caster pass and the lookup block can never disagree about which axis a tile holds.
    /// </summary>
    public static Vector3 Forward(int face) => FireShadowList.FaceForward(face);

    /// <summary>
    /// The world axis that is the face's screen-right: cross(+Y, -Forward), the right axis a plain LookAt produces,
    /// and the lateral axis of the lookup contract above. Derived, not chosen - FaceView is the LookAt itself.
    /// </summary>
    public static Vector3 Right(int face) => Vector3.Normalize(Vector3.Cross(Vector3.UnitY, -Forward(face)));

    /// <summary>
    /// World to face-view: a plain right-handed LookAt from the light down the face forward with +Y up (row-vector
    /// convention, p * M, view space looking down -Z). See the per-face sign note at the top of the file for why this is
    /// LookAt and not a basis chosen to make the GLSL sign-free.
    /// </summary>
    public static Matrix4 FaceView(Vector3 lightPos, int face) =>
        Matrix4.LookAt(lightPos, lightPos + Forward(face), Vector3.UnitY);

    /// <summary>The face projection: square, widened to FaceFovRadians so the 90 degree cone sits inside the gutter.</summary>
    public static Matrix4 FaceProjection(float near, float far) =>
        Matrix4.CreatePerspectiveFieldOfView(ShadowSettings.FaceFovRadians, 1f, near, far);

    /// <summary>The face a direction falls in, or -1 inside a polar cone (|d.y| is the major axis, or d is zero): unshadowed.</summary>
    public static int FaceOf(Vector3 d)
    {
        Vector3 a = new(MathF.Abs(d.X), MathF.Abs(d.Y), MathF.Abs(d.Z));
        if (a.Y >= MathF.Max(a.X, a.Z))
        {
            return -1;
        }

        return a.X > a.Z ? (d.X > 0f ? PositiveX : NegativeX) : (d.Z > 0f ? PositiveZ : NegativeZ);
    }

    /// <summary>
    /// The lookup contract's tile-local NDC of an offset (xy in -1..1 across the 90 degree cone) and its major distance.
    /// The lateral coordinate is d on the face's Right axis, which carries the per-face sign fireShadows.glsl spells out.
    /// </summary>
    public static (Vector2 Ndc, float Major) FaceNdc(int face, Vector3 d)
    {
        bool xFace = face is PositiveX or NegativeX;
        float major = xFace ? MathF.Abs(d.X) : MathF.Abs(d.Z);
        Vector2 across = new(Vector3.Dot(d, Right(face)), d.Y);
        return (across / major, major);
    }

    /// <summary>The stored (0..1) depth of a surface <paramref name="major"/> in front of a face with these planes.</summary>
    public static float Depth(float major, float near, float far)
    {
        float ndc = ((far + near) / (far - near)) + ((2f * far * near / (far - near)) / -major);
        return (0.5f * ndc) + 0.5f;
    }

    /// <summary>
    /// The tile's rectangle in atlas UV units: x, y the lower-left corner and z, w the size. Slot-major, row-major in the
    /// GridSide x GridSide grid, derived here independently of ShadowMapTargets.TileOrigin and pinned equal to it.
    /// </summary>
    public static Vector4 TileRect(int slot, int face)
    {
        if ((uint)slot >= ShadowSettings.MaxCastingFireCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        if ((uint)face >= FaceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(face));
        }

        int index = (slot * FaceCount) + face;
        int column = index % ShadowSettings.GridSide;
        int row = index / ShadowSettings.GridSide;
        float size = 1f / ShadowSettings.GridSide;
        return new Vector4(column * size, row * size, size, size);
    }
}
