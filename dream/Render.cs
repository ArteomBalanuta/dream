using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using ImGuiNET;
using System.Runtime.CompilerServices;
public class Render
{
    private int _vertexArrayObject;
    private int _vertexBufferObject;
    private Dictionary<string, Shader> _shaders = new();
    private Matrix4 _projection;

    // Cube vertex data: note that you might want to use indexed drawing.
    private readonly float[] _cubeVertices = {
    // Front face
    -0.5f, -0.5f,  0.5f,  // Bottom-left
     0.5f, -0.5f,  0.5f,  // Bottom-right
     0.5f,  0.5f,  0.5f,  // Top-right
    -0.5f,  0.5f,  0.5f,  // Top-left

    // Back face
    -0.5f, -0.5f, -0.5f,  // Bottom-left
     0.5f, -0.5f, -0.5f,  // Bottom-right
     0.5f,  0.5f, -0.5f,  // Top-right
    -0.5f,  0.5f, -0.5f,  // Top-left

    // Left face
    -0.5f, -0.5f, -0.5f,  // Bottom-left
    -0.5f, -0.5f,  0.5f,  // Bottom-right
    -0.5f,  0.5f,  0.5f,  // Top-right
    -0.5f,  0.5f, -0.5f,  // Top-left

    // Right face
     0.5f, -0.5f, -0.5f,  // Bottom-left
     0.5f, -0.5f,  0.5f,  // Bottom-right
     0.5f,  0.5f,  0.5f,  // Top-right
     0.5f,  0.5f, -0.5f,  // Top-left

    // Top face
    -0.5f,  0.5f,  0.5f,  // Bottom-left
     0.5f,  0.5f,  0.5f,  // Bottom-right
     0.5f,  0.5f, -0.5f,  // Top-right
    -0.5f,  0.5f, -0.5f,  // Top-left

    // Bottom face
    -0.5f, -0.5f,  0.5f,  // Bottom-left
     0.5f, -0.5f,  0.5f,  // Bottom-right
     0.5f, -0.5f, -0.5f,  // Top-right
    -0.5f, -0.5f, -0.5f   // Top-left
    };

    // HUD fields in your class:
    private int _hudVao;
    private int _hudVbo;
    private Shader _hudShader;

    // Add these fields
    private IntPtr _imGuiContext;
    private int _fontTexture;
    private Shader _shaderObj;
    private int _shader;
    private int _vertexArray;
    private int _vertexBuffer;
    private int _indexBuffer;

    private void InitializeHUD()
    {
        // Define vertices for the crosshair:
        // Two line segments:
        // Horizontal line from (390,300) to (410,300)
        // Vertical line from (400,290) to (400,310)
        float[] vertices = {
        390f, 300f,  // horizontal left point
        410f, 300f,  // horizontal right point

        400f, 290f,  // vertical top point
        400f, 310f   // vertical bottom point
    };

        // Generate and bind VAO and VBO
        _hudVao = GL.GenVertexArray();
        _hudVbo = GL.GenBuffer();
        GL.BindVertexArray(_hudVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _hudVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        // Vertex attribute layout: 2 floats per vertex.
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);

        // Unbind buffers
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindVertexArray(0);

        // Load the shader for HUD rendering.
        // Ensure these shader files exist and use modern GLSL.
        _hudShader = new Shader("D:\\workspace\\dream-project\\dream\\shaders\\hudVertex.glsl", "D:\\workspace\\dream-project\\dream\\shaders\\hudFragment.glsl");

        InitializeImGui();
    }

    private void InitializeImGui()
    {
        // 1. Initialize ImGui context
        _imGuiContext = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imGuiContext);

        // 2. Set up basic font
        var io = ImGui.GetIO();
        io.Fonts.AddFontDefault();
        io.DisplaySize = new System.Numerics.Vector2(800, 600); // Initial size


        // Disable keyboard navigation completely
        io.ConfigFlags |= ImGuiConfigFlags.NoKeyboard;
        io.ConfigFlags |= ImGuiConfigFlags.NoMouse;

        // Disable gamepad/keyboard navigation
        //io.ConfigFlags |= ImGuiConfigFlags.NavEnableSetMousePos;

        // Disable automatic window focusing
        io.ConfigWindowsMoveFromTitleBarOnly = true;
        io.ConfigWindowsResizeFromEdges = false;

        // 3. Create font texture
        CreateFontTexture();

