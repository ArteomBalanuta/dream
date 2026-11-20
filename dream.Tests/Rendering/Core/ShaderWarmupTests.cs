using Dream.Rendering.Core;
using Dream.Tests.Integration;
using Dream.Tests.Support;
using OpenTK.Graphics.OpenGL4;
using Xunit;

namespace Dream.Tests.Rendering.Core;

// The shader warm-up must never be fatal and must put every piece of GL state back on every exit path, a thrown
// step included: a framebuffer or viewport left behind makes the next scene render into the wrong target.
[Collection(RenderThreadAffinityCollection.Name)]
public sealed class ShaderWarmupTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData(" 0 ", false)]
    public void The_Environment_Variable_Turns_The_Warm_Up_Off_Only_With_Zero(string? value, bool expected)
    {
        Assert.Equal(expected, ShaderWarmup.IsEnabled(name => name == ShaderWarmup.EnvironmentVariable ? value : null));
    }

    [GlFact]
    public void Run_Restores_The_State_It_Disturbed_And_Survives_A_Throwing_Body()
    {
        string shaders = Path.Combine(RepoRoot.Find(), "dream", "shaders");
        HiddenGlContext.Run(() =>
        {
            using Shader program = new(Path.Combine(shaders, "vertexShader.glsl"), Path.Combine(shaders, "fragmentShader.glsl"));
            GL.Viewport(1, 2, 30, 40);
            GL.Enable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(TriangleFace.Front);
            GL.DepthFunc(DepthFunction.Gequal);
            GL.UseProgram(program.Handle);
            GL.ActiveTexture(TextureUnit.Texture0 + 1);
            int unit1Texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, unit1Texture);
            GL.ActiveTexture(TextureUnit.Texture0);

            ShaderWarmupReport report = ShaderWarmup.Run(session =>
            {
                int framebuffer = GL.GenFramebuffer();
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                GL.Viewport(0, 0, 8, 8);
                GL.Disable(EnableCap.DepthTest);
                GL.Enable(EnableCap.Blend);
                GL.Disable(EnableCap.CullFace);
                GL.CullFace(TriangleFace.Back);
                GL.DepthFunc(DepthFunction.Less);
                GL.UseProgram(0);
                GL.ActiveTexture(TextureUnit.Texture0 + 1);
                GL.BindTexture(TextureTarget.Texture2D, 0);
                GL.ActiveTexture(TextureUnit.Texture0 + 5);
                throw new InvalidOperationException("synthetic failure");
            });

            Assert.Equal(1, report.Failures);
            Assert.Equal(0, GL.GetInteger(GetPName.DrawFramebufferBinding));
            int[] viewport = new int[4];
            GL.GetInteger(GetPName.Viewport, viewport);
            Assert.Equal([1, 2, 30, 40], viewport);
            Assert.True(GL.IsEnabled(EnableCap.DepthTest));
            Assert.False(GL.IsEnabled(EnableCap.Blend));
            Assert.True(GL.IsEnabled(EnableCap.CullFace));
            Assert.Equal((int)TriangleFace.Front, GL.GetInteger(GetPName.CullFaceMode));
            Assert.Equal((int)DepthFunction.Gequal, GL.GetInteger(GetPName.DepthFunc));
            Assert.Equal(program.Handle, GL.GetInteger(GetPName.CurrentProgram));
            Assert.Equal((int)TextureUnit.Texture0, GL.GetInteger(GetPName.ActiveTexture));
            GL.ActiveTexture(TextureUnit.Texture0 + 1);
            Assert.Equal(unit1Texture, GL.GetInteger(GetPName.TextureBinding2D));
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.DeleteTexture(unit1Texture);
        });
    }

    // Synthetic negative: the restore assertions above can fail. A snapshot that was not restored would leave the
    // body's viewport, so a body that changes it must be observable without the guard.
    [GlFact]
    public void The_Restore_Assertion_Can_Fail()
    {
        HiddenGlContext.Run(() =>
        {
            GL.Viewport(1, 2, 30, 40);
            GL.Viewport(0, 0, 8, 8);
            int[] viewport = new int[4];
            GL.GetInteger(GetPName.Viewport, viewport);
            Assert.NotEqual([1, 2, 30, 40], viewport);
        });
    }

    [GlFact]
    public void A_Draw_Counts_As_Warmed_Only_When_Gl_Reports_No_Error()
    {
        string shaders = Path.Combine(RepoRoot.Find(), "dream", "shaders");
        HiddenGlContext.Run(() =>
        {
            using Shader program = new(Path.Combine(shaders, "vertexShader.glsl"), Path.Combine(shaders, "fragmentShader.glsl"));
            ShaderWarmupReport report = ShaderWarmup.Run(session =>
            {
                session.CountDraw(program.Handle, "clean");
                GL.BindTexture((TextureTarget)0x1234, 1); // an invalid enum: GL records INVALID_ENUM
                session.CountDraw(program.Handle + 1000, "rejected");
            });

            Assert.Equal(1, report.Programs);
            Assert.Equal(2, report.Draws);
            Assert.Equal(1, report.Failures);
        });
    }
}
