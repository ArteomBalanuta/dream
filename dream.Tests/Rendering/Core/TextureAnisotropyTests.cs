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
            GL.GetTexParameter(TextureTarget.Texture2D, (GetTextureParameter)TextureMaxAnisotropy, out float mippedLevel);
            float applied = TextureAnisotropy.AppliedLevel;
            Assert.Equal(applied > 1f ? applied : 1f, mippedLevel);

            int clamped = TextureDecoder.UploadTexture2DClamped(texture, 0);
            GL.BindTexture(TextureTarget.Texture2D, clamped);
            GL.GetTexParameter(TextureTarget.Texture2D, (GetTextureParameter)TextureMaxAnisotropy, out float clampedLevel);
            Assert.Equal(1f, clampedLevel);

            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.DeleteTexture(mipped);
            GL.DeleteTexture(clamped);
        });
    }
}
