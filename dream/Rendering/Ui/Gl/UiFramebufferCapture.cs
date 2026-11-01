using Dream.Rendering.Core;
using OpenTK.Graphics.OpenGL4;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Dream.Rendering.Ui.Gl;

// Reads the back buffer with glReadPixels and writes a PNG. Call on the render thread after drawing
// and before the buffer swap. Pack alignment and read buffer are restored.
public static class UiFramebufferCapture
{
    // RGBA8, top row first, alpha forced to 255 (the default framebuffer's alpha is not meaningful).
    public static byte[] ReadBackBufferRgba(int width, int height)
    {
        GLUtils.AssertRenderThread("UiFramebufferCapture.ReadBackBufferRgba");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        byte[] pixels = new byte[width * height * 4];
        int previousAlignment = GL.GetInteger(GetPName.PackAlignment);
        int previousReadBuffer = GL.GetInteger(GetPName.ReadBuffer);
        try
        {
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadBuffer(ReadBufferMode.Back);
            GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        }
        finally
        {
            GL.PixelStore(PixelStoreParameter.PackAlignment, previousAlignment);
            GL.ReadBuffer((ReadBufferMode)previousReadBuffer);
        }

        // GL rows start at the bottom.
        int stride = width * 4;
        byte[] row = new byte[stride];
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            System.Buffer.BlockCopy(pixels, top * stride, row, 0, stride);
            System.Buffer.BlockCopy(pixels, bottom * stride, pixels, top * stride, stride);
            System.Buffer.BlockCopy(row, 0, pixels, bottom * stride, stride);
        }

        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        return pixels;
    }

    public static void SavePng(string path, int width, int height, byte[] rgbaTopRowFirst)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(rgbaTopRowFirst);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using Image<Rgba32> image = Image.LoadPixelData<Rgba32>(rgbaTopRowFirst, width, height);
        image.SaveAsPng(path);
    }

    public static void CaptureBackBufferToPng(string path, int width, int height) =>
        SavePng(path, width, height, ReadBackBufferRgba(width, height));
}
