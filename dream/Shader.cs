using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.IO;

public class Shader
{
    public int Handle { get; private set; }
    private bool _disposed = false;

    public Shader(string vertexPath, string fragmentPath)
    {
        // Load sources from file.
        string vertexSource = File.ReadAllText(vertexPath);
        string fragmentSource = File.ReadAllText(fragmentPath);

        // Compile shaders.
        int vertexShader = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vertexShader, vertexSource);
        GL.CompileShader(vertexShader);
        GL.GetShader(vertexShader, ShaderParameter.CompileStatus, out int vStatus);
        if (vStatus == (int)All.False)
        {
            string infoLog = GL.GetShaderInfoLog(vertexShader);
            throw new Exception($"Vertex shader compilation failed: {infoLog}");
        }

        int fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fragmentShader, fragmentSource);
        GL.CompileShader(fragmentShader);
        GL.GetShader(fragmentShader, ShaderParameter.CompileStatus, out int fStatus);
        if (fStatus == (int)All.False)
        {
            string infoLog = GL.GetShaderInfoLog(fragmentShader);
            throw new Exception($"Fragment shader compilation failed: {infoLog}");
        }

        // Link shaders into a program.
        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, vertexShader);
        GL.AttachShader(Handle, fragmentShader);
        GL.LinkProgram(Handle);
        GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int linkStatus);
        if (linkStatus == (int)All.False)
        {
            string infoLog = GL.GetProgramInfoLog(Handle);
            throw new Exception($"Shader program linking failed: {infoLog}");
        }

        // Cleanup.
        GL.DetachShader(Handle, vertexShader);
        GL.DetachShader(Handle, fragmentShader);
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);

        GLUtils.CheckGLError("Shader creation");
    }

    public void Use() => GL.UseProgram(Handle);

    #region Uniform Setters
    public void SetMatrix4(string uniformName, Matrix4 matrix)
    {
        int location = GL.GetUniformLocation(Handle, uniformName);
        GL.UniformMatrix4(location, false, ref matrix);
    }

    public void SetVector4(string uniformName, Vector4 vector)
    {
        int location = GL.GetUniformLocation(Handle, uniformName);
        GL.Uniform4(location, vector);
    }

    public void SetVector3(string uniformName, Vector3 vector)
    {
        int location = GL.GetUniformLocation(Handle, uniformName);
        GL.Uniform3(location, vector);
    }

    public void SetVector2(string uniformName, Vector2 vector)
    {
        int location = GL.GetUniformLocation(Handle, uniformName);
        GL.Uniform2(location, vector);
    }

    public void SetFloat(string uniformName, float value)
    {
        int location = GL.GetUniformLocation(Handle, uniformName);
        GL.Uniform1(location, value);
    }

    public void SetInt(string uniformName, int value)
    {
        int location = GL.GetUniformLocation(Handle, uniformName);
        GL.Uniform1(location, value);
    }

    public void SetBool(string uniformName, bool value)
    {
        int location = GL.GetUniformLocation(Handle, uniformName);
        GL.Uniform1(location, value ? 1 : 0);
    }
    #endregion

    #region Resource Management
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            GL.DeleteProgram(Handle);
            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~Shader() => Dispose(false);
    #endregion
}
