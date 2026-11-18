using System.IO;
using Dream.Assets;
using Dream.Rendering.Core;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

// The particle atlas is checked as shipped: size, grid, zero margins, and the alpha rules per frame
// group. Literal numbers on purpose: they are the contract with the renderer, not derived from it.
public class FxAtlasAssetTests
{
    private const int Columns = 8;
    private const int Rows = 4;
    private const int Cell = 64;
    private const int Margin = 4;
    private const string RelativePath = "particles/particles.png";

    private static readonly Lazy<DecodedTexture> Atlas = new(() => TextureDecoder.DecodeRgba(AssetPaths.Ui(RelativePath)));

    private static int Index(int frame, int px, int py) =>
        (((frame / Columns * Cell) + py) * Columns * Cell + (frame % Columns * Cell) + px) * 4;

    private static byte Channel(int frame, int px, int py, int channel) => Atlas.Value.Rgba[Index(frame, px, py) + channel];

    private static IEnumerable<(int Px, int Py)> Pixels(bool marginOnly)
    {
        for (int py = 0; py < Cell; py++)
        {
            for (int px = 0; px < Cell; px++)
            {
                bool inside = px >= Margin && px < Cell - Margin && py >= Margin && py < Cell - Margin;
                if (inside != marginOnly)
                {
                    yield return (px, py);
                }
            }
        }
    }

    private static int MaxAlpha(int frame) => Pixels(false).Max(p => Channel(frame, p.Px, p.Py, 3));

    private static int MaxRgb(int frame) => Pixels(false).Max(p => Math.Max(Channel(frame, p.Px, p.Py, 0), Math.Max(Channel(frame, p.Px, p.Py, 1), Channel(frame, p.Px, p.Py, 2))));

    [Fact]
    public void Atlas_File_Exists_And_Is_512_By_256()
    {
        Assert.True(File.Exists(AssetPaths.Ui(RelativePath)));
        Assert.Equal(512, Atlas.Value.Width);
        Assert.Equal(256, Atlas.Value.Height);
        Assert.Equal(512 * 256 * 4, Atlas.Value.Rgba.Length);
        Assert.Equal(Columns * Cell, Atlas.Value.Width);
        Assert.Equal(Rows * Cell, Atlas.Value.Height);
    }

    [Fact]
    public void Descriptor_Matches_The_Grid()
    {
        string json = File.ReadAllText(AssetPaths.Ui("particles/particles.json"));
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement root = doc.RootElement;

        Assert.Equal("particles.png", root.GetProperty("image").GetString());
        Assert.Equal(Columns, root.GetProperty("columns").GetInt32());
        Assert.Equal(Rows, root.GetProperty("rows").GetInt32());
        Assert.Equal(Cell, root.GetProperty("cell").GetInt32());
        Assert.Equal(Margin, root.GetProperty("margin").GetInt32());
        Assert.True(root.GetProperty("yDown").GetBoolean());
        Assert.Equal("premultiplied", root.GetProperty("alpha").GetString());
        Assert.Equal(31, root.GetProperty("frames").GetArrayLength());
        Assert.Equal(31, root.GetProperty("reserved")[0].GetInt32());
        Assert.Equal(31, root.GetProperty("reserved")[1].GetInt32());
    }

