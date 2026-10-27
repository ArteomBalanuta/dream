using Dream.Rendering.Models;

namespace Dream.Rendering.Core;

﻿using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.IO;

public sealed class Shader : IDisposable
{
    public int Handle { get; private set; }
    private readonly Dictionary<string, int> _uniformLocations = new();
    private readonly Dictionary<string[], int[]> _uniformLocationTables = new(ReferenceEqualityComparer.Instance);

    public Shader(string vertexPath, string fragmentPath)
    {
        GLUtils.AssertRenderThread("Shader creation");

        // Load sources from file.
        string vertexSource = File.ReadAllText(vertexPath);
        string fragmentSource = File.ReadAllText(fragmentPath);

        int vertexShader = 0;
        int fragmentShader = 0;
        int program = 0;
        bool linked = false;
        try
        {
            // Compile shaders.
            vertexShader = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vertexShader, vertexSource);
            GL.CompileShader(vertexShader);
            GL.GetShader(vertexShader, ShaderParameter.CompileStatus, out int vStatus);
            if (vStatus == (int)All.False)
            {
                string infoLog = GL.GetShaderInfoLog(vertexShader);
                throw new Exception($"Vertex shader compilation failed: {infoLog}");
            }

            fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fragmentShader, fragmentSource);
            GL.CompileShader(fragmentShader);
            GL.GetShader(fragmentShader, ShaderParameter.CompileStatus, out int fStatus);
            if (fStatus == (int)All.False)
            {
                string infoLog = GL.GetShaderInfoLog(fragmentShader);
                throw new Exception($"Fragment shader compilation failed: {infoLog}");
            }

            // Link shaders into a program.
            program = GL.CreateProgram();
            GL.AttachShader(program, vertexShader);
            GL.AttachShader(program, fragmentShader);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linkStatus);
            if (linkStatus == (int)All.False)
            {
                string infoLog = GL.GetProgramInfoLog(program);
                throw new Exception($"Shader program linking failed: {infoLog}");
            }

            GL.DetachShader(program, vertexShader);
            GL.DetachShader(program, fragmentShader);
            linked = true;
        }
        finally
        {
            if (vertexShader != 0)
            {
                GL.DeleteShader(vertexShader);
            }

            if (fragmentShader != 0)
            {
                GL.DeleteShader(fragmentShader);
            }

            if (!linked && program != 0)
            {
                GL.DeleteProgram(program);
            }
        }

        Handle = program;
        GLUtils.CheckGLError("Shader creation");
    }

    public void Use() => GL.UseProgram(Handle);

    #region Uniform Setters
    private int GetUniformLocation(string uniformName)
    {
        if (_uniformLocations.TryGetValue(uniformName, out int existing))
        {
            return existing;
        }

        int location = GL.GetUniformLocation(Handle, uniformName);
        _uniformLocations[uniformName] = location;
        return location;
    }

    // Resolves a table of uniform names once per program. The table is keyed by the array
    // instance, so callers pass a static array and get the same int[] back on every call.
    public int[] GetUniformLocations(string[] uniformNames)
    {
        ArgumentNullException.ThrowIfNull(uniformNames);
        if (_uniformLocationTables.TryGetValue(uniformNames, out int[]? existing))
        {
            return existing;
        }

        int[] locations = new int[uniformNames.Length];
        for (int i = 0; i < uniformNames.Length; i++)
        {
            locations[i] = GetUniformLocation(uniformNames[i]);
        }

        _uniformLocationTables[uniformNames] = locations;
        return locations;
    }

    public void SetMatrix4(string uniformName, Matrix4 matrix)
    {
        int location = GetUniformLocation(uniformName);
        GL.UniformMatrix4(location, false, ref matrix);
    }

    public void SetMatrix4(int location, Matrix4 matrix)
    {
        GL.UniformMatrix4(location, false, ref matrix);
    }

    public void SetVector4(string uniformName, Vector4 vector)
    {
        int location = GetUniformLocation(uniformName);
        GL.Uniform4(location, vector);
    }

    public void SetVector3(string uniformName, Vector3 vector)
    {
        int location = GetUniformLocation(uniformName);
        GL.Uniform3(location, vector);
    }

    public void SetVector2(string uniformName, Vector2 vector)
    {
        int location = GetUniformLocation(uniformName);
        GL.Uniform2(location, vector);
    }

    public void SetFloat(string uniformName, float value)
    {
        int location = GetUniformLocation(uniformName);
        GL.Uniform1(location, value);
    }

    public void SetInt(string uniformName, int value)
    {
        int location = GetUniformLocation(uniformName);
        GL.Uniform1(location, value);
    }

    public void SetBool(string uniformName, bool value)
    {
        int location = GetUniformLocation(uniformName);
        GL.Uniform1(location, value ? 1 : 0);
    }
    #endregion

    #region Resource Management
    public void Dispose()
    {
        if (Handle == 0)
        {
            return;
        }

        GLUtils.AssertRenderThread("Shader disposal");
        GL.DeleteProgram(Handle);
        Handle = 0;
        _uniformLocations.Clear();
        _uniformLocationTables.Clear();
    }
    #endregion
}
