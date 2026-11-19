using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.RegularExpressions;
using Dream.Rendering.Lighting;
using Dream.Rendering.Mist;
using Dream.Rendering.Models;
using Dream.Rendering.Shadows;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Dream.Rendering.Core;

public sealed class Shader : IDisposable
{
    public int Handle { get; private set; }
    private readonly Dictionary<string, int> _uniformLocations = new();
    private readonly Dictionary<string[], int[]> _uniformLocationTables = new(ReferenceEqualityComparer.Instance);

    // Uniform names the per-draw paths upload through cached locations. They are resolved once,
    // right after linking, so a draw never asks GL (or a dictionary) for a location.
    public const string ModelUniformName = "model";
    public const string NormalMatrixUniformName = "normalMatrix";
    public const string AlphaCutoffUniformName = "material.alphaCutoff";
    public const string EmissiveFactorUniformName = "material.emissiveFactor";
    public const string HasEmissiveTextureUniformName = "material.hasEmissiveTexture";
    public const string EmissiveUvScrollUniformName = "material.emissiveUvScroll";
    public const string TimeUniformName = "uTime";

    // The equipment material response (enemyFragment.glsl). Only HeldWeaponRenderer and
    // WornArmourRenderer ever set them; every other draw leaves uEquipSurface at zero, which is
    // what makes the shader's whole material block a no-op. Programs that do not declare them
    // (cubeFragment.glsl, the HUD programs) resolve to -1 and upload nothing.
    public const string EquipmentSurfaceUniformName = "uEquipSurface";
    public const string EquipmentEyeUniformName = "uEquipEye";
    public const string EquipmentDetailUniformName = "uEquipDetail";

    // Defined in the fragment source of a world program's MASK variant only. The cutoff discard
    // sits behind it: a fragment shader that can discard loses early depth optimizations even when
    // the discard is never taken (measured: about double the GPU actors time), so OPAQUE programs
    // must not contain one.
    public const string AlphaMaskDefine = "WORLD_ALPHA_MASK";

    // Compiles cubeFragment.glsl as the terrain-only program (stochastic tile sampling and macro
    // variation behind uTerrainVariation). It has no MASK variant: terrain is opaque.
    public const string TerrainDefine = "DREAM_TERRAIN";

    private int _modelLocation = -1;
    private int _normalMatrixLocation = -1;
    private int _alphaCutoffLocation = -1;
    private int _emissiveFactorLocation = -1;
    private int _hasEmissiveTextureLocation = -1;
    private int _emissiveUvScrollLocation = -1;
    private int _timeLocation = -1;
    private int _equipmentSurfaceLocation = -1;
    private int _equipmentEyeLocation = -1;
    private int _equipmentDetailLocation = -1;

    // Last cutoff uploaded. Uniform values live in the program, so a value already there is not
    // uploaded again. Unknown until the first upload.
    private float _uploadedAlphaCutoff = float.NaN;

    // Last emissive factor uploaded, cached the same way. Almost every material is non-emissive
    // (0), so binding one after another uploads nothing. Unknown (NaN) until the first upload.
    private Vector3 _uploadedEmissiveFactor = new(float.NaN);

    // Last emissive texture flag, scroll and frame time uploaded, cached the same way. Unknown
    // (-1 / NaN) until the first upload.
    private int _uploadedHasEmissiveTexture = -1;
    private Vector2 _uploadedEmissiveUvScroll = new(float.NaN);
    private float _uploadedTime = float.NaN;

    // Last equipment surface and eye uploaded, cached the same way. Unknown (NaN) until the first
    // upload, so the first reset to zero after a pass is always really uploaded.
    private Vector4 _uploadedEquipmentSurface = new(float.NaN);
    private Vector3 _uploadedEquipmentEye = new(float.NaN);
    private Vector4 _uploadedEquipmentDetail = new(float.NaN);

