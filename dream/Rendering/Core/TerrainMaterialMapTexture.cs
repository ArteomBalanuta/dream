using Dream.Rendering.World;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Dream.Rendering.Core;

// The GPU copy of TerrainMaterialMap: an R8UI 3D texture on texture unit 3 (uTerrainMaterialMap).
// Integer textures cannot filter, so it is sampled with texelFetch and uses nearest filtering and
// a single mip level (otherwise the texture is incomplete and every fetch returns 0).
public sealed class TerrainMaterialMapTexture : IDisposable
{
    public const int TextureUnitIndex = 3;
    public const string OriginUniform = "uTerrainMapOrigin";
    public const string SizeUniform = "uTerrainMapSize";

    private int _handle;
    private Vector3i _textureSize;
    private Vector3i _origin;
    private Vector3i _size;

    public bool IsDisposed { get; private set; }

    // The size of the map last uploaded; zero when there is none.
    public Vector3i Size => _size;

    public void Upload(TerrainMaterialMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _origin = map.Origin;
        _size = map.Size;
        if (map.IsEmpty)
        {
            _size = Vector3i.Zero;
            return;
        }

        GLUtils.AssertRenderThread("TerrainMaterialMapTexture upload");
        GL.ActiveTexture(TextureUnit.Texture0 + TextureUnitIndex);
        int previousAlignment = GL.GetInteger(GetPName.UnpackAlignment);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        if (_handle == 0)
        {
            _handle = GL.GenTexture();
            _textureSize = Vector3i.Zero;
        }

        GL.BindTexture(TextureTarget.Texture3D, _handle);
        if (_textureSize != _size)
        {
            GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R8ui, _size.X, _size.Y, _size.Z, 0, PixelFormat.RedInteger, PixelType.UnsignedByte, map.Texels);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureBaseLevel, 0);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMaxLevel, 0);
            _textureSize = _size;
        }
        else
        {
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, _size.X, _size.Y, _size.Z, PixelFormat.RedInteger, PixelType.UnsignedByte, map.Texels);
        }

        GL.PixelStore(PixelStoreParameter.UnpackAlignment, previousAlignment);
        GL.ActiveTexture(TextureUnit.Texture0);
    }

    // Binds the map on unit 3 and sets the two uniforms on the (already used) terrain program. With
    // no map the size is zero, which tells the shader to skip the blend.
    public void Bind(Shader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_handle == 0 || _size == Vector3i.Zero)
        {
            shader.SetVector3(OriginUniform, Vector3.Zero);
            shader.SetVector3(SizeUniform, Vector3.Zero);
            return;
        }

        GL.ActiveTexture(TextureUnit.Texture0 + TextureUnitIndex);
        GL.BindTexture(TextureTarget.Texture3D, _handle);
        GL.ActiveTexture(TextureUnit.Texture0);
        shader.SetVector3(OriginUniform, new Vector3(_origin.X, _origin.Y, _origin.Z));
        shader.SetVector3(SizeUniform, new Vector3(_size.X, _size.Y, _size.Z));
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
            GLUtils.AssertRenderThread("TerrainMaterialMapTexture disposal");
            GL.DeleteTexture(_handle);
            _handle = 0;
        }
    }
}
