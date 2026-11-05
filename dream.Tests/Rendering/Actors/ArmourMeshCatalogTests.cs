using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Actors;

namespace Dream.Tests.Rendering.Actors;

// The worn catalog is the whole point of the worn-visuals wave: every piece must change the
// silhouette, must still cover its body region, and must not stand wider than the fit ceiling.
public class ArmourMeshCatalogTests
{
    private const float Eps = 1e-4f;

    public static readonly string[] Head =
        ["worn.head.leather-cap", "worn.head.iron-helm", "worn.head.greathelm"];

    public static readonly string[] Body =
        ["worn.body.leather-vest", "worn.body.chain-hauberk", "worn.body.plate-cuirass"];

    public static readonly string[] Hands =
        ["worn.hands.leather-gloves", "worn.hands.iron-gauntlets", "worn.hands.plate-gauntlets"];

    public static readonly string[] Feet =
        ["worn.feet.leather-boots", "worn.feet.iron-boots", "worn.feet.plate-sabatons"];

    public static readonly string[] Legs =
    [
        "worn.legs.leather-leggings", "worn.legs.plated-leggings", "worn.legs.chain-chausses",
        "worn.legs.plate-greaves"
    ];

    public static readonly string[] Cloaks =
        ["worn.back.travel-cloak", "worn.back.hooded-cloak", "worn.back.grave-mantle"];

    public static readonly string[] Back = [.. Cloaks, "worn.back.gossamer-wings"];

    public static readonly string[] Companion = ["worn.companion.ember-wisp"];

    public static readonly string[] All = [.. Head, .. Body, .. Hands, .. Feet, .. Legs, .. Back, .. Companion];

    public static IEnumerable<object[]> EveryId() => All.Select(id => new object[] { id });

    // A box in the node's frame. Numbers are engine units (1 authored u = 0.088).
    private readonly record struct Envelope(Vector3 Min, Vector3 Max);

    // The regions of the base figure's body a worn piece must still cover (so no bare skin shows through
    // a gap), from the shared body contract. Torso has two: the chest and the wider hip skirt.
    private static readonly Dictionary<ArmourMount, Envelope[]> Covers = new()
    {
        [ArmourMount.Head] = [new(new(-0.12f, 0.00f, -0.13f), new(0.12f, 0.42f, 0.13f))],
        [ArmourMount.Torso] =
        [
            new(new(-0.19f, 0.00f, -0.14f), new(0.19f, 0.86f, 0.14f)),
            new(new(-0.26f, 0.00f, -0.14f), new(0.26f, 0.30f, 0.14f))
        ],
        [ArmourMount.Arms] = [new(new(-0.09f, -0.95f, -0.09f), new(0.09f, -0.55f, 0.09f))],
        [ArmourMount.Legs] = [new(new(-0.11f, -0.75f, -0.11f), new(0.11f, -0.10f, 0.11f))],
        [ArmourMount.Feet] = [new(new(-0.132f, -1.30f, -0.05f), new(0.132f, -0.88f, 0.15f))]
    };

    // The outer bound of every box of a piece: far enough to read as armour, no further.
    private static readonly Dictionary<ArmourMount, Envelope> Limits = new()
    {
        [ArmourMount.Head] = new(new(-0.20f, -0.05f, -0.25f), new(0.20f, 0.75f, 0.25f)),
        [ArmourMount.Torso] = new(new(-0.42f, -0.05f, -0.25f), new(0.42f, 0.95f, 0.25f)),
        [ArmourMount.Arms] = new(new(-0.25f, -1.10f, -0.25f), new(0.25f, -0.40f, 0.25f)),
        [ArmourMount.Legs] = new(new(-0.20f, -0.80f, -0.22f), new(0.20f, -0.05f, 0.22f)),
        [ArmourMount.Feet] = new(new(-0.20f, -1.36f, -0.15f), new(0.20f, -0.80f, 0.42f))
    };

    // The fit ceiling: the union half-width of a piece over the base figure's own half-width at its
    // node. A cover rule has no upper bound, so on its own one big box satisfies it; this is the bound.
    // Head, Torso and the cloaks are the re-authored pieces and carry the 1.6 rule. Hands, legs and feet
    // were not re-authored (they were sized for the old, thicker figure) and keep their old shapes under
    // explicit, looser multipliers rather than being silently exempt.
    private const float FitCeiling = 1.6f;

