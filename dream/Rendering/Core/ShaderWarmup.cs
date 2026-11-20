using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;

namespace Dream.Rendering.Core;

// Pays the driver's draw-time shader specialisation behind the loading screen instead of on the first world frame.
//
// This is NOT shader compilation: every program is already compiled and linked at construction. Some drivers (NVIDIA
// logs it as "Vertex shader in program N is being recompiled based on GL state") do not emit machine code at link time;
// they wait for a draw to reveal the vertex attribute formats, the enabled attributes and the target, then compile a
// specialised variant. Only a real draw with the state the real pass will use can pre-empt that, so the warm-up issues
// one small real draw per program, through the same vertex layouts and the same renderer draw methods the frame uses.
//
// Rules this class enforces for every caller:
// - Draws go to an offscreen target, never the visible framebuffer (the loading screen is on it).
// - Every piece of GL state a step may touch is captured first and restored on every exit path, exceptions included.
// - Never fatal: a step that throws is logged once and skipped, and the game carries on.
public static class ShaderWarmup
{
    public const string EnvironmentVariable = "DREAM_SHADER_WARMUP";

    // The warm-up target's side in pixels: big enough to be a real (not scissored away) draw, small enough to be free.
    public const int TargetSide = 64;

    // Texture units whose 2D and 2D-array bindings are saved and restored: the material, mist, terrain tile array and
    // material map units and the two shadow map units are all below this.
    private const int SavedTextureUnits = 8;

    // On unless DREAM_SHADER_WARMUP=0, like DREAM_LOADING_PROMPT, so it can be A/B'd from the command line.
    public static bool IsEnabled(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment(EnvironmentVariable)?.Trim() != "0";
    }

    // Runs `body` between a capture and a restore of the GL state, timing it, and logs one [startup] line. Returns the
    // report; never throws.
    public static ShaderWarmupReport Run(Action<ShaderWarmupSession> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        long start = Stopwatch.GetTimestamp();
        ShaderWarmupSession session = new();
        GlStateSnapshot? snapshot = null;
        try
        {
            snapshot = GlStateSnapshot.Capture();
            DrainErrors(session, "before the warm-up");
            // The window's sample count, so the offscreen target multisamples like the scene target will.
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            session.ContextSamples = Math.Max(GL.GetInteger(GetPName.Samples), 0);
            body(session);
            // Any queued driver work counts toward the measured cost.
            GL.Finish();
        }
        catch (Exception exception)
        {
            session.ReportFailure("warm-up", exception);
        }
        finally
        {
            try
            {
                snapshot?.Restore();
                DrainErrors(session, "after the warm-up");
            }
            catch (Exception exception)
            {
                session.ReportFailure("state restore", exception);
            }
        }

        ShaderWarmupReport report = session.ToReport(Stopwatch.GetElapsedTime(start));
        Console.WriteLine(report.Describe());
        return report;
    }

    private static void DrainErrors(ShaderWarmupSession session, string when)
    {
        // A pending error from other code must not be blamed on a warm-up draw, so it is cleared and named once.
        for (int i = 0; i < 16; i++)
        {
            ErrorCode error = GL.GetError();
            if (error == ErrorCode.NoError)
            {
                return;
            }

            session.NoteStrayError(error, when);
        }
    }

    // Everything a warm-up step can disturb, captured by value. Restore also unbinds nothing it did not bind itself: it
    // puts each binding back to what it was, so the next scene renders exactly as if the warm-up had not run.
    private sealed class GlStateSnapshot
    {
        private int _drawFramebuffer;
        private int _readFramebuffer;
        private readonly int[] _viewport = new int[4];
        private readonly int[] _scissorBox = new int[4];
        private int _program;
        private int _vertexArray;
        private int _arrayBuffer;
        private int _activeTexture;
        private readonly int[] _texture2D = new int[SavedTextureUnits];
        private readonly int[] _texture2DArray = new int[SavedTextureUnits];
        private bool _depthTest;
        private bool _cullFace;
        private bool _blend;
        private bool _scissorTest;
        private bool _stencilTest;
        private bool _depthMask;
        private readonly bool[] _colorMask = new bool[4];
        private int _depthFunc;
        private int _cullFaceMode;
        private int _blendSrcRgb;
        private int _blendDstRgb;
        private int _blendSrcAlpha;
        private int _blendDstAlpha;
        private float _clearDepth;
        private readonly float[] _clearColor = new float[4];

        public static GlStateSnapshot Capture()
        {
            GlStateSnapshot s = new();
            s._drawFramebuffer = GL.GetInteger(GetPName.DrawFramebufferBinding);
            s._readFramebuffer = GL.GetInteger(GetPName.ReadFramebufferBinding);
            GL.GetInteger(GetPName.Viewport, s._viewport);
            GL.GetInteger(GetPName.ScissorBox, s._scissorBox);
            s._program = GL.GetInteger(GetPName.CurrentProgram);
            s._vertexArray = GL.GetInteger(GetPName.VertexArrayBinding);
            s._arrayBuffer = GL.GetInteger(GetPName.ArrayBufferBinding);
            s._activeTexture = GL.GetInteger(GetPName.ActiveTexture);
            for (int unit = 0; unit < SavedTextureUnits; unit++)
            {
                GL.ActiveTexture(TextureUnit.Texture0 + unit);
                s._texture2D[unit] = GL.GetInteger(GetPName.TextureBinding2D);
                s._texture2DArray[unit] = GL.GetInteger(GetPName.TextureBinding2DArray);
            }

            GL.ActiveTexture((TextureUnit)s._activeTexture);
            s._depthTest = GL.IsEnabled(EnableCap.DepthTest);
            s._cullFace = GL.IsEnabled(EnableCap.CullFace);
            s._blend = GL.IsEnabled(EnableCap.Blend);
            s._scissorTest = GL.IsEnabled(EnableCap.ScissorTest);
            s._stencilTest = GL.IsEnabled(EnableCap.StencilTest);
            GL.GetBoolean(GetPName.DepthWritemask, out s._depthMask);
            GL.GetBoolean(GetPName.ColorWritemask, s._colorMask);
            s._depthFunc = GL.GetInteger(GetPName.DepthFunc);
            s._cullFaceMode = GL.GetInteger(GetPName.CullFaceMode);
            s._blendSrcRgb = GL.GetInteger(GetPName.BlendSrcRgb);
            s._blendDstRgb = GL.GetInteger(GetPName.BlendDstRgb);
            s._blendSrcAlpha = GL.GetInteger(GetPName.BlendSrcAlpha);
            s._blendDstAlpha = GL.GetInteger(GetPName.BlendDstAlpha);
            s._clearDepth = GL.GetFloat(GetPName.DepthClearValue);
            GL.GetFloat(GetPName.ColorClearValue, s._clearColor);
            return s;
        }

