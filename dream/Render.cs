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

}
