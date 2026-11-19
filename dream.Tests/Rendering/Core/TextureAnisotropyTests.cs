using Dream.Rendering.Core;
using Dream.Tests.Support;
using OpenTK.Graphics.OpenGL4;
using Xunit;

namespace Dream.Tests.Rendering.Core;

[Collection(RenderThreadAffinityCollection.Name)]
public sealed class TextureAnisotropyTests
{
    private const int TextureMaxAnisotropy = 0x84FE;

    [Theory]
    [InlineData(null, 8f)]
    [InlineData("", 8f)]
    [InlineData("banana", 8f)]
    [InlineData("0", 1f)]
    [InlineData("1", 1f)]
    [InlineData("-4", 1f)]
    [InlineData("0.5", 1f)]
    [InlineData("2", 2f)]
    [InlineData("4", 4f)]
    [InlineData("16", 16f)]
    [InlineData("64", 16f)]
    public void ResolveLevel_FollowsTheTable(string? value, float expected)
    {
        Assert.Equal(expected, TextureAnisotropy.ResolveLevel(value));
    }

    [Fact]
    public void DefaultsAreTheDocumentedOnes()
    {
        Assert.Equal("DREAM_ANISOTROPY", TextureAnisotropy.EnvironmentVariable);
        Assert.Equal(8f, TextureAnisotropy.DefaultLevel);
    }

    private static bool ContextSupportsAnisotropy()
    {
        GL.GetInteger(GetPName.MajorVersion, out int major);
        GL.GetInteger(GetPName.MinorVersion, out int minor);
        if (major > 4 || (major == 4 && minor >= 6))
        {
            return true;
        }

        GL.GetInteger(GetPName.NumExtensions, out int count);
        for (int i = 0; i < count; i++)
        {
            string name = GL.GetString(StringNameIndexed.Extensions, i);
            if (name.EndsWith("_texture_filter_anisotropic", StringComparison.Ordinal)
                && (name.StartsWith("GL_EXT_", StringComparison.Ordinal) || name.StartsWith("GL_ARB_", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    [GlFact]
    public void MippedUpload_IsRgba8AndAnisotropic_ClampedUploadIsNot()
    {
        HiddenGlContext.Run(() =>
        {
            DecodedTexture texture = new(4, 4, new byte[4 * 4 * 4]);

            int mipped = TextureDecoder.UploadTexture2D(texture, true);
            GL.BindTexture(TextureTarget.Texture2D, mipped);
            GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat, out int format);
            Assert.Equal((int)PixelInternalFormat.Rgba8, format);
            float applied = TextureAnisotropy.AppliedLevel;

            // Independent of the product's own support check, so a wrong "unsupported" cannot pass. Also guards the
            // readback: TEXTURE_MAX_ANISOTROPY is an invalid enum on a context without the extension.
            float requested = TextureAnisotropy.ResolveLevel(
                Environment.GetEnvironmentVariable(TextureAnisotropy.EnvironmentVariable));
            bool supported = ContextSupportsAnisotropy();
            if (supported)
            {
                GL.GetTexParameter(TextureTarget.Texture2D, (GetTextureParameter)TextureMaxAnisotropy, out float mippedLevel);
                Assert.Equal(applied > 1f ? applied : 1f, mippedLevel);
                if (requested > 1f)
                {
                    Assert.True(applied > 1f, $"context supports anisotropy but AppliedLevel is {applied}");
                    Assert.True(mippedLevel > 1f, $"TEXTURE_MAX_ANISOTROPY reads back {mippedLevel}");
                }
            }

            int clamped = TextureDecoder.UploadTexture2DClamped(texture, 0);
            GL.BindTexture(TextureTarget.Texture2D, clamped);
            if (supported)
            {
                GL.GetTexParameter(TextureTarget.Texture2D, (GetTextureParameter)TextureMaxAnisotropy, out float clampedLevel);
                Assert.Equal(1f, clampedLevel);
            }

            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.DeleteTexture(mipped);
            GL.DeleteTexture(clamped);
        });
    }
}
