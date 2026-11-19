using Dream.Assets;
using Dream.Rendering.Core;
using Dream.Rendering.Effects;
using Dream.Simulation.Content;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

// The Cleave crescent is checked as shipped: the painted PNG is measured against FxArcGeometry, so the
// numbers the simulation places the quad with are the numbers the picture was painted with. In the quad,
// local +X is the caster's forward and local +Y (the top of the image) is the caster's right.
public class FxArcGeometryTests
{
    private const int Columns = 8;
    private const int Cell = 64;
    private const int ArcFrame = (int)FxFrame.Arc;
    private const float CellHalf = Cell / 2f;

    private static readonly Lazy<DecodedTexture> Atlas = new(() => TextureDecoder.DecodeRgba(AssetPaths.Ui("particles/particles.png")));

    private static byte Alpha(int px, int py)
    {
        int x = ArcFrame % Columns * Cell + px;
        int y = ArcFrame / Columns * Cell + py;
        return Atlas.Value.Rgba[(y * Atlas.Value.Width + x) * 4 + 3];
    }

    // Radius and angle of a cell pixel's centre from the cell centre. Angle in degrees, positive toward the
    // top of the image (the caster's right), zero along +X.
    private static (float Radius, float Degrees) Polar(int px, int py)
    {
        float x = px + 0.5f - CellHalf;
        float y = py + 0.5f - CellHalf;
        return (MathF.Sqrt(x * x + y * y), MathHelperDegrees(MathF.Atan2(-y, x)));
    }

    private static float MathHelperDegrees(float radians) => radians * (180f / MathF.PI);

    // The outer edge: along the forward axis, the largest radius whose alpha is still half of the peak there.
    internal static float MeasureOuterRadiusPx(Func<int, int, byte> alpha)
    {
        float peak = 0f;
        float[] profile = new float[400];
        for (int i = 0; i < profile.Length; i++)
        {
            float radius = 14f + i * 0.03f;
            float sum = 0f;
            int n = 0;
            for (int py = 0; py < Cell; py++)
            {
                for (int px = 0; px < Cell; px++)
                {
                    (float r, float deg) = Polar(px, py);
                    if (MathF.Abs(deg) < 8f && MathF.Abs(r - radius) < 0.5f)
                    {
                        sum += alpha(px, py);
                        n++;
                    }
                }
            }

            profile[i] = n > 0 ? sum / n : 0f;
            peak = MathF.Max(peak, profile[i]);
        }

        for (int i = profile.Length - 1; i >= 0; i--)
        {
            if (profile[i] >= peak / 2f)
            {
                return 14f + i * 0.03f;
            }
        }

        return 0f;
    }

    // The angular extent of what is painted near the edge (alpha at least 15 % of the cell's peak).
    internal static (float Left, float Right) MeasureSpanDegrees(Func<int, int, byte> alpha, float outerPx)
    {
        int peak = 0;
        for (int py = 0; py < Cell; py++)
        {
            for (int px = 0; px < Cell; px++)
            {
                peak = Math.Max(peak, alpha(px, py));
            }
        }

        float left = float.MaxValue;
        float right = float.MinValue;
        for (int py = 0; py < Cell; py++)
        {
            for (int px = 0; px < Cell; px++)
            {
                (float r, float deg) = Polar(px, py);
                if (r > outerPx - 5f && r < outerPx - 0.5f && alpha(px, py) >= 0.15f * peak)
                {
                    left = MathF.Min(left, deg);
                    right = MathF.Max(right, deg);
                }
            }
        }

        return (left, right);
    }

    private static float MeanAlphaNearTip(Func<int, int, byte> alpha, float outerPx, float centreDegrees)
    {
        float sum = 0f;
        int n = 0;
        for (int py = 0; py < Cell; py++)
        {
            for (int px = 0; px < Cell; px++)
            {
                (float r, float deg) = Polar(px, py);
                if (r > outerPx - 5f && r < outerPx - 0.5f && MathF.Abs(deg - centreDegrees) < 5f)
                {
                    sum += alpha(px, py);
                    n++;
                }
            }
        }

        return n > 0 ? sum / n : 0f;
    }

    [Fact]
    public void The_Painted_Outer_Edge_Is_The_Geometry_Fraction_Of_The_Half_Cell_Within_One_Pixel()
    {
        float measured = MeasureOuterRadiusPx(Alpha);

        Assert.InRange(measured, FxArcGeometry.OuterRadiusFraction * CellHalf - 1f, FxArcGeometry.OuterRadiusFraction * CellHalf + 1f);
    }

