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
        if (generateMipmaps)
        {
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        }

        return handle;
    }
}
