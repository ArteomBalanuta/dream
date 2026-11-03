using Dream.Application;
using Dream.Rendering.Actors;
using Dream.Rendering.Hud;
using Dream.Rendering.ViewModels;
using Dream.Rendering.World;
using OpenTK.Graphics.OpenGL4;
using ImGuiNET;
using System.Runtime.ExceptionServices;

namespace Dream.Rendering.Core;

public partial class Render
{
    // Releases every GL resource and child renderer this Render owns, on the render thread.
    // Child renderers go first because they draw with the shaders, and the ImGui context is
    // destroyed last. Each step runs even if an earlier one throws; the failures are rethrown
    // at the end. The startup menu destroys its own, separate ImGui context.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        GLUtils.AssertRenderThread("Render disposal");
        _disposed = true;

        List<Exception>? failures = null;
        RunDisposeStep(_worldRenderer.Dispose, ref failures);
        RunDisposeStep(_localPlayerRenderer.Dispose, ref failures);
        RunDisposeStep(_remotePlayerRenderer.Dispose, ref failures);
        RunDisposeStep(_shaders.DisposeAll, ref failures);
        RunDisposeStep(DisposeFrameLighting, ref failures);
        RunDisposeStep(DeleteDebugCubeBuffers, ref failures);
        RunDisposeStep(_fallbackCubeBuffer.Dispose, ref failures);
        RunDisposeStep(_fallbackInstances.Dispose, ref failures);
        RunDisposeStep(_staticTerrainTextures.Dispose, ref failures);
        RunDisposeStep(DisposeClassicHud, ref failures);
        RunDisposeStep(DeleteFontTexture, ref failures);
        RunDisposeStep(_hudShader.Dispose, ref failures);
        RunDisposeStep(_shaderObj.Dispose, ref failures);
        RunDisposeStep(DeleteHudBuffers, ref failures);
        RunDisposeStep(DestroyImGuiContext, ref failures);

        if (failures == null)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(failures);
    }

    private static void RunDisposeStep(Action step, ref List<Exception>? failures)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }
    }

    private void DeleteDebugCubeBuffers()
    {
        GL.DeleteBuffer(_vertexBufferObject);
        GL.DeleteVertexArray(_vertexArrayObject);
        _vertexBufferObject = 0;
        _vertexArrayObject = 0;
    }

    private void DeleteFontTexture()
    {
        GL.DeleteTexture(_fontTexture);
        _fontTexture = 0;
    }

    private void DeleteHudBuffers()
    {
        GL.DeleteBuffer(_hudVbo);
        GL.DeleteBuffer(_vertexBuffer);
        GL.DeleteBuffer(_indexBuffer);
        GL.DeleteVertexArray(_hudVao);
        GL.DeleteVertexArray(_vertexArray);
        _hudVbo = 0;
        _vertexBuffer = 0;
        _indexBuffer = 0;
        _hudVao = 0;
        _vertexArray = 0;
    }

    // ImGui.DestroyContext(IntPtr.Zero) would destroy whichever context is current, so a
    // context that was never created is skipped.
    private void DestroyImGuiContext()
    {
        if (_imGuiContext == IntPtr.Zero)
        {
            return;
        }

        ImGui.DestroyContext(_imGuiContext);
        _imGuiContext = IntPtr.Zero;
    }

    public Render(GameSettings settings)
    {
        _worldRenderer = new WorldRenderer(this);
        _enemyRenderer = new EnemyRenderer(this);
        _propRenderer = new Dream.Rendering.Props.PropRenderer(this);
        _localPlayerRenderer = new LocalPlayerRenderer(this);
        _remotePlayerRenderer = new RemotePlayerRenderer(this);
        _debugRenderer = new DebugRenderer(this);
        _hudRenderer = new HudRenderer(this);
        _viewModelRenderer = new ViewModelRenderer(this);
        // No global blend state: each pass sets what it needs. The world pass turns blending off
        // and depth testing on at its start, and the HUD and ImGui passes enable blending themselves.
        GL.Enable(EnableCap.DepthTest);
        // GL_MULTISAMPLE is already TRUE in a fresh context and nothing here disables it, so this call
        // changes nothing; it documents that the multisampled default framebuffer (Game's NumberOfSamples)
        // is resolved implicitly at SwapBuffers with no FBO. Alpha-mask materials discard, which kills
        // every sample, so cutout edges stay aliased; no alpha-to-coverage by design.
        GL.Enable(EnableCap.Multisample);

        InitializeShaders();
        InitializeFrameLighting();

        UpdateProjectionForSettings(settings);

        // Set up VAO and VBO for the cube.
        _vertexArrayObject = GL.GenVertexArray();
        _vertexBufferObject = GL.GenBuffer();

        GL.BindVertexArray(_vertexArrayObject);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, CubeVertices.Length * sizeof(float), CubeVertices, BufferUsageHint.StaticDraw);

        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindVertexArray(0);

        // The position-only cube VAO above serves debug/collision draws. Textured fallback cubes
        // need normals and UVs, so they use a mesh with the static terrain vertex layout.
        _fallbackCubeBuffer = new StaticMeshBuffer(TerrainFallbackCubeMesh.Create());

        InitializeHUD();
    }
}