    [Fact]
    public void The_Painted_Crescent_Spans_The_Geometry_Angle_Within_Six_Degrees_And_Is_Centred_On_Forward()
    {
        float outer = FxArcGeometry.OuterRadiusFraction * CellHalf;
        (float left, float right) = MeasureSpanDegrees(Alpha, outer);

        Assert.InRange(right, FxArcGeometry.PaintedArcDegrees / 2f - 6f, FxArcGeometry.PaintedArcDegrees / 2f + 6f);
        Assert.InRange(left, -FxArcGeometry.PaintedArcDegrees / 2f - 6f, -FxArcGeometry.PaintedArcDegrees / 2f + 6f);
        Assert.InRange(right - left, FxArcGeometry.PaintedArcDegrees - 6f, FxArcGeometry.PaintedArcDegrees + 6f);
    }

    [Fact]
    public void The_Crescent_Is_Dim_At_The_Casters_Left_Tip_And_Bright_At_The_Right_Tip()
    {
        float outer = FxArcGeometry.OuterRadiusFraction * CellHalf;
        float half = FxArcGeometry.PaintedArcDegrees / 2f;

        float leftTip = MeanAlphaNearTip(Alpha, outer, -half + 8f);
        float rightTip = MeanAlphaNearTip(Alpha, outer, half - 8f);

        // The top of the image is the caster's right: it is the bright end of the left-to-right sweep.
        Assert.True(leftTip > 0f);
        Assert.True(rightTip > leftTip * 1.5f, $"left tip {leftTip}, right tip {rightTip}");
    }

    [Fact]
    public void The_Measurement_Fails_On_A_Shrunk_A_Narrowed_And_A_Mirrored_Crescent()
    {
        // Synthetic negatives: the checks above must be able to fail.
        byte Shrunk(int px, int py) => Polar(px, py).Radius > 21f ? (byte)0 : Alpha(px, py);
        byte Narrow(int px, int py) => MathF.Abs(Polar(px, py).Degrees) > 40f ? (byte)0 : Alpha(px, py);
        byte Mirrored(int px, int py) => Alpha(px, Cell - 1 - py);
        float outer = FxArcGeometry.OuterRadiusFraction * CellHalf;

        Assert.True(MathF.Abs(MeasureOuterRadiusPx(Shrunk) - outer) > 1f);
        (float left, float right) = MeasureSpanDegrees(Narrow, outer);
        Assert.True(right < FxArcGeometry.PaintedArcDegrees / 2f - 6f && left > -FxArcGeometry.PaintedArcDegrees / 2f + 6f);
        float half = FxArcGeometry.PaintedArcDegrees / 2f;
        Assert.True(MeanAlphaNearTip(Mirrored, outer, half - 8f) < MeanAlphaNearTip(Mirrored, outer, -half + 8f));
    }

    [Fact]
    public void The_Painted_Arc_Equals_Every_Shipped_Melee_Arc_Skills_Arc_Degrees()
    {
        SkillDefinition[] arcs = [.. ContentCatalog.Shipped.Skills.Where(s => s.Kind == SkillKind.MeleeArc)];

        Assert.NotEmpty(arcs);
        foreach (SkillDefinition skill in arcs)
        {
            Assert.Equal(FxArcGeometry.PaintedArcDegrees, skill.ArcDegrees);
        }
    }

    [Fact]
    public void The_Visual_Edge_Lands_On_The_Hit_Range_For_Every_Shipped_Melee_Arc_Skill()
    {
        float measuredFraction = MeasureOuterRadiusPx(Alpha) / CellHalf;

        foreach (SkillDefinition skill in ContentCatalog.Shipped.Skills.Where(s => s.Kind == SkillKind.MeleeArc))
        {
            float half = FxArcGeometry.HalfSizeForRange(skill.Range);
            float edgeMetres = half * measuredFraction;

            // One painted pixel is half / 32 metres on the ground.
            Assert.InRange(edgeMetres, skill.Range - half / CellHalf, skill.Range + half / CellHalf);
        }

        Assert.Equal(3.5f, FxArcGeometry.HalfSizeForRange(3.5f) * FxArcGeometry.OuterRadiusFraction, 4);
    }
}