    private static readonly Dictionary<ArmourMount, (float BaseHalfWidth, float Multiplier)> Fit = new()
    {
        [ArmourMount.Head] = (0.12f, FitCeiling),
        [ArmourMount.Torso] = (0.26f, FitCeiling),
        [ArmourMount.Arms] = (0.09f, 2.3f),
        [ArmourMount.Legs] = (0.11f, 1.7f),
        [ArmourMount.Feet] = (0.132f, 1.5f)
    };

    // Cloaks: above this height (torso frame) a cloak is on the shoulders and back and obeys the 1.6
    // rule; below it the hem may flare to 1.9x so the taper reads, but no further.
    private const float CloakShoulderBand = 0.30f;
    private const float CloakHemFlare = 1.9f;
    private const float CloakBaseHalfWidth = 0.26f;

    // The hanging panels of a cloak: boxes whose centre is behind the torso back surface (z -0.14).
    private const float CloakPanelZ = -0.18f;

    private static ArmourModel Model(string id)
    {
        ArmourModel? model = ArmourMeshCatalog.Resolve(id);
        Assert.NotNull(model);
        return model!;
    }

    private static (Vector3 Min, Vector3 Max) PartBounds(ArmourPart part)
    {
        Matrix4 m = part.ToMatrix();
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            Vector4 corner = new(
                (i & 1) == 0 ? -0.5f : 0.5f,
                (i & 2) == 0 ? -0.5f : 0.5f,
                (i & 4) == 0 ? -0.5f : 0.5f,
                1f);
            Vector3 p = Vector4.TransformRow(corner, m).Xyz;
            min = Vector3.ComponentMin(min, p);
            max = Vector3.ComponentMax(max, p);
        }

