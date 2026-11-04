using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Dream.Rendering.Mist;
using Dream.Tests.Utilities;
using Xunit;

namespace Dream.Tests.Rendering.Mist;

// GL is unavailable in tests, so the war mist pass is checked through its sources: the shader
// contract, the GL state it sets and restores, and its place in the effects pass.
public class WarMistStructuralTests
{
    private static readonly Regex UniformDeclaration = new(@"\buniform\s+\w+\s+(\w+)\s*(?:\[\d+\])?\s*;");

    [Fact]
    public void Every_Declared_Uniform_Is_In_The_Table_And_Uploaded_Once_Per_Draw()
    {
        HashSet<string> declared = new(StringComparer.Ordinal);
        foreach (string file in new[] { WarMistRenderer.VertexShaderFile, WarMistRenderer.FragmentShaderFile })
        {
            foreach (Match match in UniformDeclaration.Matches(StripComments(ReadShader(file))))
            {
                declared.Add(match.Groups[1].Value);
            }
        }

        Assert.NotEmpty(declared);
        Assert.Equal(declared.OrderBy(name => name), WarMistRenderer.UniformNames.OrderBy(name => name));

        string renderer = ReadSource("dream/Rendering/Mist/WarMistRenderer.cs");
        string draw = Method(renderer, "public void Draw(");
        Assert.DoesNotContain("GetUniformLocation(", draw);
        Assert.Equal(
            WarMistRenderer.UniformNames.Length,
            Regex.Matches(draw, @"_locations\[Location\w+\]").Select(match => match.Value).Distinct().Count());
        Assert.Contains("_shader.GetUniformLocations(UniformNames)", renderer);
    }

    [Fact]
    public void The_Slice_Table_In_The_Shader_Has_The_Size_The_Settings_Allow()
    {
        string vertex = StripComments(ReadShader(WarMistRenderer.VertexShaderFile));

        Assert.Contains($"uniform float uSliceHeights[{WarMistSettings.MaxSlices}];", vertex);
        Assert.Equal(WarMistSettings.MaxSlices, WarMistField.CreateSliceHeights(WarMistSettings.DefaultSlices).Length);
    }

    [Fact]
    public void Vertex_Shader_Builds_The_Strip_From_The_Ids_And_Reads_No_Lighting()
    {
        string vertex = StripComments(ReadShader(WarMistRenderer.VertexShaderFile));

        Assert.StartsWith("#version 330 core", vertex.TrimStart('﻿'));
        Assert.Contains("vec2(gl_VertexID & 1, gl_VertexID >> 1)", vertex);
        Assert.Contains("uSliceHeights[gl_InstanceID]", vertex);
        Assert.DoesNotContain("#include", vertex);
        Assert.DoesNotContain("uCameraPosition", vertex);
    }

    [Fact]
    public void Fragment_Shader_Includes_The_Lighting_Read_Only_And_Fades_With_The_Fog_Without_Discard()
    {
        string fragment = StripComments(ReadShader(WarMistRenderer.FragmentShaderFile));

        Assert.Single(Regex.Matches(fragment, @"^\s*#include ""lighting\.glsl""\s*$", RegexOptions.Multiline));
        Assert.Matches(@"FragColor = vec4\(lit \* alpha, alpha\) \* dreamFogTransmittance\(vWorldPos\);", fragment);
        Assert.DoesNotContain("discard", fragment);
        Assert.DoesNotContain("dreamApplyFog", fragment);
        Assert.DoesNotContain("exp2(", fragment);
        Assert.DoesNotContain("BoundaryMist", fragment);
    }

    [Fact]
    public void Fragment_Constants_Match_The_Field()
    {
        string fragment = StripComments(ReadShader(WarMistRenderer.FragmentShaderFile));

        Assert.Equal(WarMistField.ClearRadius, ShaderConstant(fragment, "ClearRadius"));
        Assert.Equal(WarMistField.FullRadius, ShaderConstant(fragment, "FullRadius"));
        Assert.Equal(WarMistField.UnexploredDensity, ShaderConstant(fragment, "UnexploredDensity"));
        Assert.Equal(WarMistField.ExploredDensity, ShaderConstant(fragment, "ExploredDensity"));
        Assert.Equal(WarMistField.MaxTargetDensity, ShaderConstant(fragment, "MaxTargetDensity"));
        Assert.Equal(WarMistField.NoiseModMin, ShaderConstant(fragment, "NoiseModMin"));
        Assert.Equal(WarMistField.NoiseModMax, ShaderConstant(fragment, "NoiseModMax"));
        Assert.Contains("smoothstep(ClearRadius, FullRadius, distanceToFocus)", fragment);
        Assert.Contains("1.0 - pow(1.0 - target, 1.0 / uSliceCount)", fragment);
        Assert.Equal(2, Regex.Matches(fragment, @"texture\(uNoise,").Count);
    }

