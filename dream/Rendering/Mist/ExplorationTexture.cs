using OpenTK.Graphics.OpenGL4;
using Dream.Rendering.Core;

namespace Dream.Rendering.Mist;

// The GPU copy of ExplorationMemory: an R8 texture, one texel per map cell, linearly filtered and
// clamped so the remembered edge is soft. It is re-created only when the map size changes and
// re-uploaded in full only when the memory is dirty; a clean frame costs a bind and nothing else.
public sealed class ExplorationTexture : IDisposable
{
    private int _handle;
    private int _width;
    private int _depth;

    public bool IsDisposed { get; private set; }

    // Binds the texture on the active texture unit, bringing it up to date with the memory first.
    public void Bind(ExplorationMemory memory)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_handle == 0 || memory.Width != _width || memory.Depth != _depth)
        {
            Recreate(memory);
        }
        else
        {
            GL.BindTexture(TextureTarget.Texture2D, _handle);
            if (memory.Dirty)
            {
                Upload(memory, create: false);
            }
        }
    }

    private void Recreate(ExplorationMemory memory)
    {
        GLUtils.AssertRenderThread("ExplorationTexture creation");
        if (_handle != 0)
        {
            GL.DeleteTexture(_handle);
        }

        _handle = GL.GenTexture();
        _width = memory.Width;
        _depth = memory.Depth;
        GL.BindTexture(TextureTarget.Texture2D, _handle);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        Upload(memory, create: true);
    }

    // Rows are not a multiple of four bytes in general, so the unpack alignment is 1 for the call.
    private void Upload(ExplorationMemory memory, bool create)
    {
        int previousAlignment = GL.GetInteger(GetPName.UnpackAlignment);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        if (create)
        {
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, _width, _depth, 0, PixelFormat.Red, PixelType.UnsignedByte, memory.Cells);
        }
        else
        {
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, _width, _depth, PixelFormat.Red, PixelType.UnsignedByte, memory.Cells);
        }

        GL.PixelStore(PixelStoreParameter.UnpackAlignment, previousAlignment);
        memory.ClearDirty();
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        if (_handle != 0)
        {
            GLUtils.AssertRenderThread("ExplorationTexture disposal");
            GL.DeleteTexture(_handle);
            _handle = 0;
        }
    }
}
