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

    // What a person would call a plausible length for each item, in metres. Written out here on purpose and
    // NOT derived from HeldItemScale.TargetLengthMetres: Resolve is defined as target / authored length, so
    // any check of the form "drawn length is near the target" is true for every target whatsoever.
    private static readonly Dictionary<string, (float Min, float Max)> PlausibleMetres = new()
    {
        ["held.weapon.dagger"] = (0.25f, 0.50f),
        ["held.weapon.short-sword"] = (0.60f, 0.90f),
        ["held.weapon.sword"] = (0.80f, 1.10f),
        ["held.weapon.greatsword"] = (1.30f, 1.80f),
        ["held.weapon.axe"] = (0.70f, 1.10f),
        ["held.weapon.hand-axe"] = (0.40f, 0.70f),
        ["held.weapon.mace"] = (0.55f, 0.95f),
        ["held.weapon.hammer"] = (0.80f, 1.30f),
        ["held.weapon.spear"] = (1.80f, 2.40f),
        ["held.weapon.bow"] = (1.40f, 1.90f),
        ["held.weapon.short-bow"] = (0.90f, 1.30f),
        ["held.weapon.staff"] = (1.50f, 2.00f),
        ["held.weapon.bone-staff"] = (1.40f, 1.90f),
        ["held.weapon.iron-blade"] = (0.70f, 1.10f),
        ["held.gear.ward-lantern"] = (0.25f, 0.50f),
        ["held.shield.buckler"] = (0.30f, 0.50f),
        ["held.shield.kite"] = (0.80f, 1.10f),
        ["held.shield.tower"] = (1.00f, 1.30f),
        ["held.tool.blade"] = (0.60f, 1.00f),
        ["held.tool.maul"] = (0.80f, 1.30f),
        ["held.tool.builder"] = (0.40f, 0.80f),
        ["held.tool.torch"] = (0.40f, 0.70f),
        ["held.tool.lantern"] = (0.22f, 0.45f),
        [WeaponMeshCatalog.GenericVisualId] = (0.50f, 1.10f)
    };

    [Fact]
    public void EveryId_IsDrawnWithinAPlausibleLengthForItsClass_InTheWorld()
    {
        string[] ids = AllIds().Append(WeaponMeshCatalog.GenericVisualId).ToArray();
        Assert.Equal(23, AllIds().Length);
        Assert.Equal(ids.Order(StringComparer.Ordinal), PlausibleMetres.Keys.Order(StringComparer.Ordinal));
        float worldPerMetre = HeldItemScale.ModelUnitsPerMetre * PlayerModelAssetDefinition.VisualScale;
        foreach (string id in ids)
        {
            // The world length actually drawn: the rotated extent times the renderer's scale times the figure's scale.
            float world = HeldItemScale.AuthoredLengthModelUnits(id) * HeldItemScale.Resolve(id) * PlayerModelAssetDefinition.VisualScale;
            (float min, float max) = PlausibleMetres[id];
            Assert.InRange(world, min * worldPerMetre, max * worldPerMetre);
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