        public void Restore()
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _drawFramebuffer);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _readFramebuffer);
            GL.Viewport(_viewport[0], _viewport[1], _viewport[2], _viewport[3]);
            GL.Scissor(_scissorBox[0], _scissorBox[1], _scissorBox[2], _scissorBox[3]);
            GL.BindVertexArray(_vertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _arrayBuffer);
            for (int unit = 0; unit < SavedTextureUnits; unit++)
            {
                GL.ActiveTexture(TextureUnit.Texture0 + unit);
                GL.BindTexture(TextureTarget.Texture2D, _texture2D[unit]);
                GL.BindTexture(TextureTarget.Texture2DArray, _texture2DArray[unit]);
            }

            GL.ActiveTexture((TextureUnit)_activeTexture);
            SetCap(EnableCap.DepthTest, _depthTest);
            SetCap(EnableCap.CullFace, _cullFace);
            SetCap(EnableCap.Blend, _blend);
            SetCap(EnableCap.ScissorTest, _scissorTest);
            SetCap(EnableCap.StencilTest, _stencilTest);
            GL.DepthMask(_depthMask);
            GL.ColorMask(_colorMask[0], _colorMask[1], _colorMask[2], _colorMask[3]);
            GL.DepthFunc((DepthFunction)_depthFunc);
            GL.CullFace((TriangleFace)_cullFaceMode);
            GL.BlendFuncSeparate(
                (BlendingFactorSrc)_blendSrcRgb,
                (BlendingFactorDest)_blendDstRgb,
                (BlendingFactorSrc)_blendSrcAlpha,
                (BlendingFactorDest)_blendDstAlpha);
            GL.ClearDepth(_clearDepth);
            GL.ClearColor(_clearColor[0], _clearColor[1], _clearColor[2], _clearColor[3]);
            // Last: binding a program is the one thing a step may not have left alone either.
            GL.UseProgram(_program);
        }

        private static void SetCap(EnableCap cap, bool enabled)
        {
            if (enabled)
            {
                GL.Enable(cap);
            }
            else
            {
                GL.Disable(cap);
            }
        }
    }
}

// What a warm-up did, for the [startup] line and for tests.
public sealed record ShaderWarmupReport(int Programs, int Draws, int Failures, TimeSpan Elapsed)
{
    public string Describe() =>
        $"[startup] shader warm-up: {Programs} programs, {Draws} draws, {Failures} failed, " +
        $"{Elapsed.TotalMilliseconds:0.0} ms ({ShaderWarmup.EnvironmentVariable}=0 skips)";
}

// The counters and the log-once failure policy one warm-up run shares between its steps.
public sealed class ShaderWarmupSession
{
    private readonly HashSet<int> _warmedPrograms = [];
    private readonly HashSet<string> _failedSteps = [];
    private int _draws;
    private int _failures;

    // The default framebuffer's sample count, measured by Run, for targets that must multisample like the scene.
    public int ContextSamples { get; internal set; }

    // The program handles that took at least one clean draw (a MASK variant is its own program and counts once).
    public int Programs => _warmedPrograms.Count;

    // Counts one finished draw of `programHandle`: it was a program warmed if GL did not object. A draw GL rejected is
    // logged once per label and does not count, so "programs warmed" never overstates.
    public void CountDraw(int programHandle, string label)
    {
        _draws++;
        ErrorCode error = GL.GetError();
        if (error == ErrorCode.NoError)
        {
            _warmedPrograms.Add(programHandle);
            return;
        }

        _failures++;
        if (_failedSteps.Add(label))
        {
            Console.WriteLine($"[startup] shader warm-up: GL {error} on a draw of {label}; that program is not warmed");
        }
    }

    // Runs one step; a throw is logged once under the step's name and the next step still runs.
    public void Step(string name, Action step)
    {
        try
        {
            step();
        }
        catch (Exception exception)
        {
            ReportFailure(name, exception);
        }
    }

    internal void ReportFailure(string name, Exception exception)
    {
        _failures++;
        if (_failedSteps.Add(name))
        {
            Console.WriteLine($"[startup] shader warm-up: {name} failed and was skipped ({exception.GetType().Name}: {exception.Message})");
        }
    }

    internal void NoteStrayError(ErrorCode error, string when)
    {
        if (_failedSteps.Add($"stray:{when}"))
        {
            Console.WriteLine($"[startup] shader warm-up: cleared a pending GL {error} {when}");
        }
    }

    internal ShaderWarmupReport ToReport(TimeSpan elapsed) => new(Programs, _draws, _failures, elapsed);
}
