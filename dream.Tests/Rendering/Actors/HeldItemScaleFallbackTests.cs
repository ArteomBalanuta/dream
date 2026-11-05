using Dream.Rendering.Actors;
using Dream.Rendering.Models;
using Xunit;

namespace Dream.Tests.Rendering.Actors;

// An id the catalog does not know (a renamed item, a newer server, a corrupt save) must draw as the
// generic stand-in at a sane size: never invisible (scale 0, NaN, negative) and never enormous.
public class HeldItemScaleFallbackTests
{
    public static TheoryData<string> Unknown() => new()
    {
        "",
        " ",
        "held.weapon.does-not-exist",
        "HELD.WEAPON.SWORD",
        "held.weapon.sword ",
        "held.",
        "sword",
        "held.shield.",
        "\0",
        "held.weapon.sword\n",
    };

    [Theory]
    [MemberData(nameof(Unknown))]
    public void An_Unknown_Id_Resolves_To_The_Generic_Scale_Which_Is_Finite_Positive_And_Not_Huge(string id)
    {
        Assert.False(WeaponMeshCatalog.IsKnown(id));
        float scale = HeldItemScale.Resolve(id);
        Assert.True(float.IsFinite(scale) && scale > 0f, $"scale {scale}");
        Assert.Equal(HeldItemScale.Resolve(WeaponMeshCatalog.GenericVisualId), scale);
        Assert.Equal(HeldItemScale.AuthoredLengthModelUnits(WeaponMeshCatalog.GenericVisualId), HeldItemScale.AuthoredLengthModelUnits(id));

        float world = HeldItemScale.AuthoredLengthModelUnits(id) * scale * PlayerModelAssetDefinition.VisualScale;
        Assert.InRange(world, 0.05f, PlayerModelAssetDefinition.TargetWorldDimensions.Height);
    }

    [Fact]
    public void A_Null_Id_Is_Handled_Like_Any_Unknown_Id()
    {
        float scale = HeldItemScale.Resolve(null!);
        Assert.Equal(HeldItemScale.Resolve(WeaponMeshCatalog.GenericVisualId), scale);
        Assert.True(float.IsFinite(HeldItemScale.AuthoredLengthModelUnits(null!)));
    }

    [Fact]
    public void The_Generic_Stand_In_Has_A_Real_Mesh_So_The_Fallback_Is_Visible()
    {
        Assert.NotEmpty(WeaponMeshCatalog.Resolve(WeaponMeshCatalog.GenericVisualId).Parts);
        Assert.True(HeldItemScale.AuthoredLengthModelUnits(WeaponMeshCatalog.GenericVisualId) > 0.1f);
    }
}
