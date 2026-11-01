using Dream.Rendering.Core;
using OpenTK.Graphics.OpenGL4;

namespace Dream.Rendering.Ui.Gl;

// Uploads atlas pages: clamped, linear, two mip levels (4 px of extrusion survive two levels).
public sealed class UiGlTextureUploader : IUiTextureUploader
{
    public const int MaxMipLevel = 2;

    public int Upload(UiPage page, DecodedTexture texture)
    {
        GLUtils.AssertRenderThread("UiGlTextureUploader.Upload");
        int previous = GL.GetInteger(GetPName.TextureBinding2D);
        try
        {
            return TextureDecoder.UploadTexture2DClamped(texture, MaxMipLevel);
        }
        finally
        {
            GL.BindTexture(TextureTarget.Texture2D, previous);
        }
    }

    public void Delete(int handle)
    {
        if (handle != 0)
        {
            GL.DeleteTexture(handle);
        }
    }
}
