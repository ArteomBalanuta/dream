using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Actors;

public class HeldItemScaleTests
{
    private static string[] AllIds() => WeaponMeshCatalog.KnownVisualIds.ToArray();

    [Fact]
    public void TheMetreAnchor_MatchesTheAuthoredFigure()
    {
        float anchored = HeldItemScale.ModelUnitsPerMetre * HeldItemScale.AuthoredFigureHeightMetres;
        Assert.Equal(PlayerModelAssetDefinition.AuthoredDimensions.Height, anchored, 0.01f);
    }

    [Fact]
    public void EveryId_LandsWithin12PercentOfItsRealWorldLength()
    {
        string[] ids = AllIds();
        Assert.Equal(23, ids.Length);
        foreach (string id in ids.Append(WeaponMeshCatalog.GenericVisualId))
        {
            float metres = HeldItemScale.AuthoredLengthModelUnits(id) * HeldItemScale.Resolve(id) / HeldItemScale.ModelUnitsPerMetre;
            float target = HeldItemScale.TargetLengthMetres(id);
            Assert.True(MathF.Abs(metres - target) <= 0.12f * target, $"{id}: {metres:F3} m against {target:F3} m");
        }
    }

    [Fact]
    public void NoHeldItemExceedsTheFigure_ExceptTheSpear()
    {
        float figure = PlayerModelAssetDefinition.TargetWorldDimensions.Height;
        foreach (string id in AllIds().Append(WeaponMeshCatalog.GenericVisualId))
        {
            float world = HeldItemScale.AuthoredLengthModelUnits(id) * HeldItemScale.Resolve(id) * PlayerModelAssetDefinition.VisualScale;
            Assert.True(world <= 1.15f * figure, $"{id}: {world:F3} world units");
            Assert.Equal(id == "held.weapon.spear", world > 1.00f * figure);
        }
    }

    [Fact]
    public void TheOutliers_AreFixedInTheMeshTable_NotByScale()
    {
        foreach (string id in new[] { "held.weapon.dagger", "held.tool.torch", "held.weapon.hand-axe", "held.weapon.sword" })
        {
            Assert.InRange(HeldItemScale.Resolve(id), 0.9f, 1.12f);
        }
    }
}
