using System.IO;
using Dream.Rendering.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Dream.Tests.Rendering.Core;

public class TextureDecoderTests
{
    private const int Width = 2;
    private const int Height = 3;

    [Fact]
    public void DecodeRgbaFlipped_FromStream_FlipsRowsVertically()
    {
        using MemoryStream png = CreatePng();

        DecodedTexture decoded = TextureDecoder.DecodeRgbaFlipped(png);

        AssertFlipped(decoded);
    }

    [Fact]
    public void DecodeRgbaFlipped_FromPath_MatchesStreamDecode()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dream-texture-decoder-{Guid.NewGuid():N}.png");
        try
        {
            using (MemoryStream png = CreatePng())
            {
                File.WriteAllBytes(path, png.ToArray());
            }

            DecodedTexture decoded = TextureDecoder.DecodeRgbaFlipped(path);

            AssertFlipped(decoded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeRgbaFlipped_RunsOffTheCallingThread()
    {
        byte[] bytes;
        using (MemoryStream png = CreatePng())
        {
            bytes = png.ToArray();
        }

        DecodedTexture decoded = await Task.Run(() =>
        {
            using MemoryStream stream = new(bytes);
            return TextureDecoder.DecodeRgbaFlipped(stream);
        });

        AssertFlipped(decoded);
    }

    // Source row y is filled with (y * 40 + x, 200 - y, x * 100, 255 - y).
    private static Rgba32 SourcePixel(int x, int y) =>
        new((byte)(y * 40 + x), (byte)(200 - y), (byte)(x * 100), (byte)(255 - y));

    private static MemoryStream CreatePng()
    {
        using Image<Rgba32> image = new(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                image[x, y] = SourcePixel(x, y);
            }
        }

        MemoryStream stream = new();
        image.SaveAsPng(stream);
        stream.Position = 0;
        return stream;
    }

    private static void AssertFlipped(DecodedTexture decoded)
    {
        Assert.Equal(Width, decoded.Width);
        Assert.Equal(Height, decoded.Height);
        Assert.Equal(Width * Height * 4, decoded.Rgba.Length);

        for (int y = 0; y < Height; y++)
        {
            int sourceRow = Height - 1 - y;
            for (int x = 0; x < Width; x++)
            {
                Rgba32 expected = SourcePixel(x, sourceRow);
                int offset = (y * Width + x) * 4;
                Assert.Equal(expected.R, decoded.Rgba[offset]);
                Assert.Equal(expected.G, decoded.Rgba[offset + 1]);
                Assert.Equal(expected.B, decoded.Rgba[offset + 2]);
                Assert.Equal(expected.A, decoded.Rgba[offset + 3]);
            }
        }
    }
}
