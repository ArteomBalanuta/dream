using OpenTK.Graphics.OpenGL4;

namespace Dream.Rendering.Ui.Gl;

// Uploads the baked ImGui glyph atlas: RGBA, linear/linear, no mips. Ui/Gl is the only place under
// Ui allowed to touch GL, so the front end's font texture lives here rather than beside the menu.
// Render thread only.
public static class UiGlFontTexture
{
    public static int Upload(IntPtr pixels, int width, int height)
    {
        int previous = GL.GetInteger(GetPName.TextureBinding2D);
        int texture = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        // GetTexDataAsRGBA32 returns R,G,B,A bytes.
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.BindTexture(TextureTarget.Texture2D, previous);
        return texture;
    }

    public static void Delete(int handle)
    {
        GL.DeleteTexture(handle);
    }
}
