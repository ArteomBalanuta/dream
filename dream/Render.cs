using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

public class Render
{
    private int _vertexBufferObject;
    private int _vertexArrayObject;
    private int _shaderProgram;

    private Matrix4 _projection;

    public Render()
    {
        // Initialize OpenGL settings
        GL.Enable(EnableCap.DepthTest);

        // Compile shaders
        _shaderProgram = CreateShaderProgram("D:\\workspace\\dream-project\\dream\\shaders\\vertexShader.glsl", "D:\\workspace\\dream-project\\dream\\shaders\\fragmentShader.glsl");

        // Set up projection matrix
        _projection = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.PiOver4, 1.0f, 0.1f, 100.0f);

        // Set up floor vertices
        float[] floorVertices = {
            // Position
            0, 0, 0,
            10, 0, 0,
            10, 0, 10,
            0, 0, 10
        };

        // Create and bind the VAO and VBO
        _vertexArrayObject = GL.GenVertexArray();
        _vertexBufferObject = GL.GenBuffer();

        GL.BindVertexArray(_vertexArrayObject);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, floorVertices.Length * sizeof(float), floorVertices, BufferUsageHint.StaticDraw);

        // Set up vertex attributes
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        // Unbind VAO and VBO
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindVertexArray(0);
    }

    public void DrawFloor(Camera camera)
    {
        // Use the shader program
        GL.UseProgram(_shaderProgram);

        // Set the color to brown
        int colorLocation = GL.GetUniformLocation(_shaderProgram, "color");
        GL.Uniform3(colorLocation, new Vector3(0.6f, 0.3f, 0.1f));

        // Set the model matrix (identity matrix for the floor)
        int modelLocation = GL.GetUniformLocation(_shaderProgram, "model");
        Matrix4 modelMatrix = Matrix4.Identity; // Store the identity matrix in a variable
        GL.UniformMatrix4(modelLocation, false, ref modelMatrix); // Pass the variable as a ref parameter

        // Set the view and projection matrices
        int viewLocation = GL.GetUniformLocation(_shaderProgram, "view");
        Matrix4 view = camera.GetViewMatrix();
        GL.UniformMatrix4(viewLocation, false, ref view);

        int projectionLocation = GL.GetUniformLocation(_shaderProgram, "projection");
        GL.UniformMatrix4(projectionLocation, false, ref _projection);

        // Bind the VAO and draw the floor
        GL.BindVertexArray(_vertexArrayObject);
        GL.DrawArrays(PrimitiveType.TriangleFan, 0, 4);

        // Unbind the VAO
        GL.BindVertexArray(0);
    }

    private int CreateShaderProgram(string vertexShaderPath, string fragmentShaderPath)
    {
        // Load and compile vertex shader
        int vertexShader = GL.CreateShader(ShaderType.VertexShader);
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
}