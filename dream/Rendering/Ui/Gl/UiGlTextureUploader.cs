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
            // Drain errors pending from other code first, so they are not blamed on this upload.
            // A pending Fatal is still latched by CheckGLError.
            GLUtils.CheckGLError("before UI atlas upload");
            int handle = TextureDecoder.UploadTexture2DClamped(texture, MaxMipLevel);

            // A bad upload (out of memory, an unsupported size) leaves a GL error and an unusable
            // texture. Delete it and throw, so the atlas store ends in Failed and the HUD falls back.
            // Out of memory is also latched Fatal by CheckGLError, which closes the window.
            GLFaultKind fault = GLUtils.CheckGLError("UiGlTextureUploader.Upload");
            if (fault != GLFaultKind.None)
            {
                GL.DeleteTexture(handle);
                throw new InvalidOperationException($"{page} texture upload left a GL error ({fault}).");
            }

            return handle;
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