        // 4. Create basic rendering resources
        CreateImGuiResources();
    }

    private void CreateFontTexture()
    {
        ImGui.GetIO().Fonts.GetTexDataAsRGBA32(out IntPtr pixels,
            out int width, out int height, out _);

        _fontTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
            width, height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);

        ImGui.GetIO().Fonts.SetTexID((IntPtr)_fontTexture);
    }

    private void CreateImGuiResources()
    {
        // Create shader

        _shaderObj = new Shader("D:\\workspace\\dream-project\\dream\\shaders\\hudVertexText.glsl", "D:\\workspace\\dream-project\\dream\\shaders\\hudFragmentText.glsl");
        _shader = _shaderObj.Handle;

        // Create buffers
        _vertexArray = GL.GenVertexArray();
        _vertexBuffer = GL.GenBuffer();
        _indexBuffer = GL.GenBuffer();
    }

    private void RenderImGuiDrawData(ImDrawDataPtr drawData)
    {
        if (drawData.CmdListsCount == 0) return;

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);

        // Setup orthographic projection
        var io = ImGui.GetIO();
        var mvp = Matrix4.CreateOrthographicOffCenter(
            0.0f,
            io.DisplaySize.X,
            io.DisplaySize.Y,
            0.0f,
            -1.0f, 1.0f);

        GL.UseProgram(_shader);
        GL.UniformMatrix4(GL.GetUniformLocation(_shader, "ProjMtx"), false, ref mvp);
        GL.Uniform1(GL.GetUniformLocation(_shader, "Texture"), 0);

        GL.BindVertexArray(_vertexArray);

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];

            // Upload vertex data
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer,
                cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>(),
                cmdList.VtxBuffer.Data,
                BufferUsageHint.StreamDraw);

            // Upload index data
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            GL.BufferData(BufferTarget.ElementArrayBuffer,
                cmdList.IdxBuffer.Size * sizeof(ushort),
                cmdList.IdxBuffer.Data,
                BufferUsageHint.StreamDraw);

            // Set vertex attributes
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false,
                Unsafe.SizeOf<ImDrawVert>(), 0);

            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false,
                Unsafe.SizeOf<ImDrawVert>(), 8);

            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true,
                Unsafe.SizeOf<ImDrawVert>(), 16);

            // Draw commands
            int vtxOffset = 0;
            for (int cmd_i = 0; cmd_i < cmdList.CmdBuffer.Size; cmd_i++)
            {
                var pcmd = cmdList.CmdBuffer[cmd_i];
                if (pcmd.UserCallback != IntPtr.Zero) continue;

                GL.BindTexture(TextureTarget.Texture2D, (int)pcmd.TextureId);
                GL.Scissor(
                    (int)pcmd.ClipRect.X,
                    (int)(io.DisplaySize.Y - pcmd.ClipRect.W),
                    (int)(pcmd.ClipRect.Z - pcmd.ClipRect.X),
                    (int)(pcmd.ClipRect.W - pcmd.ClipRect.Y));

                GL.DrawElements(PrimitiveType.Triangles,
                    (int)pcmd.ElemCount,
                    DrawElementsType.UnsignedShort,
                    (int)pcmd.IdxOffset * sizeof(ushort));
            }
        }

        // Cleanup
        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.Disable(EnableCap.Blend);
    }

    // Add to your DisposeHUD method
    public void DisposeHUD()
    {
        GL.DeleteTexture(_fontTexture);
        GL.DeleteProgram(_shader);
        GL.DeleteBuffer(_vertexBuffer);
        GL.DeleteBuffer(_indexBuffer);
        GL.DeleteVertexArray(_vertexArray);
        ImGui.DestroyContext(_imGuiContext);
    }


    public Render()
    {
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // Create and store shaders.
        LoadShader("default",
            "D:\\workspace\\dream-project\\dream\\shaders\\vertexShader.glsl",
            "D:\\workspace\\dream-project\\dream\\shaders\\fragmentShader.glsl");

        LoadShader("cube",
            "D:\\workspace\\dream-project\\dream\\shaders\\cubeVertex.glsl",
            "D:\\workspace\\dream-project\\dream\\shaders\\cubeFragment.glsl");

        _projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.PiOver4, 1.0f, 0.1f, 100.0f);

        // Set up VAO and VBO for the cube.
        _vertexArrayObject = GL.GenVertexArray();
        _vertexBufferObject = GL.GenBuffer();

        GL.BindVertexArray(_vertexArrayObject);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, _cubeVertices.Length * sizeof(float), _cubeVertices, BufferUsageHint.StaticDraw);

        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindVertexArray(0);

        InitializeHUD();
    }

    public void LoadShader(string name, string vertexPath, string fragmentPath)
    {
        _shaders[name] = new Shader(vertexPath, fragmentPath);
    }

    // Common method to set transformation uniforms.
    private void SetTransforms(Shader shader, Matrix4 model, Matrix4 view)
    {
        shader.SetMatrix4("model", model);
        shader.SetMatrix4("view", view);
        shader.SetMatrix4("projection", _projection);
    }

    private void DrawCubeInternal(Shader shader, Matrix4 model, Matrix4 view)
    {
        shader.Use();
        SetTransforms(shader, model, view);
        GL.BindVertexArray(_vertexArrayObject);
        GL.DrawArrays(PrimitiveType.TriangleFan, 0, _cubeVertices.Length / 3);
        GL.BindVertexArray(0);
    }

    public void DrawCube(Camera camera, Cube cube)
    {
        /* this is working when applied textures but wireframes are off  */
        Matrix4 modelMatrix =
    Matrix4.CreateTranslation(cube.Position)
  * Matrix4.CreateScale(cube.Side)
  * Matrix4.CreateTranslation(new Vector3(-0.5f, -0.5f, -0.5f));

        // Use the cube's pre-calculated center position directly
        if (cube.Animation != null)
        {
            cube.Animation.Render(_shaders[cube.ShaderName],
                                camera.GetViewMatrix(),
                                _projection,
                                modelMatrix);
        }
        else
        {
            DrawCubeTransparent(camera, modelMatrix);
            DrawCubeWireframe(camera, modelMatrix);
        }
    }


    private void DrawCubeWireframe(Camera camera, Matrix4 modelMatrix)
    {
        Shader shader = _shaders.TryGetValue("default", out var s) ? s : _shaders["default"];
        shader.Use();
        shader.SetVector3("color", new Vector3(1.0f, 1.0f, 1.0f)); // white
        SetTransforms(shader, modelMatrix, camera.GetViewMatrix());

        // Enable wireframe mode.
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
        GL.BindVertexArray(_vertexArrayObject);
        GL.DrawArrays(PrimitiveType.TriangleFan, 0, _cubeVertices.Length / 3);
        GL.BindVertexArray(0);
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
    }

    private void DrawCubeTransparent(Camera camera, Matrix4 modelMatrix)
    {
        Shader shader = _shaders.TryGetValue("default", out var s) ? s : _shaders["default"];
        shader.Use();
        shader.SetVector3("color", new Vector3(0.5f, 0.7f, 1.0f)); // light blue
        SetTransforms(shader, modelMatrix, camera.GetViewMatrix());

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.BindVertexArray(_vertexArrayObject);
        GL.DrawArrays(PrimitiveType.TriangleFan, 0, _cubeVertices.Length / 3);
        GL.BindVertexArray(0);
        GL.Disable(EnableCap.Blend);
    }

    public void DrawCollisionShape(Matrix4 modelMatrix, Camera camera)
    {
        Shader shader = _shaders["default"];
        shader.Use();
        shader.SetVector3("color", new Vector3(1.0f, 0.0f, 0.0f)); // Red color for collision

        SetTransforms(shader, modelMatrix, camera.GetViewMatrix());

        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
        GL.BindVertexArray(_vertexArrayObject);
        GL.DrawArrays(PrimitiveType.TriangleFan, 0, _cubeVertices.Length / 3);
        GL.BindVertexArray(0);
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
    }

    // In your Render class
    private bool _wasFocused = true;
    public void DrawHUD(FrameEventArgs args, bool windowFocused)
    {
        // Disable depth test so HUD renders on top
        GL.Disable(EnableCap.DepthTest);

        // Use HUD shader
        _hudShader.Use();
        Matrix4 projection = Matrix4.CreateOrthographicOffCenter(0, 800, 600, 0, -1, 1);
        _hudShader.SetMatrix4("projection", projection);
        _hudShader.SetVector3("color", new Vector3(1.0f, 0.0f, 0.0f));

        // Draw crosshair
        GL.BindVertexArray(_hudVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, 2);
        GL.DrawArrays(PrimitiveType.Lines, 2, 2);
        GL.BindVertexArray(0);

        // Setup ImGui frame
        // Disable all ImGui interactions
        var io = ImGui.GetIO();

        // Reset input states when regaining focus
        if (windowFocused && !_wasFocused)
        {
            // Clear all keyboard/mouse states
            io.KeyCtrl = false;
            io.KeyShift = false;
            io.KeyAlt = false;
            io.KeySuper = false;

            // Clear mouse positions
            io.MousePos = new System.Numerics.Vector2(-float.MaxValue, -float.MaxValue);
        }
        _wasFocused = windowFocused;


        ImGui.NewFrame();

        // Set up basic HUD window
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(10, 10));
        ImGui.Begin("HUD",
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoFocusOnAppearing );

        ImGui.Text("Score: 100");
        ImGui.End();
       
        // Render directly without input handling
        ImGui.Render();
        RenderImGuiDrawData(ImGui.GetDrawData());

        // Re-enable depth testing
        GL.Enable(EnableCap.DepthTest);
    }

}
