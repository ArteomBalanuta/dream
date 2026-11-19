using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Actors;

namespace Dream.Tests.Rendering.Actors;

// The tier tints. The two things that must never break: every shipped item has a tier (or the set
// silently splits into tinted and untinted pieces), and no tier may destroy a material - the world
// pass has no tonemap, so a tint that pushes a channel past the albedo ceiling clips to a flat
// patch, and one that halves a channel turns a dark piece into a hole.
public class EquipmentTintCatalogTests
{
    // How far below its untinted value a channel may be pulled. Below this the material stops
    // reading as itself.
    private const float MinChannelRatio = 0.55f;

    public static TheoryData<string> WeaponIds()
    {
        TheoryData<string> data = new();
        foreach (string id in WeaponMeshCatalog.KnownVisualIds)
        {
            data.Add(id);
        }

        return data;
    }

    public static TheoryData<string> ArmourIds()
    {
        TheoryData<string> data = new();
        foreach (string id in ArmourMeshCatalog.KnownVisualIds)
        {
            data.Add(id);
        }

        return data;
    }

    [Fact]
    public void EveryKnownVisualId_HasATier()
    {
        List<string> missing = [];
        foreach (string id in WeaponMeshCatalog.KnownVisualIds.Concat(ArmourMeshCatalog.KnownVisualIds))
        {
            if (!EquipmentTintCatalog.Tiers.ContainsKey(id))
            {
                missing.Add(id);
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void TheTierTable_NamesNothingTheCatalogsDoNotHave()
    {
        // The fallback stick is not in the model table but is a real drawn id, so it may be tiered.
        HashSet<string> known =
            [.. WeaponMeshCatalog.KnownVisualIds, .. ArmourMeshCatalog.KnownVisualIds, WeaponMeshCatalog.GenericVisualId];
        Assert.Empty(EquipmentTintCatalog.Tiers.Keys.Where(id => !known.Contains(id)));
    }

    // The royal sorcerer set is one set and must read as one: six pieces, one tier. Named here
    // rather than derived from the mesh catalog, because "every known id has a tier" passes just as
    // happily when a piece is quietly Common.
    public static TheoryData<string> SorcererRegaliaIds() =>
    [
        "worn.head.sorcerer-crown",
        "worn.body.sorcerer-regalia",
        "worn.hands.sorcerer-gauntlets",
        "worn.legs.sorcerer-greaves",
        "worn.feet.sorcerer-sabatons",
        "worn.back.sorcerer-mantle",
    ];

    [Theory]
    [MemberData(nameof(SorcererRegaliaIds))]
    public void TheSorcererRegalia_IsEpic(string visualId)
    {
        Assert.Equal(EquipmentTier.Epic, EquipmentTintCatalog.TierOf(visualId));
        Assert.Equal(EquipmentTintCatalog.TintFor(EquipmentTier.Epic), EquipmentTintCatalog.Resolve(visualId));
    }

    // The regalia is enamelled indigo plate, a material the tier table has not had to carry before.
    // Run the same safety check the per-id theories run, on the ids whether or not the mesh catalog
    // of this build already lists them.
    [Theory]
    [MemberData(nameof(SorcererRegaliaIds))]
    public void TheSorcererRegaliaTint_NeverBlowsOutOrDestroysAMaterial(string visualId)
    {
        EquipmentTint tint = EquipmentTintCatalog.Resolve(visualId);
        foreach (ArmourMaterial material in Enum.GetValues<ArmourMaterial>())
        {
            EquipmentSurface surface = EquipmentSurface.For(material);
            AssertSafe(tint, ArmourMeshCatalog.BaseColor(material), surface.AccentMix, $"{visualId}/{material}");
        }
    }

    [Fact]
    public void AnUnknownIdAndNull_ResolveToNeutral()
    {
        Assert.Equal(EquipmentTint.Neutral, EquipmentTintCatalog.Resolve(null));
        Assert.Equal(EquipmentTint.Neutral, EquipmentTintCatalog.Resolve("held.weapon.not-an-item"));
        Assert.Equal(EquipmentTint.Neutral, EquipmentTintCatalog.Resolve(string.Empty));
        Assert.True(EquipmentTintCatalog.Resolve("held.weapon.not-an-item").IsNeutral);
    }

    [Fact]
    public void Resolve_IsDeterministic()
    {
        foreach (string id in WeaponMeshCatalog.KnownVisualIds.Concat(ArmourMeshCatalog.KnownVisualIds))
        {
            Assert.Equal(EquipmentTintCatalog.Resolve(id), EquipmentTintCatalog.Resolve(id));
        }
    }

    [Fact]
    public void EveryTier_HasATint_AndCommonIsNeutral()
    {
        Assert.True(EquipmentTintCatalog.TintFor(EquipmentTier.Common).IsNeutral);
        foreach (EquipmentTier tier in Enum.GetValues<EquipmentTier>())
        {
            EquipmentTint tint = EquipmentTintCatalog.TintFor(tier);
            AssertChannels(tint.Primary, 0.80f, 1.05f, $"{tier} primary");
            AssertChannels(tint.Accent, MinChannelRatio, 1.10f, $"{tier} accent");
        }
    }

    [Theory]
    [MemberData(nameof(WeaponIds))]
    public void AWeaponTint_NeverBlowsOutOrDestroysAMaterial(string visualId)
    {
        EquipmentTint tint = EquipmentTintCatalog.Resolve(visualId);
        foreach (WeaponMaterial material in Enum.GetValues<WeaponMaterial>())
        {
            EquipmentSurface surface = EquipmentSurface.For(material);
            AssertSafe(tint, WeaponMeshCatalog.BaseColor(material), surface.AccentMix, $"{visualId}/{material}");
        }
    }

    [Theory]
    [MemberData(nameof(ArmourIds))]
    public void AnArmourTint_NeverBlowsOutOrDestroysAMaterial(string visualId)
    {
        EquipmentTint tint = EquipmentTintCatalog.Resolve(visualId);
        foreach (ArmourMaterial material in Enum.GetValues<ArmourMaterial>())
        {
            EquipmentSurface surface = EquipmentSurface.For(material);
            AssertSafe(tint, ArmourMeshCatalog.BaseColor(material), surface.AccentMix, $"{visualId}/{material}");
        }
    }

    [Fact]
    public void Apply_LeavesAlphaAlone()
    {
        EquipmentTint tint = EquipmentTintCatalog.TintFor(EquipmentTier.Legendary);
        Assert.Equal(1f, tint.Apply(new Vector4(0.2f, 0.2f, 0.2f, 1f), 1f).W);
    }

    private static void AssertSafe(EquipmentTint tint, Vector4 baseColor, float accentMix, string what)
    {
        Vector4 tinted = tint.Apply(baseColor, accentMix);
        float max = EquipmentTint.MaxAlbedoChannel;

        // Assert on the UNCLAMPED product, not on Apply's output. Apply ends in MathF.Min(..., max),
        // so asserting that its result is under the ceiling only re-states the clamp and can never
        // fail. What matters is that no catalog colour times its tint ever REACHES the clamp: a
        // combination that did would be silently flattened to a uniform 0.42 patch on screen, and
        // the tier would read as a flat blob instead of a tint. That is the failure this catches.
        float mix = Math.Clamp(accentMix, 0f, 1f);
        Vector3 factor = (tint.Primary * (1f - mix)) + (tint.Accent * mix);
        Assert.True(
            baseColor.X * factor.X <= max && baseColor.Y * factor.Y <= max && baseColor.Z * factor.Z <= max,
            $"{what} would clip before the clamp: {baseColor.Xyz * factor} over {max}");
        Assert.True(tinted.X <= max && tinted.Y <= max && tinted.Z <= max, $"{what} over the ceiling: {tinted}");
        AssertNotDarkened(tinted.X, baseColor.X, what);
        AssertNotDarkened(tinted.Y, baseColor.Y, what);
        AssertNotDarkened(tinted.Z, baseColor.Z, what);
    }

    private static void AssertNotDarkened(float tinted, float untinted, string what)
    {
        Assert.True(
            tinted >= untinted * MinChannelRatio - 1e-5f,
            $"{what} darkened to {tinted} from {untinted}");
    }

    private static void AssertChannels(Vector3 v, float min, float max, string what)
    {
        Assert.True(v.X >= min && v.X <= max, $"{what}.x {v.X}");
        Assert.True(v.Y >= min && v.Y <= max, $"{what}.y {v.Y}");
        Assert.True(v.Z >= min && v.Z <= max, $"{what}.z {v.Z}");
    }
}