    public Shader(string vertexPath, string fragmentPath, string? fragmentDefine = null)
    {
        GLUtils.AssertRenderThread("Shader creation");

        // Load sources from file. Includes resolve before the define goes in, so the define still
        // lands right after the shader's own #version.
        string vertexSource = ResolveIncludes(File.ReadAllText(vertexPath), DirectoryOf(vertexPath));
        string fragmentSource = ResolveIncludes(File.ReadAllText(fragmentPath), DirectoryOf(fragmentPath));
        if (fragmentDefine != null)
        {
            fragmentSource = InsertDefine(fragmentSource, fragmentDefine);
        }

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
        _modelLocation = GetUniformLocation(ModelUniformName);
        _normalMatrixLocation = GetUniformLocation(NormalMatrixUniformName);
        _alphaCutoffLocation = GetUniformLocation(AlphaCutoffUniformName);
        _emissiveFactorLocation = GetUniformLocation(EmissiveFactorUniformName);
        _hasEmissiveTextureLocation = GetUniformLocation(HasEmissiveTextureUniformName);
        _emissiveUvScrollLocation = GetUniformLocation(EmissiveUvScrollUniformName);
        _timeLocation = GetUniformLocation(TimeUniformName);
        _equipmentSurfaceLocation = GetUniformLocation(EquipmentSurfaceUniformName);
        _equipmentEyeLocation = GetUniformLocation(EquipmentEyeUniformName);
        _equipmentDetailLocation = GetUniformLocation(EquipmentDetailUniformName);
        // GL 3.3 core has no layout(binding = N) on a block, so the FrameLighting block (from
        // lighting.glsl) and the LightList block (pointLights.glsl) are pointed at their binding
        // points here, once per program; the BoundaryMist block (boundaryMist.glsl) and the FireShadowList block
        // (fireShadows.glsl) too. Programs that do not declare a block are left alone.
        FrameLightingBuffer.BindProgramBlock(program);
        LightListBuffer.BindProgramBlock(program);
        BoundaryMistBuffer.BindProgramBlock(program);
        FireShadowListBuffer.BindProgramBlock(program);
        GpuDebug.LabelObject(ObjectLabelIdentifier.Program, program,
            Path.GetFileName(vertexPath) + "+" + Path.GetFileName(fragmentPath)
            + (fragmentDefine != null ? " " + fragmentDefine : ""));
        GLUtils.CheckGLError("Shader creation");
    }

    // The program that draws this program's glTF MASK materials: the same sources compiled with
    // AlphaMaskDefine. Null for programs with no MASK variant (they draw every material opaque).
    // Owned by this shader and disposed with it.
    public Shader? AlphaMaskVariant { get; private set; }

    public void AttachAlphaMaskVariant(Shader variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        if (ReferenceEquals(variant, this) || AlphaMaskVariant != null)
        {
            throw new InvalidOperationException("A shader has at most one alpha-mask variant, and it is another program.");
        }

        AlphaMaskVariant = variant;
    }

    // Puts `#define <name>` on the line after `#version`, which must stay the first directive.
    public static string InsertDefine(string source, string define)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(define);

        int versionStart = source.IndexOf("#version", StringComparison.Ordinal);
        if (versionStart < 0)
        {
            return $"#define {define}\n{source}";
        }

