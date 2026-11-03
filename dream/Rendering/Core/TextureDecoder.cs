using OpenTK.Graphics.OpenGL4;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;

namespace Dream.Rendering.Core;

// RGBA8 pixels, rows already flipped vertically to match OpenGL's bottom-left origin.
public sealed record DecodedTexture(int Width, int Height, byte[] Rgba);

// Splits texture loading into a CPU decode, which any thread may run, and a GL upload,
// which only the render thread may run.
public static class TextureDecoder
{
    public static DecodedTexture DecodeRgbaFlipped(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return DecodeRgbaFlipped(stream);
    }

    public static DecodedTexture DecodeRgbaFlipped(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using Image<Rgba32> image = Image.Load<Rgba32>(stream);
        image.Mutate(operation => operation.Flip(FlipMode.Vertical));

        byte[] pixelData = new byte[4 * image.Width * image.Height];
        image.CopyPixelDataTo(pixelData);
        return new DecodedTexture(image.Width, image.Height, pixelData);
    }

    // RGBA8 pixels in file order (first row is the top row), straight alpha, no flip and no
    // premultiply. The UI atlas uses this: its UVs have a top-left origin, so no flip is needed.
    public static DecodedTexture DecodeRgba(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return DecodeRgba(stream);
    }

    public static DecodedTexture DecodeRgba(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using Image<Rgba32> image = Image.Load<Rgba32>(stream);
        byte[] pixelData = new byte[4 * image.Width * image.Height];
        image.CopyPixelDataTo(pixelData);
        return new DecodedTexture(image.Width, image.Height, pixelData);
    }

    // Uploads with ClampToEdge, linear magnification and (when maxMipLevel > 0) a trilinear
    // minification filter over a mip chain capped at maxMipLevel. maxMipLevel 0 is plain Linear.
    // Leaves the new texture bound to the active unit; callers restore their own binding.
    public static int UploadTexture2DClamped(DecodedTexture texture, int maxMipLevel)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentOutOfRangeException.ThrowIfNegative(maxMipLevel);
        GLUtils.AssertRenderThread("TextureDecoder.UploadTexture2DClamped");

        int handle = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, handle);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba,
            texture.Width,
            texture.Height,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            texture.Rgba);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)(maxMipLevel > 0 ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, maxMipLevel);
        if (maxMipLevel > 0)
        {
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        }

        return handle;
    }

    public static int UploadTexture2D(DecodedTexture texture, bool generateMipmaps)
    {
        ArgumentNullException.ThrowIfNull(texture);
        GLUtils.AssertRenderThread("TextureDecoder.UploadTexture2D");

        int handle = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, handle);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba,
            texture.Width,
            texture.Height,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            texture.Rgba);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)(generateMipmaps ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear));
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        // Terrain UVs run 0..cellSize, one repeat per voxel, so tiling is explicit rather than a GL default.
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        if (generateMipmaps)
        {
            if (IsPowerOfTwo(texture.Width) && IsPowerOfTwo(texture.Height))
            {
                // GenerateMipmap is driver defined and may clamp at the edge, which seams a tiling texture at
                // coarse levels. A 2x2 box on a power-of-two level needs no edge handling at all.
                UploadBoxMips(texture);
            }
            else
            {
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            }
        }

        return handle;
    }

    public static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    // One 2x2 box reduction of RGBA8 pixels; width and height are the source size and must be even or 1.
    public static byte[] BoxReduce(byte[] src, int width, int height)
    {
        int w = Math.Max(1, width / 2);
        int h = Math.Max(1, height / 2);
        int sx = width > 1 ? 2 : 1;
        int sy = height > 1 ? 2 : 1;
        byte[] dst = new byte[4 * w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < sy; dy++)
                    {
                        for (int dx = 0; dx < sx; dx++)
                        {
                            sum += src[(4 * (((y * sy) + dy) * width + (x * sx) + dx)) + c];
                        }
                    }

                    dst[(4 * ((y * w) + x)) + c] = (byte)((sum + ((sx * sy) / 2)) / (sx * sy));
                }
            }
        }

        return dst;
    }

    private static void UploadBoxMips(DecodedTexture texture)
    {
        byte[] level = texture.Rgba;
        int w = texture.Width;
        int h = texture.Height;
        int index = 0;
        while (w > 1 || h > 1)
        {
            level = BoxReduce(level, w, h);
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
            index++;
            GL.TexImage2D(
                TextureTarget.Texture2D,
                index,
                PixelInternalFormat.Rgba,
                w,
                h,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                level);
        }
    }
}
