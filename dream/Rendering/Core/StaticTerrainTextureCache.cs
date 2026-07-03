using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Graphics.OpenGL4;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;

namespace Dream.Rendering.Core;

public sealed class StaticTerrainTextureCache : IDisposable
{
    private readonly Dictionary<StaticTerrainTextureKey, int> _textures = new();
    private bool _disposed;

    public int GetOrLoad(StaticTerrainMaterial material)
    {
        if (!material.HasTexture)
        {
            return -1;
        }

        StaticTerrainTextureKey key = new(material.MaterialId, material.Role);
        if (_textures.TryGetValue(key, out int existing))
        {
            return existing;
        }

        int created = LoadTexture(material.TexturePath);
        _textures[key] = created;
        return created;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (int texture in _textures.Values)
        {
            if (texture != -1)
            {
                GL.DeleteTexture(texture);
            }
        }

        _textures.Clear();
        _disposed = true;
    }

    private static int LoadTexture(string path)
    {
        if (!File.Exists(path))
        {
            return -1;
        }

        int handle = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, handle);
        using Image<Rgba32> image = Image.Load<Rgba32>(path);
        image.Mutate(operation => operation.Flip(FlipMode.Vertical));

        byte[] pixelData = new byte[4 * image.Width * image.Height];
        image.CopyPixelDataTo(pixelData);

        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba,
            image.Width,
            image.Height,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            pixelData);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

        return handle;
    }

    private readonly record struct StaticTerrainTextureKey(
        GeometryMaterialId MaterialId,
        StaticTerrainMaterialRole Role);
}