        int lineEnd = source.IndexOf('\n', versionStart);
        return lineEnd < 0
            ? $"{source}\n#define {define}\n"
            : source.Insert(lineEnd + 1, $"#define {define}\n");
    }

    // Replaces each line `#include "file.glsl"` with the text of that file, read from `directory`
    // (the including shader's own folder). One level only: an included file may not contain an
    // #include or a #version, and a missing file throws. Other lines, and their line endings, are
    // kept as they are.
    public static string ResolveIncludes(string source, string directory)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(directory);
        if (!source.Contains("#include", StringComparison.Ordinal))
        {
            return source;
        }

        System.Text.StringBuilder resolved = new(source.Length);
        int lineStart = 0;
        while (lineStart < source.Length)
        {
            int newline = source.IndexOf('\n', lineStart);
            int lineEnd = newline < 0 ? source.Length : newline + 1;
            string line = source.Substring(lineStart, lineEnd - lineStart);
            if (TryParseInclude(line, out string? fileName))
            {
                resolved.Append(ReadInclude(fileName, directory));
                resolved.Append('\n');
            }
            else
            {
                resolved.Append(line);
            }

            lineStart = lineEnd;
        }

        return resolved.ToString();
    }

    private static bool TryParseInclude(string line, [NotNullWhen(true)] out string? fileName)
    {
        Match match = IncludePattern.Match(line);
        fileName = match.Success ? match.Groups[1].Value : null;
        return match.Success;
    }

    private static readonly Regex IncludePattern =
        new(@"^﻿?[ \t]*#[ \t]*include[ \t]+""([^""]+)""[ \t]*\r?\n?$");

    private static readonly Regex DirectivePattern =
        new(@"^[ \t]*#[ \t]*(include|version)\b", RegexOptions.Multiline);

    private static string ReadInclude(string fileName, string directory)
    {
        if (fileName != Path.GetFileName(fileName))
        {
            throw new InvalidOperationException($"Shader include \"{fileName}\" must name a file in the shader's own directory.");
        }

        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Shader include \"{fileName}\" not found in {directory}.", path);
        }

        string text = File.ReadAllText(path).TrimStart('﻿');
        Match directive = DirectivePattern.Match(text);
        if (directive.Success)
        {
            throw new InvalidOperationException(
                $"Shader include \"{fileName}\" contains #{directive.Groups[1].Value}; includes are one level and have no #version.");
        }

        return text.EndsWith('\n') ? text[..^1] : text;
    }

    private static string DirectoryOf(string path) => Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;

    public void Use()
    {
        GL.UseProgram(Handle);
        RenderCounters.CountProgramBind();
    }

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
    // instance, so callers pass a static array and get the same cached table back on every call.
    // The table is shared, so it is handed out read-only.
    public ReadOnlySpan<int> GetUniformLocations(string[] uniformNames)
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

    // A location of -1 means the program has no such active uniform (never declared, or optimized
    // out). GL ignores uploads to -1, so the setters skip the call and do not count it:
    // RenderCounters.UniformUploads counts only uploads that reach the program.
    public void SetMatrix4(string uniformName, Matrix4 matrix)
    {
        SetMatrix4(GetUniformLocation(uniformName), matrix);
    }

    public void SetMatrix4(int location, Matrix4 matrix)
    {
        if (location < 0)
        {
            return;
        }

        GL.UniformMatrix4(location, false, ref matrix);
        RenderCounters.CountUniformUpload();
    }

    public void SetMatrix3(int location, Matrix3 matrix)
    {
        if (location < 0)
        {
            return;
        }

        GL.UniformMatrix3(location, false, ref matrix);
        RenderCounters.CountUniformUpload();
    }

    // The per-draw model transform: uploads `model` and, when the program declares it, the
    // matching `normalMatrix` (transpose(inverse(mat3(model))), computed here on the CPU once per
    // draw instead of per vertex in the shader). Both locations are cached at link time.
    public void SetModelMatrix(Matrix4 model)
    {
        SetMatrix4(_modelLocation, model);
        if (_normalMatrixLocation >= 0)
        {
            SetMatrix3(_normalMatrixLocation, NormalMatrix.FromModel(in model));
        }
    }

    // The MASK cutoff (`material.alphaCutoff`), which only an AlphaMaskVariant program declares;
    // on an opaque program the location is -1 and nothing is uploaded. A value already in the
    // program is not uploaded again.
    public void SetAlphaCutoff(float alphaCutoff)
    {
        if (_alphaCutoffLocation < 0 || alphaCutoff.Equals(_uploadedAlphaCutoff))
        {
            return;
        }

        GL.Uniform1(_alphaCutoffLocation, alphaCutoff);
        RenderCounters.CountUniformUpload();
        _uploadedAlphaCutoff = alphaCutoff;
    }

    // The material emissive (`material.emissiveFactor`, both world fragment shaders). A value
    // already in the program is not uploaded again; a program without it uploads nothing.
    public void SetEmissiveFactor(Vector3 emissiveFactor)
    {
        if (_emissiveFactorLocation < 0 || emissiveFactor.Equals(_uploadedEmissiveFactor))
        {
            return;
        }

        GL.Uniform3(_emissiveFactorLocation, emissiveFactor);
        RenderCounters.CountUniformUpload();
        _uploadedEmissiveFactor = emissiveFactor;
    }

    // `material.hasEmissiveTexture`: gates the emissive sample in both world fragment shaders. A
    // program without it uploads nothing.
    public void SetHasEmissiveTexture(bool hasEmissiveTexture)
    {
        int value = hasEmissiveTexture ? 1 : 0;
        if (_hasEmissiveTextureLocation < 0 || value == _uploadedHasEmissiveTexture)
        {
            return;
        }

        GL.Uniform1(_hasEmissiveTextureLocation, value);
        RenderCounters.CountUniformUpload();
        _uploadedHasEmissiveTexture = value;
    }

    // `material.emissiveUvScroll`: emissive UV offset per second (zero = still).
    public void SetEmissiveUvScroll(Vector2 uvScroll)
    {
        if (_emissiveUvScrollLocation < 0 || uvScroll.Equals(_uploadedEmissiveUvScroll))
        {
            return;
        }

        GL.Uniform2(_emissiveUvScrollLocation, uvScroll);
        RenderCounters.CountUniformUpload();
        _uploadedEmissiveUvScroll = uvScroll;
    }

    // `uTime`: seconds from RenderFrameClock, read only by scrolling emissive materials.
    public void SetTime(float seconds)
    {
        if (_timeLocation < 0 || seconds.Equals(_uploadedTime))
        {
            return;
        }

        GL.Uniform1(_timeLocation, seconds);
        RenderCounters.CountUniformUpload();
        _uploadedTime = seconds;
    }

    // `uEquipSurface` (x metallic, y roughness, z grain, w accentMix): the equipment material
    // response in enemyFragment.glsl. Zero - EquipmentSurface.None, what the equipment renderers
    // set at the end of their pass - skips the whole block, so every other draw is unchanged. A
    // program without the uniform (it is not declared in cubeFragment.glsl, and a program may also
    // have optimized it out) has location -1 and uploads nothing, exactly like SetEmissiveFactor.
    public void SetEquipmentSurface(Vector4 surface)
    {
        if (_equipmentSurfaceLocation < 0 || surface.Equals(_uploadedEquipmentSurface))
        {
            return;
        }

        GL.Uniform4(_equipmentSurfaceLocation, surface);
        RenderCounters.CountUniformUpload();
        _uploadedEquipmentSurface = surface;
    }

    // `uEquipDetail` (x pattern id, y the part's size in metres, z pattern depth, w a per-item
    // seed): which procedural surface the equipment block draws and how big it is. Read only when
    // uEquipSurface is non-zero, so it needs no reset of its own - but it is still zeroed with the
    // rest on a program switch, because a stale value is a stale value.
    public void SetEquipmentDetail(Vector4 detail)
    {
        if (_equipmentDetailLocation < 0 || detail.Equals(_uploadedEquipmentDetail))
        {
            return;
        }

        GL.Uniform4(_equipmentDetailLocation, detail);
        RenderCounters.CountUniformUpload();
        _uploadedEquipmentDetail = detail;
    }

    // `uEquipEye`: the real eye in world space, for the equipment specular half vector. The frame
    // lighting block's uCameraPosition is the FOG origin (the isometric focus), not the eye, so the
    // specular needs its own. Cached and -1 tolerant like the setter above.
    public void SetEquipmentEye(Vector3 eye)
    {
        if (_equipmentEyeLocation < 0 || eye.Equals(_uploadedEquipmentEye))
        {
            return;
        }

        GL.Uniform3(_equipmentEyeLocation, eye);
        RenderCounters.CountUniformUpload();
        _uploadedEquipmentEye = eye;
    }

    public void SetVector4(string uniformName, Vector4 vector)
    {
        int location = GetUniformLocation(uniformName);
        if (location < 0)
        {
            return;
        }

        GL.Uniform4(location, vector);
        RenderCounters.CountUniformUpload();
    }

    public void SetVector3(string uniformName, Vector3 vector)
    {
        int location = GetUniformLocation(uniformName);
        if (location < 0)
        {
            return;
        }

        GL.Uniform3(location, vector);
        RenderCounters.CountUniformUpload();
    }

    public void SetVector2(string uniformName, Vector2 vector)
    {
        int location = GetUniformLocation(uniformName);
        if (location < 0)
        {
            return;
        }

        GL.Uniform2(location, vector);
        RenderCounters.CountUniformUpload();
    }

    public void SetFloat(string uniformName, float value)
    {
        int location = GetUniformLocation(uniformName);
        if (location < 0)
        {
            return;
        }

        GL.Uniform1(location, value);
        RenderCounters.CountUniformUpload();
    }

    public void SetInt(string uniformName, int value)
    {
        int location = GetUniformLocation(uniformName);
        if (location < 0)
        {
            return;
        }

        GL.Uniform1(location, value);
        RenderCounters.CountUniformUpload();
    }

    public void SetBool(string uniformName, bool value)
    {
        int location = GetUniformLocation(uniformName);
        if (location < 0)
        {
            return;
        }

        GL.Uniform1(location, value ? 1 : 0);
        RenderCounters.CountUniformUpload();
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
        Shader? variant = AlphaMaskVariant;
        AlphaMaskVariant = null;
        try
        {
            variant?.Dispose();
        }
        finally
        {
            GL.DeleteProgram(Handle);
            Handle = 0;
            _uniformLocations.Clear();
            _uniformLocationTables.Clear();
            _modelLocation = -1;
            _normalMatrixLocation = -1;
            _alphaCutoffLocation = -1;
            _emissiveFactorLocation = -1;
            _hasEmissiveTextureLocation = -1;
            _emissiveUvScrollLocation = -1;
            _timeLocation = -1;
            _equipmentSurfaceLocation = -1;
            _equipmentEyeLocation = -1;
            _equipmentDetailLocation = -1;
        }
    }
    #endregion
}

