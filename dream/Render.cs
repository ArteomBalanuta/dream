using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;

public class Render
{
    private int _vertexBufferObject;
    private int _vertexArrayObject;
    private Dictionary<string, int> _shaders = new Dictionary<string, int>();

    private Matrix4 _projection;

    public Render()
    {
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // Load default shader
        LoadShader("default",
            "D:\\workspace\\dream-project\\dream\\shaders\\vertexShader.glsl",
            "D:\\workspace\\dream-project\\dream\\shaders\\fragmentShader.glsl");

        LoadShader("ak_shader",
            "D:\\workspace\\dream-project\\dream\\shaders\\shaderVert.glsl",
            "D:\\workspace\\dream-project\\dream\\shaders\\shaderFrag.glsl");

        
        _projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.PiOver4, 1.0f, 0.1f, 100.0f);

        // Set up projection matrix
        _projection = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.PiOver4, 1.0f, 0.1f, 100.0f);

        // Define cube vertices (1x1x1 cube)
        float[] cubeVertices = {
            // Front face
            0.0f, 0.0f, 1.0f, // Bottom-left
            1.0f, 0.0f, 1.0f, // Bottom-right
            1.0f, 1.0f, 1.0f, // Top-right
            0.0f, 1.0f, 1.0f, // Top-left

            // Back face
            0.0f, 0.0f, 0.0f, // Bottom-left
            1.0f, 0.0f, 0.0f, // Bottom-right
            1.0f, 1.0f, 0.0f, // Top-right
            0.0f, 1.0f, 0.0f, // Top-left

            // Left face
            0.0f, 0.0f, 0.0f, // Bottom-left
            0.0f, 0.0f, 1.0f, // Bottom-right
            0.0f, 1.0f, 1.0f, // Top-right
            0.0f, 1.0f, 0.0f, // Top-left

            // Right face
            1.0f, 0.0f, 0.0f, // Bottom-left
            1.0f, 0.0f, 1.0f, // Bottom-right
            1.0f, 1.0f, 1.0f, // Top-right
            1.0f, 1.0f, 0.0f, // Top-left

            // Top face
            0.0f, 1.0f, 1.0f, // Bottom-left
            1.0f, 1.0f, 1.0f, // Bottom-right
            1.0f, 1.0f, 0.0f, // Top-right
            0.0f, 1.0f, 0.0f, // Top-left

            // Bottom face
            0.0f, 0.0f, 1.0f, // Bottom-left
            1.0f, 0.0f, 1.0f, // Bottom-right
            1.0f, 0.0f, 0.0f, // Top-right
            0.0f, 0.0f, 0.0f  // Top-left
        };

        // Create and bind the VAO and VBO
        _vertexArrayObject = GL.GenVertexArray();
        _vertexBufferObject = GL.GenBuffer();

        GL.BindVertexArray(_vertexArrayObject);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, cubeVertices.Length * sizeof(float), cubeVertices, BufferUsageHint.StaticDraw);

        // Set up vertex attributes
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        // Unbind VAO and VBO
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindVertexArray(0);
    }

    public void LoadShader(string name, string vertexShaderPath, string fragmentShaderPath)
    {
        int shaderProgram = CreateShaderProgram(vertexShaderPath, fragmentShaderPath);
        _shaders[name] = shaderProgram;
    }

    private void CheckGLError(string context)
    {
        var error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            Console.WriteLine($"[OpenGL Error] {error} in {context}");
        }
    }


    private int CreateShaderProgram(string vertexShaderPath, string fragmentShaderPath)
    {
        // Load and compile vertex shader
        int vertexShader = GL.CreateShader(ShaderType.VertexShader);
        CheckGLError("GL.CreateShader-Vertex");

        string vertexShaderSource = File.ReadAllText(vertexShaderPath);
        GL.ShaderSource(vertexShader, vertexShaderSource);
        GL.CompileShader(vertexShader);

        // Check for vertex shader compilation errors
        GL.GetShader(vertexShader, ShaderParameter.CompileStatus, out int vertexShaderStatus);
        if (vertexShaderStatus == (int)All.False)
        {
            string infoLog = GL.GetShaderInfoLog(vertexShader);
            throw new Exception($"Vertex shader compilation failed: {infoLog}");
        }

        // Load and compile fragment shader
        int fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
        string fragmentShaderSource = File.ReadAllText(fragmentShaderPath);
        GL.ShaderSource(fragmentShader, fragmentShaderSource);
        GL.CompileShader(fragmentShader);

        // Check for fragment shader compilation errors
        GL.GetShader(fragmentShader, ShaderParameter.CompileStatus, out int fragmentShaderStatus);
        if (fragmentShaderStatus == (int)All.False)
        {
            string infoLog = GL.GetShaderInfoLog(fragmentShader);
            throw new Exception($"Fragment shader compilation failed: {infoLog}");
        }

        // Link shaders into a program
        int shaderProgram = GL.CreateProgram();
        GL.AttachShader(shaderProgram, vertexShader);
        GL.AttachShader(shaderProgram, fragmentShader);
        GL.LinkProgram(shaderProgram);
        CheckGLError("GL.LinkProgram");

        GL.GetProgramInfoLog(shaderProgram, out string programLog);
        if (!string.IsNullOrEmpty(programLog))
        {
            Console.WriteLine($"Shader program link log:\n{programLog}");
        }

        // Check for linking errors
        GL.GetProgram(shaderProgram, GetProgramParameterName.LinkStatus, out int programStatus);
        if (programStatus == (int)All.False)
        {
            string infoLog = GL.GetProgramInfoLog(shaderProgram);
            throw new Exception($"Shader program linking failed: {infoLog}");
        }

        // Clean up shaders
        GL.DetachShader(shaderProgram, vertexShader);
        GL.DetachShader(shaderProgram, fragmentShader);
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);

        return shaderProgram;
    }

    private void DrawCubeWireframe(Camera camera, Vector3 position, Cube cube)
    {

        if (!_shaders.TryGetValue(cube.ShaderName, out int shaderProgram))
        {
            shaderProgram = _shaders["default"];
        }

        GL.UseProgram(shaderProgram);


        // Set the color to white (or any color you want for the wireframe)
        int colorLocation = GL.GetUniformLocation(shaderProgram, "color");
        GL.Uniform3(colorLocation, new Vector3(1.0f, 1.0f, 1.0f)); // WHITE COLOR

        // Set the model matrix (scale and translate the cube)
        int modelLocation = GL.GetUniformLocation(shaderProgram, "model");
        Matrix4 modelMatrix = Matrix4.CreateScale(cube.Side) * Matrix4.CreateTranslation(position);
        GL.UniformMatrix4(modelLocation, false, ref modelMatrix);

        // Set the view and projection matrices
        int viewLocation = GL.GetUniformLocation(shaderProgram, "view");
        Matrix4 view = camera.GetViewMatrix();
        GL.UniformMatrix4(viewLocation, false, ref view);

        int projectionLocation = GL.GetUniformLocation(shaderProgram, "projection");
        GL.UniformMatrix4(projectionLocation, false, ref _projection);

        // Enable wireframe mode
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);

        // Bind the VAO and draw the cube
        GL.BindVertexArray(_vertexArrayObject);
        GL.DrawArrays(PrimitiveType.TriangleFan, 0, 512); // 48 vertices for a cube

        // Disable wireframe mode
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);

        // Unbind the VAO
        GL.BindVertexArray(0);
    }

    private void DrawCubeTransparent(Camera camera, Vector3 position, Cube cube)
    {

        if (!_shaders.TryGetValue(cube.ShaderName, out int shaderProgram))
        {
            shaderProgram = _shaders["default"];
        }

        // Use the shader program
        GL.UseProgram(shaderProgram);

        // Set the color to a transparent color (alpha = 0)
        int colorLocation = GL.GetUniformLocation(shaderProgram, "color");
        GL.Uniform3(colorLocation, new Vector3(0.5f, 0.7f, 1.0f)); // Light blue color (alpha is set in the shader)

        // Set the model matrix (scale and translate the cube)
        int modelLocation = GL.GetUniformLocation(shaderProgram, "model");
        Matrix4 modelMatrix = Matrix4.CreateScale(cube.Side) * Matrix4.CreateTranslation(position);
        GL.UniformMatrix4(modelLocation, false, ref modelMatrix);

        // Set the view and projection matrices
        int viewLocation = GL.GetUniformLocation(shaderProgram, "view");
        Matrix4 view = camera.GetViewMatrix();
        GL.UniformMatrix4(viewLocation, false, ref view);

        int projectionLocation = GL.GetUniformLocation(shaderProgram, "projection");
        GL.UniformMatrix4(projectionLocation, false, ref _projection);

        // Enable blending for transparency
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // Bind the VAO and draw the cube
        GL.BindVertexArray(_vertexArrayObject);
        GL.DrawArrays(PrimitiveType.TriangleFan, 0, 24); // 24 vertices for a cube

        // Disable blending
        GL.Disable(EnableCap.Blend);

        // Unbind the VAO
        GL.BindVertexArray(0);
    }

    public void DrawCube(Camera camera, Vector3 position, Cube cube)
    {
        if (cube.Animation != null)
        {
            if (!_shaders.TryGetValue(cube.ShaderName, out int shaderProgram))
                shaderProgram = _shaders["default"];

            Matrix4 modelMatrix = Matrix4.CreateScale(cube.Side) * Matrix4.CreateTranslation(position);
            cube.Animation.Render(
                camera.GetViewMatrix(),
                _projection,
                shaderProgram,
                modelMatrix
            );
        }
        else
        {
            DrawCubeTransparent(camera, position, cube);
            DrawCubeWireframe(camera, position, cube);
        }
    }
}