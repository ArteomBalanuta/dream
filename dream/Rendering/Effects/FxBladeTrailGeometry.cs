using OpenTK.Mathematics;

namespace Dream.Rendering.Effects;

// The path of the Cleave blade trail in the caster's frame, and the trail cell's painted extent. The player's
// "cleave" clip is authored so its blade tip runs along this path through the active window of MeleeArcSwing
// (PlayerCleaveClipTests measures the shipped clip against it); FxSimulation lays the trail on it; gen_particles.py
// paints the trail cell to CellPlateauFraction and FxBladeTrailCellTests measures the shipped PNG against it.
// Angles follow the effect convention: 0 is the caster's aim, negative is the caster's left. The cut runs from the
// left end of the arc to the right end at a constant angular speed over the active window, falling from
// TipHeightStartMetres to TipHeightEndMetres: a high-left to low-right cut.
public static class FxBladeTrailGeometry
{
    // The blade sweeps the skill's own arc (PaintedArcDegrees is pinned to every MeleeArc skill's arcDegrees).
    public const float HalfSweepDegrees = FxArcGeometry.PaintedArcDegrees / 2f;

    // Horizontal distance from the caster's vertical axis to the blade tip, metres.
    public const float TipRadiusMetres = 1.0f;

    // Height of the blade tip above the caster's feet at the left end and at the right end of the cut, metres.
    public const float TipHeightStartMetres = 1.4f;
    public const float TipHeightEndMetres = 1.0f;

    // The reference blade the clip is measured with: fist to tip, metres.
    public const float BladeReachMetres = 0.8f;

    // The trail cell paints a full-brightness band along local +X out to this fraction of the half-cell and fades
    // to nothing before the dark outer ring.
    public const float CellPlateauFraction = 20f / 32f;

    // The tip's sweep angle, radians, at `progress` through the active window (0 = left end, 1 = right end).
    public static float TipAngleRadians(float progress) =>
        MathHelper.DegreesToRadians(HalfSweepDegrees) * ((2f * Math.Clamp(progress, 0f, 1f)) - 1f);

    // The tip's height above the feet, metres, at `progress` through the active window.
    public static float TipHeightMetres(float progress) =>
        TipHeightStartMetres + ((TipHeightEndMetres - TipHeightStartMetres) * Math.Clamp(progress, 0f, 1f));
}
