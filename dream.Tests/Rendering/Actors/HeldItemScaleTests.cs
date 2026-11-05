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

    // The Y extent of the item as DRAWN, computed here with a closed form that does not share code with
    // HeldItemScale: a box of size (sx, sy, sz) pitched about X by p and rolled about Z by r is centrally
    // symmetric, so its Y half-extent is (sx |sin r| + (sy |cos p| + sz |sin p|) |cos r|) / 2.
    private static float DrawnExtentModelUnits(string id)
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        foreach (WeaponPart part in WeaponMeshCatalog.Resolve(id).Parts)
        {
            float p = MathF.PI / 180f * part.PitchDegrees;
            float r = MathF.PI / 180f * part.RollDegrees;
            float half = 0.5f * ((part.Size.X * MathF.Abs(MathF.Sin(r)))
                + (((part.Size.Y * MathF.Abs(MathF.Cos(p))) + (part.Size.Z * MathF.Abs(MathF.Sin(p)))) * MathF.Abs(MathF.Cos(r))));
            min = MathF.Min(min, part.Center.Y - half);
            max = MathF.Max(max, part.Center.Y + half);
        }

        return max - min;
    }

    [Fact]
    public void EveryId_LandsWithin12PercentOfItsRealWorldLength_AsDrawn()
    {
        string[] ids = AllIds();
        Assert.Equal(23, ids.Length);
        foreach (string id in ids.Append(WeaponMeshCatalog.GenericVisualId))
        {
            // The drawn extent (rotations included) times the scale the renderer applies.
            float metres = DrawnExtentModelUnits(id) * HeldItemScale.Resolve(id) / HeldItemScale.ModelUnitsPerMetre;
            float target = HeldItemScale.TargetLengthMetres(id);
            Assert.True(MathF.Abs(metres - target) <= 0.12f * target, $"{id}: {metres:F3} m against {target:F3} m");
        }
    }

    [Fact]
    public void TheAuthoredLength_IsTheRotatedExtent_NotTheUnrotatedOne()
    {
        foreach (string id in AllIds().Append(WeaponMeshCatalog.GenericVisualId))
        {
            Assert.Equal(DrawnExtentModelUnits(id), HeldItemScale.AuthoredLengthModelUnits(id), 3);
        }

        // The buckler's 45 degree plate: 0.32 tall unrotated, 0.32 * (sin 45 + cos 45) = 0.4525 drawn.
        float unrotated = 0f;
        float lo = float.MaxValue;
        float hi = float.MinValue;
        foreach (WeaponPart part in WeaponMeshCatalog.Resolve("held.shield.buckler").Parts)
        {
            lo = MathF.Min(lo, part.Center.Y - (part.Size.Y * 0.5f));
            hi = MathF.Max(hi, part.Center.Y + (part.Size.Y * 0.5f));
        }

        unrotated = hi - lo;
        Assert.True(HeldItemScale.AuthoredLengthModelUnits("held.shield.buckler") > unrotated * 1.1f);
    }

    [Fact]
    public void NoParts_HaveNoExtent_AndAnItemWithNoPartsIsNotNaN()
    {
        Assert.Equal(0f, HeldItemScale.RotatedExtentModelUnits(Array.Empty<WeaponPart>()));
        Assert.True(float.IsFinite(HeldItemScale.RotatedExtentModelUnits([new WeaponPart(OpenTK.Mathematics.Vector3.Zero, OpenTK.Mathematics.Vector3.Zero, default)])));
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
