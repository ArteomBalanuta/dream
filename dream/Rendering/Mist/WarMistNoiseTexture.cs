using OpenTK.Graphics.OpenGL4;
using Dream.Rendering.Core;

namespace Dream.Rendering.Mist;

// The war mist's noise: a tileable 128 x 128 two-channel texture of smooth fractal value noise,
// generated here from a fixed seed (no asset file, and the same bytes on every run and machine).
// Red and green are two independent fields; the fragment shader reads one of each at different
// scales and speeds. The lattice wraps at every octave, so the texture repeats without a seam.
public static class WarMistNoiseTexture
{
    public const int Size = 128;
    public const uint Seed = 0x6D157u;

    // Lattice cells across the tile for each octave; every one divides Size, so the octave tiles.
    private static readonly int[] OctaveCells = [4, 8, 16, 32];

    // Size x Size x 2 bytes, row-major, red then green per texel.
    public static byte[] Generate(uint seed = Seed)
    {
        byte[] bytes = new byte[Size * Size * 2];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int offset = (y * Size + x) * 2;
                bytes[offset] = ToByte(Fbm(x, y, seed));
                bytes[offset + 1] = ToByte(Fbm(x, y, seed ^ 0x9E3779B9u));
            }
        }

        return bytes;
    }

    // Uploads the bytes as a repeating, linearly filtered RG8 texture and returns its handle. Render thread only.
    public static int Upload(byte[] rg)
    {
        GLUtils.AssertRenderThread("WarMistNoiseTexture.Upload");
        int previousBinding = GL.GetInteger(GetPName.TextureBinding2D);
        int previousAlignment = GL.GetInteger(GetPName.UnpackAlignment);
        int handle = GL.GenTexture();
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, handle);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rg8, Size, Size, 0, PixelFormat.Rg, PixelType.UnsignedByte, rg);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        }
        catch
        {
            GL.DeleteTexture(handle);
            throw;
        }
        finally
        {
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, previousAlignment);
            GL.BindTexture(TextureTarget.Texture2D, previousBinding);
        }

        return handle;
    }

    private static float Fbm(int x, int y, uint seed)
    {
        float sum = 0f;
        float total = 0f;
        float amplitude = 1f;
        for (int octave = 0; octave < OctaveCells.Length; octave++)
        {
            int cells = OctaveCells[octave];
            float scale = cells / (float)Size;
            sum += amplitude * ValueNoise(x * scale, y * scale, cells, seed + (uint)octave * 0x85EBCA6Bu);
            total += amplitude;
            amplitude *= 0.5f;
        }

        return sum / total;
    }

    // Smoothly interpolated lattice noise whose lattice wraps every `cells`.
    private static float ValueNoise(float x, float y, int cells, uint seed)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        float fx = x - x0;
        float fy = y - y0;
        float u = fx * fx * (3f - 2f * fx);
        float v = fy * fy * (3f - 2f * fy);
        int xa = Wrap(x0, cells);
        int xb = Wrap(x0 + 1, cells);
        int ya = Wrap(y0, cells);
        int yb = Wrap(y0 + 1, cells);
        float top = Lerp(Lattice(xa, ya, seed), Lattice(xb, ya, seed), u);
        float bottom = Lerp(Lattice(xa, yb, seed), Lattice(xb, yb, seed), u);
        return Lerp(top, bottom, v);
    }

    private static float Lattice(int x, int y, uint seed)
    {
        uint h = seed;
        h ^= (uint)x * 0x27D4EB2Fu;
        h = (h << 13) | (h >> 19);
        h *= 0x165667B1u;
        h ^= (uint)y * 0x9E3779B1u;
        h ^= h >> 15;
        h *= 0x85EBCA77u;
        h ^= h >> 13;
        h *= 0xC2B2AE3Du;
        h ^= h >> 16;
        return (h & 0xFFFFFFu) / (float)0x1000000;
    }

    private static int Wrap(int value, int cells) => ((value % cells) + cells) % cells;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