        return (min, max);
    }

    private static (Vector3 Min, Vector3 Max) Union(ArmourModel model)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (ArmourPart part in model.Parts)
        {
            (Vector3 pMin, Vector3 pMax) = PartBounds(part);
            min = Vector3.ComponentMin(min, pMin);
            max = Vector3.ComponentMax(max, pMax);
        }

        return (min, max);
    }

    private static bool PartContains(ArmourPart part, Vector3 point)
    {
        Matrix4 inverse = Matrix4.Invert(part.ToMatrix());
        Vector3 local = Vector4.TransformRow(new Vector4(point, 1f), inverse).Xyz;
        return MathF.Abs(local.X) <= 0.5f + Eps && MathF.Abs(local.Y) <= 0.5f + Eps && MathF.Abs(local.Z) <= 0.5f + Eps;
    }

    private static float Luma(ArmourMaterial material)
    {
        Vector4 c = ArmourMeshCatalog.BaseColor(material);
        return (0.2126f * c.X) + (0.7152f * c.Y) + (0.0722f * c.Z);
    }

    private static bool IsLight(ArmourMaterial material) => Luma(material) >= 0.30f;

    private static bool IsDark(ArmourMaterial material) => Luma(material) <= 0.18f;

    [Theory]
    [MemberData(nameof(EveryId))]
    public void EveryExpectedId_IsKnown_AndHasThreeToEightParts(string id)
    {
        Assert.True(ArmourMeshCatalog.IsKnown(id));
        Assert.Contains(id, ArmourMeshCatalog.KnownVisualIds);
        ArmourModel model = Model(id);

        Assert.Equal(id, model.VisualId);
        Assert.InRange(model.Parts.Count, 3, 8);
        Assert.All(model.Parts, p => Assert.True(p.Size.X > 0f && p.Size.Y > 0f && p.Size.Z > 0f));
    }

    [Fact]
    public void TheCatalogHoldsExactlyTheTwentyOneDrawnModels()
    {
        Assert.Equal(21, All.Length);
        Assert.Equal(21, All.Distinct().Count());
        Assert.Equal(All.OrderBy(s => s, StringComparer.Ordinal), ArmourMeshCatalog.KnownVisualIds.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void NoTwoWornPiecesShareAShape()
    {
        HashSet<string> shapes = [];
        foreach (string id in All)
        {
            string shape = string.Join(";", Model(id).Parts.Select(p => $"{p.Center}{p.Size}{p.Material}{p.PitchDegrees}{p.RollDegrees}"));
            Assert.True(shapes.Add(shape), $"{id} has the same mesh as another piece");
        }
    }

    [Fact]
    public void NoTwoWornPiecesOfASlotShareAnOutline()
    {
        // Stricter than the part list: the union silhouette box differs inside every slot, so two
        // pieces cannot differ only by a recoloured box.
        foreach (string[] slot in new[] { Head, Body, Hands, Feet, Legs, Back })
        {
            HashSet<string> outlines = [];
            foreach (string id in slot)
            {
                (Vector3 min, Vector3 max) = Union(Model(id));
                string outline = $"{min.X:F2},{min.Y:F2},{min.Z:F2},{max.X:F2},{max.Y:F2},{max.Z:F2}";
                Assert.True(outlines.Add(outline), $"{id} has the same outline box as another piece of its slot");
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("worn.head.unknown")]
    [InlineData("held.weapon.sword")]
    [InlineData("worn.neck.ward-pendant")]
    [InlineData("worn.ring.iron-band")]
    [InlineData("worn.head")]
    public void UnknownIds_ResolveToNothing(string? id)
    {
        Assert.False(ArmourMeshCatalog.IsKnown(id));
        Assert.Null(ArmourMeshCatalog.Resolve(id));
    }

    [Fact]
    public void EveryIdCarriesItsSlotsPrefix_AndMount()
    {
        (string Prefix, ArmourMount Mount, string[] Ids)[] slots =
        [
            ("worn.head.", ArmourMount.Head, Head),
            ("worn.body.", ArmourMount.Torso, Body),
            ("worn.hands.", ArmourMount.Arms, Hands),
            ("worn.feet.", ArmourMount.Feet, Feet),
            ("worn.legs.", ArmourMount.Legs, Legs),
            ("worn.back.", ArmourMount.Back, Back),
            ("worn.companion.", ArmourMount.Companion, Companion)
        ];

        foreach ((string prefix, ArmourMount mount, string[] ids) in slots)
        {
            foreach (string id in ids)
            {
                Assert.StartsWith(prefix, id);
                Assert.Equal(mount, Model(id).Mount);
            }
        }
    }

    [Theory]
    [MemberData(nameof(EveryId))]
    public void EveryPiece_HasALightTopFacet_AndADeepDarkMass(string id)
    {
        ArmourModel model = Model(id);

        // Procedural boxes get no bake and no AO: the contrast has to be authored. A top facet is a
        // box that is not tilted far from level.
        Assert.Contains(model.Parts, p => IsLight(p.Material) && MathF.Abs(p.PitchDegrees) <= 20f && MathF.Abs(p.RollDegrees) <= 20f);
        Assert.Contains(model.Parts, p => IsDark(p.Material));
    }

    public static IEnumerable<object[]> FittedIds() =>
        Head.Concat(Body).Concat(Hands).Concat(Feet).Concat(Legs).Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(FittedIds))]
    public void EveryFittedPiece_CoversItsBodyRegion_AndStaysInsideItsLimit(string id)
    {
        ArmourModel model = Model(id);

        // Every sample of each cover region (corners, edge midpoints, face centres, centre) lies inside
        // some part, so no bare body shows through a gap. This is coverage only: it proves nothing about
        // shape, which is what the fit ceiling is for.
        foreach (Envelope cover in Covers[model.Mount])
        {
            for (int ix = 0; ix < 3; ix++)
            {
                for (int iy = 0; iy < 3; iy++)
                {
                    for (int iz = 0; iz < 3; iz++)
                    {
                        Vector3 point = new(
                            MathHelper.Lerp(cover.Min.X, cover.Max.X, ix / 2f),
                            MathHelper.Lerp(cover.Min.Y, cover.Max.Y, iy / 2f),
                            MathHelper.Lerp(cover.Min.Z, cover.Max.Z, iz / 2f));
                        Assert.True(model.Parts.Any(p => PartContains(p, point)), $"{id} leaves {point} uncovered");
                    }
                }
            }
        }

        Envelope limit = Limits[model.Mount];
        foreach (ArmourPart part in model.Parts)
        {
            (Vector3 min, Vector3 max) = PartBounds(part);
            Assert.True(min.X >= limit.Min.X - Eps && min.Y >= limit.Min.Y - Eps && min.Z >= limit.Min.Z - Eps, $"{id} {part.Center} leaves its limit (min {min})");
            Assert.True(max.X <= limit.Max.X + Eps && max.Y <= limit.Max.Y + Eps && max.Z <= limit.Max.Z + Eps, $"{id} {part.Center} leaves its limit (max {max})");
        }
    }

    [Theory]
    [MemberData(nameof(FittedIds))]
    public void EveryFittedPiece_StaysWithinItsFitCeiling(string id)
    {
        // The opposite direction to the cover rule, and the one whose absence made the plank: a piece
        // wider than ~1.6x the body at its node reads as a block that swallows the figure, however
        // well it covers. Both sides of the union are measured, so a lopsided piece cannot hide.
        ArmourModel model = Model(id);
        (float baseHalfWidth, float multiplier) = Fit[model.Mount];
        (Vector3 min, Vector3 max) = Union(model);
        float ceiling = baseHalfWidth * multiplier;

        Assert.True(max.X <= ceiling + Eps && -min.X <= ceiling + Eps, $"{id} half-width {MathF.Max(max.X, -min.X)} exceeds {ceiling} ({multiplier}x {baseHalfWidth})");
    }

    public static IEnumerable<object[]> CloakIds() => Cloaks.Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(CloakIds))]
    public void EveryCloak_StaysWithinItsFitCeiling_AndHangsOffTheBody(string id)
    {
        ArmourModel model = Model(id);
        float shoulderCeiling = CloakBaseHalfWidth * FitCeiling;
        float hemCeiling = CloakBaseHalfWidth * CloakHemFlare;

        foreach (ArmourPart part in model.Parts)
        {
            (Vector3 min, Vector3 max) = PartBounds(part);
            float half = MathF.Max(max.X, -min.X);

            // The hem flare is only for the boxes that hang below the shoulders.
            float allowed = part.Center.Y >= CloakShoulderBand ? shoulderCeiling : hemCeiling;
            Assert.True(half <= allowed + Eps, $"{id} box at {part.Center} is {half} half-wide, over {allowed}");

            // Nothing hangs further back than the contract's panel depth plus the swing of the pitch.
            Assert.True(min.Z >= -0.42f, $"{id} box at {part.Center} reaches back to {min.Z}");
        }

        // Hanging panels sit behind the torso back surface (z -0.14) and clear of it.
        foreach (ArmourPart panel in model.Parts.Where(p => p.Center.Z <= CloakPanelZ))
        {
            Assert.True(panel.Center.Z >= -0.30f - Eps, $"{id} panel at {panel.Center} hangs too far back");
        }
    }

    private static float ShoulderHalfWidth(ArmourModel model) =>
        model.Parts.Where(p => p.Center.Y >= 0.5f).Max(p => MathF.Max(PartBounds(p).Max.X, -PartBounds(p).Min.X));

    [Theory]
    [MemberData(nameof(CloakIds))]
    public void EveryCloak_TapersFromNarrowShouldersToAWiderHem(string id)
    {
        // A cloak is a stack of hanging panels (centre z <= -0.18). A single broad slab reads as a
        // billboard at the isometric angle, so the highest panel must be narrow and everything below
        // it must be clearly wider, and no single box may be broader than 0.9.
        ArmourPart[] panels = [.. Model(id).Parts.Where(p => p.Center.Z <= CloakPanelZ)];
        Assert.True(panels.Length >= 3, $"{id} has only {panels.Length} hanging panels");

        ArmourPart top = panels.OrderByDescending(p => p.Center.Y).First();
        (Vector3 topMin, Vector3 topMax) = PartBounds(top);
        float topWidth = topMax.X - topMin.X;
        Assert.True(topWidth <= 0.44f, $"{id} top panel is {topWidth} wide");

        float lowerMin = float.MaxValue;
        float lowerMax = float.MinValue;
        foreach (ArmourPart panel in panels.Where(p => p != top))
        {
            (Vector3 min, Vector3 max) = PartBounds(panel);
            lowerMin = MathF.Min(lowerMin, min.X);
            lowerMax = MathF.Max(lowerMax, max.X);
        }

        Assert.True(lowerMax - lowerMin >= topWidth + 0.12f, $"{id} hem {lowerMax - lowerMin} vs shoulders {topWidth}");

        // No oversized box: none is broader than the mantle shoulders.
        Assert.All(Model(id).Parts, p => Assert.True(p.Size.X <= 0.90f, $"{id} has a {p.Size.X} wide box"));

        // The panels hang at more than one depth, so the lengths do not merge into one flat plane.
        Assert.True(panels.Select(p => MathF.Round(p.Center.Z, 2)).Distinct().Count() >= 3, $"{id} panels share a depth");
    }

    [Theory]
    [InlineData("worn.back.hooded-cloak", 3)]
    [InlineData("worn.back.grave-mantle", 2)]
    public void LongCloaks_HaveAScallopedHem(string id, int minFolds)
    {
        // The hem is several side-by-side boxes whose bottoms differ, so the lower outline steps
        // instead of running as one straight line. Folds are the boxes reaching the lowest tier.
        (Vector3 min, _) = Union(Model(id));
        float[] bottoms = [.. Model(id).Parts
            .Where(p => p.Center.Z <= CloakPanelZ && PartBounds(p).Item1.Y < min.Y + 0.25f)
            .Select(p => MathF.Round(PartBounds(p).Item1.Y, 2))];
        Assert.True(bottoms.Length >= minFolds, $"{id} has {bottoms.Length} hem boxes");
        Assert.True(bottoms.Distinct().Count() >= minFolds, $"{id} hem bottoms are level");
        Assert.True(bottoms.Max() - bottoms.Min() >= 0.05f, $"{id} hem steps only {bottoms.Max() - bottoms.Min()}");
    }

    [Fact]
    public void TheFourBackItems_DifferStronglyInSilhouette()
    {
        (Vector3 travelMin, Vector3 travelMax) = Union(Model("worn.back.travel-cloak"));
        (Vector3 hoodedMin, Vector3 hoodedMax) = Union(Model("worn.back.hooded-cloak"));
        (Vector3 mantleMin, _) = Union(Model("worn.back.grave-mantle"));
        (Vector3 wingsMin, Vector3 wingsMax) = Union(Model("worn.back.gossamer-wings"));

        // The hooded cloak is the long one, with a collar that stands above the shoulders.
        Assert.True(hoodedMin.Y < travelMin.Y - 0.3f);
        Assert.True(hoodedMin.Y < mantleMin.Y - 0.3f);
        Assert.True(hoodedMax.Y > travelMax.Y + 0.15f);

        // The mantle is the broad one at the shoulders (the hems flare alike, so compare the shoulders).
        Assert.True(ShoulderHalfWidth(Model("worn.back.grave-mantle")) > ShoulderHalfWidth(Model("worn.back.travel-cloak")) + 0.15f);
        Assert.True(ShoulderHalfWidth(Model("worn.back.grave-mantle")) > ShoulderHalfWidth(Model("worn.back.hooded-cloak")) + 0.15f);

        // The wings are not cloth: no cloth material, thin planes, glowing, and nowhere near a hem.
        ArmourModel wings = Model("worn.back.gossamer-wings");
        Assert.DoesNotContain(wings.Parts, p => p.Material is ArmourMaterial.ClothAsh or ArmourMaterial.ClothRed or ArmourMaterial.Leather);
        Assert.Equal(2, wings.Parts.Count(p => p.Material == ArmourMaterial.Arcane && p.Size.Z <= 0.03f));
        Assert.True(wingsMin.Y > 0f);
        Assert.InRange(wingsMax.Y, 0.90f, 1.05f);
        Assert.InRange(wingsMax.X, 0.40f, 0.60f);
        Assert.InRange(wingsMin.X, -0.60f, -0.40f);
    }

    [Fact]
    public void Wings_SpanTheRoadmapEnvelope_Symmetrically()
    {
        ArmourModel wings = Model("worn.back.gossamer-wings");
        foreach (ArmourPart plane in wings.Parts.Where(p => p.Material == ArmourMaterial.Arcane))
        {
            (Vector3 min, Vector3 max) = PartBounds(plane);
            float inner = MathF.Min(MathF.Abs(min.X), MathF.Abs(max.X));
            float outer = MathF.Max(MathF.Abs(min.X), MathF.Abs(max.X));
            Assert.InRange(inner, 0.10f, 0.24f);
            Assert.InRange(outer, 0.45f, 0.60f);
            Assert.InRange(min.Y, 0.40f, 0.50f);
            Assert.InRange(max.Y, 0.90f, 1.00f);
        }

        Assert.Equal(
            wings.Parts.Where(p => p.Center.X > 0f).Sum(p => p.Size.X * p.Size.Y),
            wings.Parts.Where(p => p.Center.X < 0f).Sum(p => p.Size.X * p.Size.Y),
            3);
    }

    [Fact]
    public void Wisp_IsAnEmissiveOrbFloatingBesideTheShoulder()
    {
        ArmourModel wisp = Model("worn.companion.ember-wisp");
        (Vector3 min, Vector3 max) = Union(wisp);

        Assert.InRange(min.X, 0.30f, 0.50f);
        Assert.InRange(max.X, 0.40f, 0.60f);
        Assert.True(min.Y > 0.7f);
        Assert.Contains(wisp.Parts, p => ArmourMeshCatalog.Emissive(p.Material) != Vector3.Zero);
    }

    [Fact]
    public void Pieces_AreSymmetricAboutTheirOwnAxis_ExceptWhereTheyAreOneSided()
    {
        // Arms, legs and feet are drawn twice with a mirror, so each model must be authored about its
        // own axis and not lean to a side. The companion is the one deliberate exception.
        foreach (string id in Hands.Concat(Legs).Concat(Feet).Concat(Head).Concat(Body).Concat(Back))
        {
            (Vector3 min, Vector3 max) = Union(Model(id));
            Assert.True(MathF.Abs(min.X + max.X) < 0.02f, $"{id} leans: x {min.X}..{max.X}");
        }
    }

    [Fact]
    public void Materials_AreFromTheFiguresPalette_AndOnlyGlowingOnesEmit()
    {
        foreach (ArmourMaterial material in Enum.GetValues<ArmourMaterial>())
        {
            Vector4 color = ArmourMeshCatalog.BaseColor(material);
            Assert.InRange(color.X, 0f, 1f);
            Assert.InRange(color.Y, 0f, 1f);
            Assert.InRange(color.Z, 0f, 1f);
            Assert.Equal(1f, color.W);
            bool glows = ArmourMeshCatalog.Emissive(material) != Vector3.Zero;
            Assert.Equal(material is ArmourMaterial.Arcane or ArmourMaterial.Ember, glows);
        }

        // Sampled from gen_char_player.py's palette, not WeaponMaterial.Steel (0.46, 0.48, 0.52).
        Assert.Equal(new Vector4(0.36f, 0.39f, 0.46f, 1f), ArmourMeshCatalog.BaseColor(ArmourMaterial.Steel));
        Assert.Equal(new Vector4(0.086f, 0.102f, 0.157f, 1f), ArmourMeshCatalog.BaseColor(ArmourMaterial.ClothCold));
        Assert.Equal(new Vector4(0.118f, 0.125f, 0.141f, 1f), ArmourMeshCatalog.BaseColor(ArmourMaterial.ClothAsh));
        Assert.Equal(new Vector4(0.353f, 0.118f, 0.102f, 1f), ArmourMeshCatalog.BaseColor(ArmourMaterial.ClothRed));
    }

    [Fact]
    public void PartMatrix_ScalesThenTurnsThenMoves()
    {
        ArmourPart part = new(new Vector3(1f, 2f, 3f), new Vector3(2f, 4f, 6f), ArmourMaterial.Steel, 0f, 90f);

        Vector3 corner = Vector4.TransformRow(new Vector4(0.5f, 0.5f, 0.5f, 1f), part.ToMatrix()).Xyz;

        Assert.Equal(-1f, corner.X, 3);
        Assert.Equal(3f, corner.Y, 3);
        Assert.Equal(6f, corner.Z, 3);
    }

    [Fact]
    public void SideAttach_MirrorsTheRightSideAndMovesArmPiecesOntoTheArmAxis()
    {
        Matrix4 left = WornArmourRenderer.CreateSideAttach(+1f, WornArmourRenderer.ArmOffsetX);
        Matrix4 right = WornArmourRenderer.CreateSideAttach(-1f, WornArmourRenderer.ArmOffsetX);

        Vector3 leftPoint = Vector4.TransformRow(new Vector4(0.1f, -0.8f, 0.05f, 1f), left).Xyz;
        Vector3 rightPoint = Vector4.TransformRow(new Vector4(0.1f, -0.8f, 0.05f, 1f), right).Xyz;
        Assert.Equal(0.3f, leftPoint.X, 4);
        Assert.Equal(-0.3f, rightPoint.X, 4);
        Assert.Equal(leftPoint.Y, rightPoint.Y, 4);
        Assert.Equal(leftPoint.Z, rightPoint.Z, 4);

        // Legs and feet carry no lateral offset: the leg nodes already sit on the leg.
        Vector3 legPoint = Vector4.TransformRow(new Vector4(0.1f, 0f, 0f, 1f), WornArmourRenderer.CreateSideAttach(-1f, 0f)).Xyz;
        Assert.Equal(-0.1f, legPoint.X, 4);
    }
}
