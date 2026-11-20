using System.Text.RegularExpressions;
using Dream.Rendering.Actors;
using Xunit;

namespace Dream.Tests.Rendering.Core;

// The world hover's rim light (uActorRim in enemyFragment.glsl) is pinned through its sources: GL cannot open
// in a unit test, ShaderCompileTests proves it compiles, and these prove it is the shape the design needs.
public sealed class ActorRimShaderContractTests
{
    private static readonly string Shader = Read("dream", "shaders", "enemyFragment.glsl");

    [Fact]
    public void TheRimUniforms_AreDeclared()
    {
        Assert.Contains("uniform vec4 uActorRim;", Shader);
        Assert.Contains("uniform vec3 uActorRimEye;", Shader);
        Assert.Matches(@"const float ActorRimPower = [1-3]\.\d+;", Shader);
        // The flat fill is a clamped share of the same colour, so it can never exceed the edge.
        Assert.Contains("mix(pow(1.0 - clamp(rimNdv, 0.0, 1.0), ActorRimPower), 1.0, clamp(uActorRim.w, 0.0, 1.0))", Shader);
    }

    [Fact]
    public void TheRimBlock_IsGuardedOnAPositiveColour()
    {
        Assert.Contains("if (uActorRim.r + uActorRim.g + uActorRim.b > 0.0) {", Shader);
        // After the equipment branch, before the emissive / exposure line.
        string block = Block();
        Assert.True(Shader.IndexOf(block, StringComparison.Ordinal) > Shader.IndexOf("if (uEquipSurface.y > 0.0)", StringComparison.Ordinal));
        Assert.True(Shader.IndexOf(block, StringComparison.Ordinal) < Shader.IndexOf("dreamExpose(lit)", StringComparison.Ordinal));
    }

    [Fact]
    public void BackFaces_AddNothing_BecauseNdvIsClamped()
    {
        string block = Block();
        Assert.Contains("clamp(rimNdv, 0.0, 1.0)", block);
        Assert.Contains("(rimNdv > 0.0 ? 1.0 : 0.0)", block);
        Assert.Contains("uActorRimEye - fragPos", block);
        Assert.DoesNotContain("uCameraPosition", block);
    }

    [Fact]
    public void TheRim_IsCappedByTheHeadroomUnderTheClipPoint()
    {
        string block = Block();
        Assert.Contains("1.0 / max(uLightingParams.x, 0.01)", block);
        Assert.Contains("hoverPeak > hoverRoom ? hoverRoom / hoverPeak : 1.0", block);
    }

    [Fact]
    public void DreamDirect_IsTextuallyUntouched_ByThisWave()
    {
        // The shared lighting include is not owned by this wave: its dreamDirect body is the engine contract.
        string lighting = Read("dream", "shaders", "lighting.glsl");
        Assert.Contains("dreamDirect(", lighting);
        Assert.DoesNotContain("uActorRim", lighting);
        Assert.DoesNotContain("uActorRim", Read("dream", "shaders", "surface.glsl"));
    }

    [Fact]
    public void TheDyingDraw_NeverRims()
    {
        string source = Read("dream", "Rendering", "Actors", "EnemyRenderer.cs");
        Assert.Contains("ApplyTint(model, shader, dying.Last.TintOffset, Vector4.Zero, camera.Position);", source);
        // The live draw takes its rim from the highlight; nothing else does.
        Assert.Single(Regex.Matches(source, @"ActorHighlight\.RimFor\("));
        // The reset covers the base program and the MASK variant.
        Assert.Contains("maskProgram.SetVector4(RimUniformName, Vector4.Zero)", source);
        Assert.Contains("_tintShader.SetVector4(RimUniformName, Vector4.Zero)", source);
    }