    [Fact]
    public void Every_Cell_Margin_Is_Fully_Zero()
    {
        for (int frame = 0; frame < Columns * Rows; frame++)
        {
            foreach ((int px, int py) in Pixels(marginOnly: true))
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    Assert.True(Channel(frame, px, py, channel) == 0, $"frame {frame} margin pixel ({px},{py}) channel {channel}");
                }
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(24)]
    [InlineData(25)]
    public void Additive_Frames_Have_Zero_Alpha_And_Visible_Emission(int frame)
    {
        Assert.Equal(0, MaxAlpha(frame));
        Assert.True(MaxRgb(frame) >= 100, $"frame {frame} emission peaks at {MaxRgb(frame)}");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Ember_Frames_Are_Near_Neutral_And_Bright(int frame)
    {
        Assert.True(MaxRgb(frame) >= 200, $"frame {frame} peaks at {MaxRgb(frame)}");
        foreach ((int px, int py) in Pixels(marginOnly: false))
        {
            int r = Channel(frame, px, py, 0), g = Channel(frame, px, py, 1), b = Channel(frame, px, py, 2);
            Assert.True(Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 26, $"frame {frame} ({px},{py}) is tinted");
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void Dust_Frames_Peak_Near_215(int frame)
    {
        Assert.InRange(MaxRgb(frame), 190, 240);
    }

    [Theory]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    public void Flame_Frames_Stay_Under_245(int frame)
    {
        Assert.True(MaxRgb(frame) <= 245, $"frame {frame} peaks at {MaxRgb(frame)}");
    }

    [Theory]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    public void Flame_Frames_Have_Coverage_Alpha_Peaking_Near_0_8_With_Meaningful_Rgb(int frame)
    {
        // Alpha is scaled by the instance alpha: 0 at night (additive), 0.8 by day (occludes the sky).
        Assert.InRange(MaxAlpha(frame), 190, 215);
        Assert.True(MaxRgb(frame) >= 100, $"frame {frame} emission peaks at {MaxRgb(frame)}");
        foreach ((int px, int py) in Pixels(marginOnly: false))
        {
            int a = Channel(frame, px, py, 3);
            int rgb = Math.Max(Channel(frame, px, py, 0), Math.Max(Channel(frame, px, py, 1), Channel(frame, px, py, 2)));
            if (a > 8)
            {
                Assert.True(rgb * 2 >= a, $"frame {frame} ({px},{py}) rgb {rgb} is dark under alpha {a}");
            }
        }
    }

    [Theory]
    [InlineData(23)]
    [InlineData(26)]
    public void Arc_And_Ring_Frames_Are_Coverage_Alpha_Under_245_With_Meaningful_Rgb(int frame)
    {
        // Ground-aligned coverage frames, like the flame: additive at night, occluding the sky by day.
        Assert.InRange(MaxAlpha(frame), 190, 215);
        Assert.InRange(MaxRgb(frame), 100, 245);
        foreach ((int px, int py) in Pixels(marginOnly: false))
        {
            int a = Channel(frame, px, py, 3);
            int rgb = Math.Max(Channel(frame, px, py, 0), Math.Max(Channel(frame, px, py, 1), Channel(frame, px, py, 2)));
            if (a > 8)
            {
                Assert.True(rgb * 2 >= a, $"frame {frame} ({px},{py}) rgb {rgb} is dark under alpha {a}");
            }
        }
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    public void Bolt_Frames_Have_A_Hot_Core_Under_245(int frame)
    {
        Assert.InRange(MaxRgb(frame), 200, 245);
    }

    [Fact]
    public void Ring_Is_Dark_In_The_Centre()
    {
        Assert.True(Channel(26, Cell / 2, Cell / 2, 0) <= 8);
        Assert.True(Channel(26, Cell / 2, Cell / 2, 3) <= 8);
    }

    [Fact]
    public void Bolt_Variants_Differ()
    {
        bool differ = Pixels(marginOnly: false).Any(p => Channel(24, p.Px, p.Py, 0) != Channel(25, p.Px, p.Py, 0));
        Assert.True(differ);
    }

    [Fact]
    public void Blob_Is_Black_With_Soft_Coverage()
    {
        foreach ((int px, int py) in Pixels(marginOnly: false))
        {
            for (int channel = 0; channel < 3; channel++)
            {
                Assert.Equal(0, Channel(22, px, py, channel));
            }
        }

        Assert.True(MaxAlpha(22) >= 200, $"blob alpha peaks at {MaxAlpha(22)}");
        Assert.Equal(0, Channel(22, Margin, Cell / 2, 3));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    public void Halo_Frames_Have_No_Clipped_Plateau(int frame)
    {
        int max = Pixels(false).Max(p => (int)Channel(frame, p.Px, p.Py, 0));
        Assert.True(max < 255, $"frame {frame} reaches 255");
        int near = Pixels(false).Count(p => Channel(frame, p.Px, p.Py, 0) >= max - 3);
        Assert.True(near <= 16, $"frame {frame} has {near} texels within 3 of its peak {max}");
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void Ash_Frames_Cover_And_Stay_Dark(int frame)
    {
        Assert.True(MaxAlpha(frame) >= 90, $"frame {frame} alpha peaks at {MaxAlpha(frame)}");
        Assert.True(MaxRgb(frame) <= 90, $"frame {frame} RGB peaks at {MaxRgb(frame)}");

        long rgb = 0, alpha = 0;
        foreach ((int px, int py) in Pixels(marginOnly: false))
        {
            int a = Channel(frame, px, py, 3);
            if (a == 0)
            {
                continue;
            }

            alpha += a;
            rgb += Channel(frame, px, py, 0) + Channel(frame, px, py, 1) + Channel(frame, px, py, 2);
        }

        double lum = rgb / (3.0 * alpha);
        Assert.InRange(lum, 0.2, 0.4);
    }

    [Fact]
    public void Premultiplied_Frames_Never_Exceed_Their_Alpha()
    {
        for (int frame = 12; frame < 16; frame++)
        {
            foreach ((int px, int py) in Pixels(marginOnly: false))
            {
                int alpha = Channel(frame, px, py, 3);
                for (int channel = 0; channel < 3; channel++)
                {
                    Assert.True(Channel(frame, px, py, channel) <= alpha, $"frame {frame} ({px},{py}) channel {channel}");
                }
            }
        }
    }

    [Fact]
    public void Halo_Frames_Peak_At_The_Centre_And_Are_Dark_At_The_Edge()
    {
        foreach (int frame in new[] { 16, 17 })
        {
            int centre = Channel(frame, Cell / 2, Cell / 2, 0);
            int edge = Channel(frame, Margin, Cell / 2, 0);
            Assert.True(centre >= 100, $"frame {frame} centre {centre}");
            Assert.Equal(0, edge);
        }

        // The core variant is tighter: less of the cell is lit above 20/255.
        int Lit(int frame) => Pixels(false).Count(p => Channel(frame, p.Px, p.Py, 0) > 20);
        Assert.True(Lit(17) < Lit(16));
    }

    [Theory]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(30)]
    public void Hit_Marks_Are_Additive_Neutral_And_Capped(int frame)
    {
        Assert.Equal(0, MaxAlpha(frame));
        int peak = MaxRgb(frame);
        Assert.InRange(peak, 200, 245);
        foreach ((int px, int py) in Pixels(marginOnly: false))
        {
            Assert.True(Channel(frame, px, py, 0) == Channel(frame, px, py, 1) && Channel(frame, px, py, 1) == Channel(frame, px, py, 2), $"frame {frame} ({px},{py}) not neutral");
        }
    }

    [Fact]
    public void Hit_Marks_Differ_From_Each_Other()
    {
        for (int a = 27; a < 31; a++)
        {
            for (int b = a + 1; b < 31; b++)
            {
                Assert.True(Pixels(false).Any(p => Channel(a, p.Px, p.Py, 0) != Channel(b, p.Px, p.Py, 0)), $"frames {a} and {b} are identical");
            }
        }
    }

    [Fact]
    public void Reserved_Cells_Are_Empty()
    {
        for (int frame = 31; frame < Columns * Rows; frame++)
        {
            for (int py = 0; py < Cell; py++)
            {
                for (int px = 0; px < Cell; px++)
                {
                    for (int channel = 0; channel < 4; channel++)
                    {
                        Assert.True(Channel(frame, px, py, channel) == 0, $"frame {frame} ({px},{py})");
                    }
                }
            }
        }
    }
}
