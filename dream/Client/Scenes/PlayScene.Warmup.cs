using Dream.Application;
using Dream.Client.Presentation;
using Dream.Rendering.Core;
using Dream.Rendering.Effects;
using Dream.Rendering.Mist;
using Dream.Rendering.Post;
using Dream.Rendering.Sky;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using WarmUpRender = Dream.Rendering.Core.Render;

namespace Dream.Client.Scenes;

public partial class PlayScene
{
    // The shader warm-up (see ShaderWarmup): one small real draw per program the scene owns, into offscreen targets,
    // so the driver's draw-time specialisation happens behind the loading screen and not on the first world frame.
    // Called once by Game right after the scene is built. Never throws, and puts every piece of GL state back.
    //
    // Not covered: the 2D programs (ImGui, the classic HUD, the title menu, the crosshair). They draw every frame
    // from the title screen on, so they are specialised long before a world is entered, and none of them shows up in
    // the driver's recompile messages on world entry.
    public ShaderWarmupReport? WarmUpShaders()
    {
        if (!ShaderWarmup.IsEnabled(Environment.GetEnvironmentVariable))
        {
            Console.WriteLine($"[startup] shader warm-up: off ({ShaderWarmup.EnvironmentVariable}=0)");
            return null;
        }

        return ShaderWarmup.Run(session =>
        {
            using SceneTarget scene = new(ShaderWarmup.TargetSide, ShaderWarmup.TargetSide, session.ContextSamples);
            if (!scene.IsAvailable)
            {
                throw new InvalidOperationException("the offscreen scene target is not complete");
            }

            // Registry programs: default, cube, terrain, enemy, shadowDepth and every MASK variant.
            session.Step("world programs", () => _render.WarmUpWorldPrograms(session, scene));

            // The passes that draw over the world, in the order the frame runs them, each through its own Draw so the
            // state (blend, depth, VAO, instancing) is the real one.
            session.Step("sky", () =>
            {
                WarmUpRender.BeginWarmUpColourPass(scene);
                if (_sky == null)
                {
                    return;
                }

                _sky.Draw(default(SkyState), WarmUpRender.WarmUpView, WarmUpRender.WarmUpProjection);
                session.CountDraw(_sky.ProgramHandle, "sky");
            });
            session.Step("boundary mist", () =>
            {
                if (_boundaryMist == null)
                {
                    return;
                }

                WarmUpRender.BeginWarmUpColourPass(scene);
                _boundaryMist.Draw(WarmUpRender.WarmUpView, WarmUpRender.WarmUpProjection);
                session.CountDraw(_boundaryMist.ProgramHandle, "boundary mist");
            });
            session.Step("effects", () =>
            {
                if (_fxRenderer == null)
                {
                    return;
                }

                FxInstance[] sparks = new FxInstance[1];
                sparks[0].HalfSize = 0.5f;
                sparks[0].Color = FxInstance.PackColor(1f, 1f, 1f, 1f);
                WarmUpRender.BeginWarmUpColourPass(scene);
                _fxRenderer.Draw(sparks, 1, FxViewBasis.FromView(WarmUpRender.WarmUpView), WarmUpRender.WarmUpView * WarmUpRender.WarmUpProjection);
                session.CountDraw(_fxRenderer.ProgramHandle, "effects");
            });
            session.Step("war mist", () =>
            {
                // Only when the veil will draw. The scene creates this renderer lazily on its first draw (DrawWarMistPass);
                // it is created here instead, into the same field, so the program that gets warmed is the one the frame
                // uses. A throw lands in the step's catch (logged, skipped) and the first draw retries through its own path.
                if (_warMistFailed || !_settings.WarMistEnabled || _settings.CameraViewMode != CameraViewMode.Isometric)
                {
                    return;
                }

                WarMistRenderer renderer = _warMistRenderer ??= new WarMistRenderer(WarMistSettings.Current.SliceCount);

                WarmUpRender.BeginWarmUpColourPass(scene);
                renderer.Draw(WarmUpRender.WarmUpView * WarmUpRender.WarmUpProjection, Vector3.Zero, 0d, new ExplorationMemory(4, 4));
                session.CountDraw(renderer.ProgramHandle, "war mist");
            });
            session.Step("post chain", () =>
            {
                if (_postChain == null)
                {
                    return;
                }

                // The chain reads the resolved single-sample texture and its last pass writes to the window; here it
                // writes to a small RGBA8 target instead (the visible framebuffer is showing the loading screen).
                WarmUpRender.BeginWarmUpColourPass(scene);
                scene.Resolve();
                int output = CreateWarmUpOutput(out int outputTexture);
                try
                {
                    _postChain.Run(scene.ResolvedTexture, scene.Width, scene.Height, output);
                    // Three programs, four draws: bright, blur x2, resolve. Counted by program handle.
                    foreach (int handle in _postChain.ProgramHandles)
                    {
                        session.CountDraw(handle, "post chain");
                    }
                }
                finally
                {
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                    GL.DeleteFramebuffer(output);
                    GL.DeleteTexture(outputTexture);
                }
            });
        });
    }

    // A small colour-only target standing in for the window in the post chain's last pass.
    private static int CreateWarmUpOutput(out int texture)
    {
        texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(
            TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, ShaderWarmup.TargetSide, ShaderWarmup.TargetSide, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        int framebuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
        return framebuffer;
    }
}
