namespace Dream.Rendering.Effects;

// Where the Cleave crescent's painted edge sits in its atlas cell, so the effect can be placed to match
// the skill. The atlas cell is 64 px with 28 px drawn and the outer 3 px dark, so the painted edge cannot
// reach past about 24 px of the 32 px half-cell. gen_particles.py paints the crescent with these numbers
// (ARC_OUTER_PX, ARC_SPAN_DEG); check_particles.py and FxArcGeometryTests measure the shipped PNG
// against them, and a test pins PaintedArcDegrees to every shipped MeleeArc skill's arcDegrees.
public static class FxArcGeometry
{
    // The angle the painted crescent spans, +-60 degrees about the caster's forward.
    public const float PaintedArcDegrees = 120f;

    // The painted outer edge as a fraction of the quad's half-size (24 px of the 32 px half-cell).
    public const float OuterRadiusFraction = 24f / 32f;

    // The quad half-size that puts the painted outer edge exactly `range` metres from the caster.
    public static float HalfSizeForRange(float range) => range / OuterRadiusFraction;
}
