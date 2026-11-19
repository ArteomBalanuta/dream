using Dream.Assets;
using Dream.Rendering.Core;
using Dream.Rendering.Effects;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

// The blade trail cell is checked as shipped: the painted PNG is measured against FxBladeTrailGeometry, so the
// plateau the simulation stretches segments by is the plateau the picture was painted with. Local +X is the
// band's length; the centre of the cell is the centre of the band.
public class FxBladeTrailCellTests
{
    private const int Columns = 8;
    private const int Cell = 64;
    private const int TrailFrame = (int)FxFrame.Trail;
    private const float CellHalf = Cell / 2f;

    private static readonly Lazy<DecodedTexture> Atlas = new(() => TextureDecoder.DecodeRgba(AssetPaths.Ui("particles/particles.png")));

    private static byte Channel(int px, int py, int channel)
    {
        int x = TrailFrame % Columns * Cell + px;
        int y = TrailFrame / Columns * Cell + py;
        return Atlas.Value.Rgba[(y * Atlas.Value.Width + x) * 4 + channel];
    }

    private static byte Max(int px, int py) => Math.Max(Channel(px, py, 0), Math.Max(Channel(px, py, 1), Channel(px, py, 2)));

    // Brightest of the two rows straddling the band's centre line, per column.
    private static int Centre(Func<int, int, byte> value, int px) => Math.Max(value(px, Cell / 2 - 1), value(px, Cell / 2));

    private static int Peak(Func<int, int, byte> value)
    {
        int peak = 0;
        for (int py = 0; py < Cell; py++)
        {
            for (int px = 0; px < Cell; px++)
            {
                peak = Math.Max(peak, value(px, py));
            }
        }

        return peak;
    }

    // The plateau's half-length: the farthest column centre, from the cell centre, still within 3% of the peak
    // along the centre line, measured on the +X side and the -X side. A flat run that is not symmetric is an error.
    internal static (float Right, float Left) MeasurePlateauHalfPx(Func<int, int, byte> value)
    {
        int peak = Peak(value);
        float right = 0f;
        float left = 0f;
        for (int px = 0; px < Cell; px++)
        {
            if (Centre(value, px) < peak * 0.97f)
            {
                continue;
            }

            float x = px + 0.5f - CellHalf;
            if (x > 0f)
            {
                right = MathF.Max(right, x);
            }
            else
            {
                left = MathF.Max(left, -x);
            }
        }

        return (right, left);
    }

    private static bool PlateauMatches(Func<int, int, byte> value)
    {
        float expected = FxBladeTrailGeometry.CellPlateauFraction * CellHalf;
        (float right, float left) = MeasurePlateauHalfPx(value);
        return MathF.Abs(right - expected) <= 1f && MathF.Abs(left - expected) <= 1f;
    }

    [Fact]
    public void Plateau_Edge_Is_Where_The_Geometry_Says()
    {
        Assert.True(PlateauMatches(Max), $"plateau half-length {MeasurePlateauHalfPx(Max)}, want {FxBladeTrailGeometry.CellPlateauFraction * CellHalf} +-1");
    }

    [Fact]
    public void Plateau_Is_Flat_And_The_Band_Is_Dark_Past_It()
    {
        int peak = Peak(Max);
        float expected = FxBladeTrailGeometry.CellPlateauFraction * CellHalf;
        for (int px = 0; px < Cell; px++)
        {
            float x = MathF.Abs(px + 0.5f - CellHalf);
            if (x <= expected - 1f)
            {
                Assert.True(Centre(Max, px) >= peak * 0.97f, $"column {px} dips below the plateau");
            }

            if (x >= 25f)
            {
                Assert.True(Centre(Max, px) <= peak * 0.02f, $"column {px} is still lit at 25 px");
            }
        }
    }

    [Fact]
    public void Band_Is_Additive_Neutral_And_Capped()
    {
        int peak = 0;
        for (int py = 0; py < Cell; py++)
        {
            for (int px = 0; px < Cell; px++)
            {
                Assert.Equal(0, Channel(px, py, 3));
                Assert.True(Channel(px, py, 0) == Channel(px, py, 1) && Channel(px, py, 1) == Channel(px, py, 2), $"({px},{py}) not neutral");
                peak = Math.Max(peak, Max(px, py));
            }
        }

        Assert.InRange(peak, 230, 245);
    }

    [Fact]
    public void Core_Row_Is_The_Brightest_And_The_Band_Is_Symmetric_Across()
    {
        int px = Cell / 2;
        int core = Math.Max(Max(px, Cell / 2 - 1), Max(px, Cell / 2));
        Assert.Equal(Peak(Max), core);
        for (int py = 0; py < Cell / 2; py++)
        {
            Assert.True(Max(px, Cell / 2 - 1 - py) == Max(px, Cell / 2 + py), $"row offset {py} is not mirrored");
        }
    }

    [Fact]
    public void Gate_Fails_On_A_Band_Shortened_To_14_Px()
    {
        Assert.True(PlateauMatches(Max));
        byte Short(int px, int py) => MathF.Abs(px + 0.5f - CellHalf) > 14f ? (byte)0 : Max(px, py);
        Assert.False(PlateauMatches(Short), "a 14 px band must not match the geometry's plateau");
    }
}
