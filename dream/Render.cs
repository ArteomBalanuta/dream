using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.IO;

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

    public void DrawHUD()
    {
        // Disable depth testing so the HUD always appears on top.
        GL.Disable(EnableCap.DepthTest);

        // Use the HUD shader
        _hudShader.Use();

        // Create an orthographic projection matrix for 2D screen space.
        // Here, the screen is assumed to be 800x600.
        Matrix4 projection = Matrix4.CreateOrthographicOffCenter(0, 800, 600, 0, -1, 1);
        _hudShader.SetMatrix4("projection", projection);

        // Set the uniform for the crosshair color (red)
        _hudShader.SetVector3("color", new Vector3(1.0f, 0.0f, 0.0f));

        // Bind the VAO containing the crosshair geometry.
        GL.BindVertexArray(_hudVao);

        // Draw the two line segments.
        // First two vertices make the horizontal line:
        GL.DrawArrays(PrimitiveType.Lines, 0, 2);
        // Next two vertices make the vertical line:
        GL.DrawArrays(PrimitiveType.Lines, 2, 2);

        // Unbind the VAO.
        GL.BindVertexArray(0);

        // Re-enable depth testing after drawing the HUD.
        GL.Enable(EnableCap.DepthTest);
    }

}
