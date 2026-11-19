using System.Text.RegularExpressions;
using Xunit;

namespace Dream.Tests.Rendering.Core;

// Wave 9 tester, GPU-7: AnimatedModel switches to a world program's MASK variant in the middle of
// a node and back. Uniform values live in each program object, so after a switch every uniform the
// MASK program's draw reads must have been written to THAT program, either on the switch
// (BindProgram) or per primitive (ApplySkinning, SetMaterialProperties); otherwise the variant
// draws with whatever an earlier frame or model left in it. Checked structurally: every uniform
// declared by the world shaders (struct members expanded) is written by those paths, and the
// switch back (RestoreProgram) only rebinds, which is enough because the base program's own
// values were never touched while the variant was bound.
public class Wave9AlphaMaskUniformCoverageTests
{
    [Theory]
    [InlineData("cubeFragment.glsl")]
    [InlineData("enemyFragment.glsl")]
    public void Every_Uniform_The_Mask_Program_Reads_Is_Written_After_A_Switch(string fragmentFile)
    {
        string shaders = Path.Combine(RepoRoot(), "dream", "shaders");
        string vertex = File.ReadAllText(Path.Combine(shaders, "cubeVertex.glsl"));
        string fragment = File.ReadAllText(Path.Combine(shaders, fragmentFile));
        // uTerrainVariation lives in the DREAM_TERRAIN block, a program that never has a MASK
        // variant (a WORLD_ALPHA_MASK build of it is an #error, pinned by TerrainSurfaceShaderContractTests).
        // The whole terrain block is stripped first (uTerrainVariation, uTerrainLayer, the blend and
        // relief uniforms, the tile array and the material map).
        List<string> uniforms = [.. DeclaredUniforms(vertex), .. DeclaredUniforms(StripTerrainBlocks(fragment))];
        Assert.Contains("model", uniforms);
        Assert.Contains("material.alphaCutoff", uniforms);

        string animatedModel = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Models", "AnimatedModel.cs"));
        string shaderSource = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Shader.cs"));
        string switchPaths =
            MethodBody(animatedModel, "private void BindProgram(") +
            MethodBody(animatedModel, "private void ApplySkinning(") +
            MethodBody(animatedModel, "private void SetMaterialProperties(");

        // Names written through constants or dedicated setters.
        Dictionary<string, bool> indirect = new()
        {
            ["model"] = switchPaths.Contains("SetModelMatrix(", StringComparison.Ordinal),
            ["normalMatrix"] = switchPaths.Contains("SetModelMatrix(", StringComparison.Ordinal) &&
                MethodBody(shaderSource, "public void SetModelMatrix(Matrix4 model)").Contains("_normalMatrixLocation", StringComparison.Ordinal),
            ["material.alphaCutoff"] = switchPaths.Contains("SetAlphaCutoff(", StringComparison.Ordinal),
            ["material.emissiveFactor"] = switchPaths.Contains("SetEmissiveFactor(", StringComparison.Ordinal),
            ["material.hasEmissiveTexture"] = switchPaths.Contains("SetHasEmissiveTexture(", StringComparison.Ordinal),
            ["material.emissiveUvScroll"] = switchPaths.Contains("SetEmissiveUvScroll(", StringComparison.Ordinal),
            ["uTime"] = switchPaths.Contains("SetTime(", StringComparison.Ordinal),
            ["uEquipSurface"] = switchPaths.Contains("SetEquipmentSurface(", StringComparison.Ordinal),
            ["uEquipEye"] = switchPaths.Contains("SetEquipmentEye(", StringComparison.Ordinal),
            ["jointMatrices"] = switchPaths.Contains("JointMatrixArrayUniformName", StringComparison.Ordinal),
        };

        List<string> missing = [];
        foreach (string uniform in uniforms.Distinct())
        {
            bool written = indirect.TryGetValue(uniform, out bool viaSetter)
                ? viaSetter
                : switchPaths.Contains($"\"{uniform}\"", StringComparison.Ordinal);
            if (!written)
            {
                missing.Add(uniform);
            }
        }

        Assert.True(missing.Count == 0, $"Not written after a switch to the MASK program: {string.Join(", ", missing)}");

        // The instanced/non-instanced flag is per program and set on every switch (a variant last
        // used instanced must not keep useInstancing = true for a non-instanced draw).
        string bindProgram = MethodBody(animatedModel, "private void BindProgram(");
        Assert.Contains("program.SetBool(\"useInstancing\", instanced);", bindProgram);
        // useSkinning is written for both outcomes of every primitive.
        string applySkinning = MethodBody(animatedModel, "private void ApplySkinning(");
        Assert.Contains("shader.SetBool(\"useSkinning\", false);", applySkinning);
        Assert.Contains("shader.SetBool(\"useSkinning\", true);", applySkinning);
    }

    // Removes every `#ifdef DREAM_TERRAIN ... #endif` block (programs that have no MASK variant),
    // honouring nested conditionals.
    private static string StripTerrainBlocks(string source)
    {
        System.Text.StringBuilder result = new();
        int depth = 0;
        bool inTerrain = false;
        foreach (string line in source.Split('\n'))
        {
            string trimmed = line.TrimStart();
            bool opens = trimmed.StartsWith("#if", StringComparison.Ordinal);
            if (!inTerrain && trimmed.StartsWith("#ifdef DREAM_TERRAIN", StringComparison.Ordinal))
            {
                inTerrain = true;
                depth = 1;
                continue;
            }

            if (inTerrain)
            {
                if (opens)
                {
                    depth++;
                }
                else if (trimmed.StartsWith("#endif", StringComparison.Ordinal) && --depth == 0)
                {
                    inTerrain = false;
                }

                continue;
            }

            result.Append(line).Append('\n');
        }

        return result.ToString();
    }

    // `uniform T name;` plus the members of `uniform Struct name;` as name.member.
    private static IEnumerable<string> DeclaredUniforms(string glsl)
    {
        string source = Regex.Replace(Regex.Replace(glsl, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"//[^\n]*", string.Empty);
        Dictionary<string, List<string>> structs = [];
        foreach (Match match in Regex.Matches(source, @"struct\s+(\w+)\s*\{(.*?)\}", RegexOptions.Singleline))
        {
            structs[match.Groups[1].Value] = Regex.Matches(match.Groups[2].Value, @"\w+\s+(\w+)\s*;")
                .Select(member => member.Groups[1].Value)
                .ToList();
        }

        foreach (Match match in Regex.Matches(source, @"uniform\s+(\w+)\s+(\w+)\s*(\[\s*\d+\s*\])?\s*;"))
        {
            string type = match.Groups[1].Value;
            string name = match.Groups[2].Value;
            if (structs.TryGetValue(type, out List<string>? members))
            {
                foreach (string member in members)
                {
                    yield return $"{name}.{member}";
                }
            }
            else
            {
                yield return name;
            }
        }
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found.");
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
                return source.Substring(open, i - open + 1);
            }
        }

        throw new InvalidOperationException($"{signature} has no closing brace.");
    }

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
