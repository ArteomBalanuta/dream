using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Actors;

namespace Dream.Tests.Rendering.Actors;

// The material -> surface table the equipment shader reads. Every value is a shader input, so the
// ranges here are the ones enemyFragment.glsl assumes; None has to stay all zeros, because that is
// the value every OTHER actor, enemy, NPC and prop draw leaves in the uniform.
public class EquipmentSurfaceTests
{
    public static TheoryData<WeaponMaterial> WeaponMaterials()
    {
        TheoryData<WeaponMaterial> data = new();
        foreach (WeaponMaterial material in Enum.GetValues<WeaponMaterial>())
        {
            data.Add(material);
        }

        return data;
    }

    public static TheoryData<ArmourMaterial> ArmourMaterials()
    {
        TheoryData<ArmourMaterial> data = new();
        foreach (ArmourMaterial material in Enum.GetValues<ArmourMaterial>())
        {
            data.Add(material);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(WeaponMaterials))]
    public void EveryWeaponMaterial_HasASurfaceInRange(WeaponMaterial material)
    {
        AssertInRange(EquipmentSurface.For(material), material.ToString());
    }

    [Theory]
    [MemberData(nameof(ArmourMaterials))]
    public void EveryArmourMaterial_HasASurfaceInRange(ArmourMaterial material)
    {
        AssertInRange(EquipmentSurface.For(material), material.ToString());
    }

    [Fact]
    public void None_IsAllZeros_SoTheShaderBlockIsSkipped()
    {
        Assert.Equal(0f, EquipmentSurface.None.Metallic);
        Assert.Equal(0f, EquipmentSurface.None.Roughness);
        Assert.Equal(0f, EquipmentSurface.None.Grain);
        Assert.Equal(0f, EquipmentSurface.None.AccentMix);
        Assert.Equal(Vector4.Zero, EquipmentSurface.None.ToUniform());
    }

    [Fact]
    public void AnUnknownMaterial_FallsBackToNone()
    {
        Assert.Equal(EquipmentSurface.None, EquipmentSurface.For((WeaponMaterial)999));
        Assert.Equal(EquipmentSurface.None, EquipmentSurface.For((ArmourMaterial)999));
    }

    [Fact]
    public void ToUniform_MapsTheFieldsInTheDocumentedOrder()
    {
        EquipmentSurface surface = new(0.1f, 0.2f, 0.3f, 0.4f);
        Vector4 uniform = surface.ToUniform();
        Assert.Equal(0.1f, uniform.X);
        Assert.Equal(0.2f, uniform.Y);
        Assert.Equal(0.3f, uniform.Z);
        Assert.Equal(0.4f, uniform.W);
    }

    private static void AssertInRange(EquipmentSurface surface, string material)
    {
        // Roughness is the shader's gate (uEquipSurface.y > 0.0), so a real material must be above
        // the authoring floor, never zero.
        Assert.True(
            surface.Roughness >= EquipmentSurface.MinRoughness && surface.Roughness <= 1f,
            $"{material} roughness {surface.Roughness}");
        Assert.InRange(surface.Metallic, 0f, 1f);
        Assert.InRange(surface.Grain, 0f, 1f);
        Assert.InRange(surface.AccentMix, 0f, 1f);
    }
}
