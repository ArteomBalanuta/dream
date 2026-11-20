using System.Text.RegularExpressions;
using Dream.Rendering.Post;
using Dream.Tests.Integration;
using Xunit;

namespace Dream.Tests.Rendering.Post;

// A uniform name that does not match the shader resolves to location -1, Shader.SetFloat skips the
// upload, and the uniform keeps GL's default of 0. For the resolve that means the grade or the bloom
// is simply OFF with a green suite. GL is unavailable in tests, so the three name arrays are checked
// against the declarations in the shader sources, in both directions.
public class PostChainUniformContractTests
{
    private static readonly Regex UniformDeclaration = new(@"\buniform\s+\w+\s+(\w+)\s*(?:\[\d+\])?\s*;");

    private static string Shader(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot.Find(), "dream", "shaders", file));

    private static string StripComments(string source)
    {
        string withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\n]*", string.Empty);
    }

    private static string[] Declared(string source) =>
        UniformDeclaration.Matches(StripComments(source))
            .Select(match => match.Groups[1].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static string[] Sorted(string[] names) => names.OrderBy(name => name, StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData(PostChain.BrightShaderFile)]
    [InlineData(PostChain.BlurShaderFile)]
    [InlineData(PostChain.ResolveShaderFile)]
    public void TheNameArray_MatchesTheDeclaredUniforms_InBothDirections(string file)
    {
        string[] names = file switch
        {
            PostChain.BrightShaderFile => PostChain.BrightUniformNames,
            PostChain.BlurShaderFile => PostChain.BlurUniformNames,
            _ => PostChain.ResolveUniformNames,
        };

        string[] declared = Declared(Shader(file));
        Assert.NotEmpty(declared);
        // Equal on the sorted sequences fails for a declared uniform missing from the array (never
        // uploaded) and for an array entry the shader does not declare (location -1).
        Assert.Equal(declared, Sorted(names));
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact]
    public void TheIncludedToneFile_DeclaresNoUniform_SoTheThreeFilesAreTheWholeContract()
    {
        Assert.Empty(Declared(Shader("postTone.glsl")));
        Assert.Empty(Declared(Shader(PostChain.VertexShaderFile)));
    }

    // The synthetic negative: a misspelt entry, and a missing one, must each be seen as a mismatch,
    // or the comparison above could not tell a typo from the real thing.
    [Fact]
    public void TheComparison_FailsForATypoAndForAMissingEntry()
    {
        string[] declared = Declared(Shader(PostChain.ResolveShaderFile));

        string[] typo = PostChain.ResolveUniformNames.ToArray();
        typo[^1] = typo[^1] + "s";
        Assert.NotEqual(declared, Sorted(typo));

        string[] missing = PostChain.ResolveUniformNames.Take(PostChain.ResolveUniformNames.Length - 1).ToArray();
        Assert.NotEqual(declared, Sorted(missing));
    }
}