    [Fact]
    public void TheVillager_ZeroesTheRimRightAfterItsDraw()
    {
        string source = Read("dream", "Rendering", "Actors", "NpcModelRenderer.cs");
        int draw = source.IndexOf("model.RenderPrepared(shader, instance, modelMatrix);", StringComparison.Ordinal);
        int zero = source.IndexOf("ZeroTint(shader);", draw, StringComparison.Ordinal);
        Assert.True(draw > 0 && zero > draw);
        Assert.Contains("model.ActorRim = Vector4.Zero;", source.Substring(zero, 120));
        string zeroTint = source.Substring(source.IndexOf("private static void ZeroTint", StringComparison.Ordinal), 600);
        Assert.Contains("shader.SetVector4(RimUniformName, Vector4.Zero);", zeroTint);
        Assert.Contains("maskProgram.SetVector4(RimUniformName, Vector4.Zero);", zeroTint);
    }

    [Theory]
    [InlineData("HeldWeaponRenderer.cs")]
    [InlineData("WornArmourRenderer.cs")]
    public void TheEquipmentPasses_ZeroTheRim_WhereTheyZeroTheTint(string file)
    {
        string source = Read("dream", "Rendering", "Actors", file);
        int tint = source.IndexOf("shader.SetVector3(\"uActorTintOffset\", Vector3.Zero);", StringComparison.Ordinal);
        Assert.True(tint > 0);
        Assert.Contains("shader.SetVector4(\"uActorRim\", Vector4.Zero);", source.Substring(tint, 150));
    }

    [Fact]
    public void AnimatedModel_CarriesTheRimToASwitchedProgram()
    {
        string source = Read("dream", "Rendering", "Models", "AnimatedModel.cs");
        Assert.Contains("program.SetVector4(\"uActorRim\", ActorRim);", source);
        Assert.Contains("program.SetVector3(\"uActorRimEye\", ActorRimEye);", source);
    }

    // The real gate on the block's place and shape is the post-floor contract in FrameLightingContractTests: it
    // must accept the shipped shader and reject an unguarded block, a second block, and a bare statement after it.
    [Fact]
    public void ThePostFloorContract_RejectsAnUnguardedDuplicatedOrTrailingRim()
    {
        const string Guard = "if (uActorRim.r + uActorRim.g + uActorRim.b > 0.0) {";
        Assert.Matches(Dream.Tests.Rendering.Lighting.FrameLightingContractTests.PostFloorContract, Shader);

        string unguarded = Shader.Replace(Guard, "{", StringComparison.Ordinal);
        Assert.NotEqual(Shader, unguarded);
        Assert.DoesNotMatch(Dream.Tests.Rendering.Lighting.FrameLightingContractTests.PostFloorContract, unguarded);

        string block = Block();
        string whole = block + "\n    }";
        string duplicated = Shader.Replace(whole, whole + "\n    " + whole, StringComparison.Ordinal);
        Assert.NotEqual(Shader, duplicated);
        Assert.DoesNotMatch(Dream.Tests.Rendering.Lighting.FrameLightingContractTests.PostFloorContract, duplicated);

        string trailing = Shader.Replace(whole, whole + "\n    lit += vec3(0.1);", StringComparison.Ordinal);
        Assert.NotEqual(Shader, trailing);
        Assert.DoesNotMatch(Dream.Tests.Rendering.Lighting.FrameLightingContractTests.PostFloorContract, trailing);
    }

    // A still camera's hovered enemy must be rimmed toward the eye, not the world origin: other draws of the shared
    // program write a zero eye into it, so the enemy renderer may not skip the eye upload on a cached value.
    [Fact]
    public void TheEnemyRenderer_UploadsTheEye_OnEveryRimmedDraw()
    {
        string source = Read("dream", "Rendering", "Actors", "EnemyRenderer.cs");
        Assert.DoesNotContain("_appliedRimEye", source);
        Assert.Contains("if (!rim.Equals(Vector4.Zero))\n        {\n            shader.SetVector3(RimEyeUniformName, rimEye);", source.Replace("\r\n", "\n"));
    }

    private static string Block()
    {
        int start = Shader.IndexOf("if (uActorRim.r + uActorRim.g + uActorRim.b > 0.0) {", StringComparison.Ordinal);
        Assert.True(start > 0);
        int end = Shader.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return Shader.Substring(start, end - start);
    }

    private static string Read(params string[] parts)
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "dream", "dream.csproj")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        return File.ReadAllText(Path.Combine(new[] { root! }.Concat(parts).ToArray())).ReplaceLineEndings("\n");
    }
}