    [Fact]
    public void Draw_Sets_Its_State_Restores_It_And_Allocates_Nothing()
    {
        string draw = Method(ReadSource("dream/Rendering/Mist/WarMistRenderer.cs"), "public void Draw(");

        int cullQueryAt = IndexOf(draw, "bool cullWasEnabled = GL.IsEnabled(EnableCap.CullFace);");
        int depthTestAt = IndexOf(draw, "GL.Enable(EnableCap.DepthTest);");
        int writesOffAt = IndexOf(draw, "GL.DepthMask(false);");
        int blendOnAt = IndexOf(draw, "GL.Enable(EnableCap.Blend);");
        int blendFuncAt = IndexOf(draw, "GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);");
        int cullOffAt = IndexOf(draw, "GL.Disable(EnableCap.CullFace);");
        int noiseBindAt = IndexOf(draw, "GL.BindTexture(TextureTarget.Texture2D, _noise);");
        int explorationAt = IndexOf(draw, "_exploration.Bind(memory);");
        int drawAt = IndexOf(draw, "GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, _sliceCount);");
        int countAt = IndexOf(draw, "RenderCounters.CountDraw();");
        int writesOnAt = IndexOf(draw, "GL.DepthMask(true);");
        int blendOffAt = IndexOf(draw, "GL.Disable(EnableCap.Blend);");
        int cullOnAt = IndexOf(draw, "GL.Enable(EnableCap.CullFace);");

        Assert.DoesNotContain("return;", draw);
        Assert.True(cullQueryAt < depthTestAt && cullQueryAt < cullOffAt);
        Assert.True(depthTestAt < drawAt && writesOffAt < drawAt && blendOnAt < drawAt && blendFuncAt < drawAt && cullOffAt < drawAt);
        Assert.True(noiseBindAt < explorationAt && explorationAt < drawAt);
        Assert.True(drawAt < countAt && countAt < writesOnAt && countAt < blendOffAt && countAt < cullOnAt);
        Assert.Contains("GL.BindVertexArray(0);", draw[drawAt..]);
        Assert.Contains("GL.ActiveTexture(TextureUnit.Texture0);", draw[drawAt..]);
        Assert.DoesNotContain("GL.Disable(EnableCap.DepthTest)", draw);
        Assert.DoesNotContain("new ", draw.Replace("new ObjectDisposedException(", string.Empty));
        Assert.DoesNotContain("foreach", draw);
        Assert.DoesNotContain("=>", draw);
    }

    [Fact]
    public void Renderer_Asserts_The_Render_Thread_And_Releases_Everything()
    {
        string source = ReadSource("dream/Rendering/Mist/WarMistRenderer.cs");
        string constructor = Method(source, "public WarMistRenderer(");
        string dispose = Method(source, "public void Dispose()");

        Assert.Contains("GLUtils.AssertRenderThread(", constructor);
        Assert.Contains("GLUtils.AssertRenderThread(", dispose);
        Assert.Contains("_shader?.Dispose();", dispose);
        Assert.Contains("_exploration.Dispose();", dispose);
        Assert.Contains("GL.DeleteTexture(_noise);", dispose);
        Assert.Contains("GL.DeleteVertexArray(_vertexArray);", dispose);
    }

    [Fact]
    public void The_Exploration_Texture_Uploads_Only_When_Dirty_And_Aligns_Single_Byte_Rows()
    {
        string source = ReadSource("dream/Rendering/Mist/ExplorationTexture.cs");
        string bind = Method(source, "public void Bind(");
        string upload = Method(source, "private void Upload(");

        Assert.Contains("if (memory.Dirty)", bind);
        Assert.Contains("PixelStoreParameter.UnpackAlignment, 1", upload);
        Assert.Contains("PixelInternalFormat.R8", upload);
        Assert.Contains("GL.TexSubImage2D(", upload);
        Assert.Contains("memory.ClearDirty();", upload);
    }

    [Fact]
    public void The_Noise_Is_Deterministic_Different_Per_Seed_Tileable_And_Not_Flat()
    {
        byte[] first = WarMistNoiseTexture.Generate();
        byte[] second = WarMistNoiseTexture.Generate();
        byte[] other = WarMistNoiseTexture.Generate(WarMistNoiseTexture.Seed + 1);

        Assert.Equal(WarMistNoiseTexture.Size * WarMistNoiseTexture.Size * 2, first.Length);
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);