// The CPU side of the cube/enemy shaders' `uniform mat3 normalMatrix`.
public static class NormalMatrix
{
    // transpose(inverse(mat3(model))) in OpenTK's row-vector layout, ready to upload with
    // transpose = false exactly like the model matrix. It is the cofactor matrix of the upper 3x3
    // divided by its determinant, so rotation, translation and any (non-uniform) scale give the
    // same result as Matrix4.Invert + Transpose, without inverting a 4x4. A singular (zero-scale)
    // matrix returns the unscaled cofactor matrix instead of throwing; its direction is still
    // usable, and the fragment shaders normalize the normal anyway.
    public static Matrix3 FromModel(in Matrix4 model)
    {
        Vector3 r0 = model.Row0.Xyz;
        Vector3 r1 = model.Row1.Xyz;
        Vector3 r2 = model.Row2.Xyz;

        Vector3 c0 = Vector3.Cross(r1, r2);
        Vector3 c1 = Vector3.Cross(r2, r0);
        Vector3 c2 = Vector3.Cross(r0, r1);
        float determinant = Vector3.Dot(r0, c0);
        float scale = float.IsFinite(determinant) && MathF.Abs(determinant) > 1e-30f
            ? 1f / determinant
            : 1f;

        return new Matrix3(c0 * scale, c1 * scale, c2 * scale);
    }
}

// The material alpha modes the world pass (cubeFragment, enemyFragment) supports. Wave 8 turned
// blending off in the world pass and there is no sorted transparency, so only OPAQUE and MASK
// exist there: glTF BLEND is drawn as OPAQUE. MASK materials draw with the program's
// AlphaMaskVariant, which discards fragments whose alpha is below the material's cutoff; OPAQUE
// materials draw with the base program, which has no discard at all.
public static class WorldAlphaMode
{
    public const int Opaque = 0;
    public const int Mask = 1;
}