        for (int channel = 0; channel < 2; channel++)
        {
            double mean = 0;
            for (int i = channel; i < first.Length; i += 2)
            {
                mean += first[i];
            }

            mean /= first.Length / 2;
            double variance = 0;
            for (int i = channel; i < first.Length; i += 2)
            {
                variance += (first[i] - mean) * (first[i] - mean);
            }

            double deviation = Math.Sqrt(variance / (first.Length / 2));
            Assert.InRange(mean, 90, 165);
            Assert.True(deviation > 15, $"channel {channel} is too flat: {deviation}");

            // Tileable: the step across the wrap seam is no bigger than a step inside the tile.
            double seam = 0;
            double inside = 0;
            int size = WarMistNoiseTexture.Size;
            for (int line = 0; line < size; line++)
            {
                seam += Math.Abs(At(first, 0, line, channel) - At(first, size - 1, line, channel));
                seam += Math.Abs(At(first, line, 0, channel) - At(first, line, size - 1, channel));
                for (int step = 0; step < size - 1; step++)
                {
                    inside += Math.Abs(At(first, step, line, channel) - At(first, step + 1, line, channel));
                    inside += Math.Abs(At(first, line, step, channel) - At(first, line, step + 1, channel));
                }
            }

            double seamMean = seam / (2 * size);
            double insideMean = inside / (2 * size * (size - 1));
            Assert.True(seamMean < insideMean * 1.5, $"channel {channel} seam {seamMean} vs inside {insideMean}");
        }
    }

    [Fact]
    public void The_War_Mist_Never_References_The_Boundary_Mist()
    {
        string directory = Path.Combine(RepoRoot(), "dream", "Rendering", "Mist");
        string[] files =
        [
            .. Directory.GetFiles(directory, "WarMist*.cs"),
            Path.Combine(directory, "ExplorationMemory.cs"),
            Path.Combine(directory, "ExplorationTexture.cs"),
            Path.Combine(RepoRoot(), "dream", "Client", "Scenes", "PlayScene.WarMist.cs"),
            Path.Combine(RepoRoot(), "dream", "shaders", "warMistVertex.glsl"),
            Path.Combine(RepoRoot(), "dream", "shaders", "warMistFragment.glsl"),
        ];

        Assert.True(files.Length >= 9);
        foreach (string file in files)
        {
            Assert.DoesNotContain("BoundaryMist", File.ReadAllText(file));
        }
    }

    [Fact]
    public void The_Pass_Is_The_First_Statement_Of_The_Effects_Pass_Before_Its_Guard()
    {
        string pass = Method(ReadSource("dream/Client/Scenes/PlayScene.cs"), "private void DrawEffectsPass(");

        int warMistAt = IndexOf(pass, "DrawWarMistPass(in presentation, deltaSeconds);");
        int guardAt = IndexOf(pass, "if (_fxRenderer is not { } renderer");
        Assert.True(warMistAt < guardAt);
        string beforeCall = pass[(pass.IndexOf('{') + 1)..warMistAt];
        Assert.Equal(string.Empty, beforeCall.Trim());
        Assert.Single(Regex.Matches(pass, @"DrawWarMistPass\("));
    }

    [Fact]
    public void The_Scene_Draws_Only_In_The_Isometric_View_Creates_Lazily_And_Disposes_With_The_Effects()
    {
        string source = ReadSource("dream/Client/Scenes/PlayScene.cs");
        string pass = Method(source, "private void DrawWarMistPass(");
        string disposeEffects = Method(source, "private void DisposeEffects()");
        string disposeWarMist = Method(source, "private void DisposeWarMist()");

        Assert.Contains("_settings.CameraViewMode != CameraViewMode.Isometric", pass);
        Assert.Contains("_settings.WarMistEnabled", pass);
        Assert.Contains("new WarMistRenderer(", pass);
        Assert.Contains("RenderFaultLog.Shared.Report(\"war-mist\", exception);", pass);
        Assert.Contains("_runtime.LastCameraFocus", pass);
        Assert.Contains("DisposeWarMist();", disposeEffects);
        Assert.Contains("_warMistRenderer?.Dispose();", disposeWarMist);
        Assert.Contains("_warMistRenderer = null;", disposeWarMist);
        Assert.DoesNotContain("GpuTimerSection", pass);
    }

    private static float ShaderConstant(string shader, string name)
    {
        Match match = Regex.Match(shader, @"const float " + name + @" = ([0-9.]+);");
        Assert.True(match.Success, name);
        return float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static int IndexOf(string source, string value)
    {
        int index = source.IndexOf(value, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Missing: {value}");
        return index;
    }

    private static int At(byte[] bytes, int x, int y, int channel) =>
        bytes[(y * WarMistNoiseTexture.Size + x) * 2 + channel];

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[start..(i + 1)];
            }
        }

        throw new InvalidOperationException("Unbalanced braces.");
    }

    private static string StripComments(string source)
    {
        string withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\n]*", string.Empty);
    }

    private static string ReadShader(string fileName) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "dream", "shaders", fileName));

    private static string ReadSource(string relative) =>
        PartialClassSource.Read(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
