using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;

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
        [ArmourMount.Head] = [new(new(-0.12f, 0.00f, -0.135f), new(0.12f, 0.42f, 0.135f))],
        [ArmourMount.Torso] =
        [
            new(new(-0.195f, 0.00f, -0.145f), new(0.195f, 0.86f, 0.15f)),
            new(new(-0.26f, 0.00f, -0.145f), new(0.26f, 0.30f, 0.15f))
        ],
        [ArmourMount.Arms] = [new(new(-0.09f, -0.95f, -0.075f), new(0.09f, -0.55f, 0.10f))],
        [ArmourMount.Legs] = [new(new(-0.11f, -0.75f, -0.11f), new(0.11f, -0.10f, 0.11f))],
        [ArmourMount.Feet] = [new(new(-0.132f, -1.30f, -0.055f), new(0.132f, -0.88f, 0.155f))]
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

    // The fit window: how far a piece's union must stand PROUD of the body at its node (the floor) and how far
    // it may stand (the ceiling). Both are needed. A cover rule has no upper bound, so one big box satisfies
    // it, and it also has no lower bound, so a piece that is exactly the body (measured: leather-vest, union
    // 0.2600 against a body of 0.2600) changes colour but not outline. This file's header promises that every
    // piece changes the silhouette; the floor is what keeps that true.
    //
    // Derived from the figure, not from the pieces. PixelUnits is one screen pixel at the shipped zoom
    // (the actor is 28 x 101 px, 0.0265 engine units per px). The FLOOR is one pixel past the body's
    // half-width: below that the outline does not move. The CEILING is the body plus a shell allowance:
    // for the head 0.06 (a 0.04 wall and a hand of clearance, 2.3 px) which makes 0.18 = 1.5x the base head of
    // 0.12, and for the torso 1.6x. The authored helms sit at 0.16-0.17, so the head's margin to its ceiling is
    // 0.01-0.02 (0.4-0.8 px) and to its floor 0.014-0.024. Depth is bounded the same way, so a plank cannot be
    // turned 90 degrees to dodge the width rule. Hands, legs and feet were not re-authored (sized for the old,
    // thicker figure): they keep their old multipliers, no floor and no depth bound, rather than being
    // silently exempt; a boot legitimately runs far forward of the leg.
    private const float PixelUnits = 0.0265f;
    private const float HeadShell = 0.06f;
    private const float TorsoCeilingMultiplier = 1.6f;

    private readonly record struct FitWindow(float BaseHalfWidth, float MinHalfWidth, float MaxHalfWidth, float MaxHalfDepth);

    private static readonly Dictionary<ArmourMount, FitWindow> Fit = new()
    {
        // base head 0.12 x 0.135: floor 0.1465, ceiling 0.18 wide, 0.195 deep
        [ArmourMount.Head] = new(0.12f, 0.12f + PixelUnits, 0.12f + HeadShell, 0.135f + HeadShell),
        // base torso 0.26 x 0.15: floor 0.2865, ceiling 0.416 wide, 0.24 deep
        [ArmourMount.Torso] = new(0.26f, 0.26f + PixelUnits, 0.26f * TorsoCeilingMultiplier, 0.15f * TorsoCeilingMultiplier),
        [ArmourMount.Arms] = new(0.09f, 0f, 0.09f * 2.3f, float.MaxValue),
        [ArmourMount.Legs] = new(0.11f, 0f, 0.11f * 1.7f, float.MaxValue),
        [ArmourMount.Feet] = new(0.132f, 0f, 0.132f * 1.5f, float.MaxValue)
    };

    // Cloaks: above this height (torso frame) a cloak is on the shoulders and back and obeys the 1.6
    // rule; below it the hem may flare to 1.9x so the taper reads, but no further.
    private const float CloakShoulderBand = 0.30f;
    private const float CloakHemFlare = 1.9f;
    private const float CloakBaseHalfWidth = 0.26f;

    // The hanging panels of a cloak: boxes whose centre is behind the torso back surface (z -0.14).
    private const float CloakPanelZ = -0.18f;

    // ---- The cover table against the mesh it describes.
    //
    // Covers/Limits/Fit are the body's geometry stated by hand. Nothing else compared them to the shipped
    // figure, so the figure could drift away from the table with every test green (the tunic's hairline
    // 0.1901 against a declared 0.19 was the symptom). This reads player-grim.glb and measures, in each
    // mount node's own frame, the figure's extent inside every cover region's y slab, then holds the table to it.
    // The rule is the one the worn pieces rely on and is ONE-SIDED outward: the figure may not stand past the
    // table (a worn piece covers the table value, so a figure wider than the table pokes out of it), and it may
    // stand short of it by at most TableSlack (the table is not allowed to ask pieces to cover air).
    // It fails when the base figure is widened, narrowed or moved beyond those, or when a Covers row is edited
    // without the figure.
    private const float TableOver = 0.0005f;
    private const float TableSlack = 0.015f;
    private const float ArmAxisX = 0.2f;   // the arm hangs 0.2 units outboard of its node (UNITS_PER_U * ARM_OFFSET_U)

    private static readonly Dictionary<ArmourMount, (string Node, float Shift)[]> MountNodes = new()
    {
        [ArmourMount.Head] = [("head", 0f)],
        [ArmourMount.Torso] = [("torso", 0f)],
        [ArmourMount.Arms] = [("arm-left", ArmAxisX), ("arm-right", -ArmAxisX)],
        [ArmourMount.Legs] = [("leg-left", 0f), ("leg-right", 0f)],
        [ArmourMount.Feet] = [("leg-left", 0f), ("leg-right", 0f)]
    };

    // Every vertex plus 16 points along every triangle edge, so a slab bound that falls between two loft
    // rings is still measured on the surface.
    private static Dictionary<string, List<Vector3>> FigureSurfacePoints()
    {
        ModelRoot model = ModelRoot.Load(PlayerModelAssetDefinition.ModelPath);
        Dictionary<string, List<Vector3>> byNode = [];
        foreach (Node node in model.LogicalNodes.Where(n => n.Mesh != null))
        {
            List<Vector3> list = byNode.TryGetValue(node.Name, out List<Vector3>? existing) ? existing : byNode[node.Name] = [];
            foreach (MeshPrimitive primitive in node.Mesh.Primitives)
            {
                IList<System.Numerics.Vector3> positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
                int[] index = [.. primitive.GetIndices().Select(i => (int)i)];
                Vector3 P(int i) => new(positions[i].X, positions[i].Y, positions[i].Z);
                list.AddRange(positions.Select(v => new Vector3(v.X, v.Y, v.Z)));
                for (int t = 0; t + 2 < index.Length; t += 3)
                {
                    for (int e = 0; e < 3; e++)
                    {
                        Vector3 a = P(index[t + e]);
                        Vector3 b = P(index[t + ((e + 1) % 3)]);
                        for (int k = 1; k < 16; k++)
                        {
                            list.Add(Vector3.Lerp(a, b, k / 16f));
                        }
                    }
                }
            }
        }

        return byNode;
    }

    // Problems between the declared table and the figure's points; empty when they agree.
    private static List<string> CoverTableProblems(Dictionary<string, List<Vector3>> figure)
    {
        List<string> problems = [];
        foreach ((ArmourMount mount, Envelope[] regions) in Covers)
        {
            for (int r = 0; r < regions.Length; r++)
            {
                Envelope region = regions[r];

                // A region nested over a shorter one (the torso's chest over its hip skirt) is measured above
                // the shorter one's top, or the wider skirt would be read as the chest.
                float yMin = regions.Where(o => o.Max.Y < region.Max.Y).Select(o => o.Max.Y).DefaultIfEmpty(region.Min.Y).Max();
                yMin = MathF.Max(yMin, region.Min.Y);

                Vector3 lo = new(float.MaxValue);
                Vector3 hi = new(float.MinValue);
                float zLo = float.MaxValue;
                float zHi = float.MinValue;
                foreach ((string node, float shift) in MountNodes[mount])
                {
                    foreach (Vector3 v in figure[node].Where(v => v.Y >= yMin - 1e-6f && v.Y <= region.Max.Y + 1e-6f))
                    {
                        Vector3 q = new(v.X - shift, v.Y, v.Z);
                        lo = Vector3.ComponentMin(lo, q);
                        hi = Vector3.ComponentMax(hi, q);
                    }

                    // Depth is measured over the region's WHOLE y range: the nested slab exists only so the
                    // wider hip skirt is not read as the chest's width.
                    foreach (Vector3 v in figure[node].Where(v => v.Y >= region.Min.Y - 1e-6f && v.Y <= region.Max.Y + 1e-6f))
                    {
                        zLo = MathF.Min(zLo, v.Z);
                        zHi = MathF.Max(zHi, v.Z);
                    }
                }

                void Compare(string axis, float measured, float declared, bool upper)
                {
                    // upper: a MAX extent (must not exceed the declared value). Otherwise a MIN extent.
                    float outward = upper ? measured - declared : declared - measured;
                    if (outward > TableOver)
                    {
                        problems.Add($"{mount}[{r}] {axis}: the figure reaches {measured:F4}, past the table's {declared:F4}");
                    }
                    else if (outward < -TableSlack)
                    {
                        problems.Add($"{mount}[{r}] {axis}: the figure reaches only {measured:F4}, {-outward:F4} short of the table's {declared:F4}");
                    }
                }

                // Y is checked only where the region's own edge is the figure's edge (the head's crown).
                if (mount == ArmourMount.Head)
                {
                    Compare("top y", figure["head"].Max(v => v.Y), region.Max.Y, upper: true);
                }

                Compare("max x", hi.X, region.Max.X, upper: true);
                Compare("min x", lo.X, region.Min.X, upper: false);
                Compare("max z", zHi, region.Max.Z, upper: true);
                Compare("min z", zLo, region.Min.Z, upper: false);
            }
        }

        return problems;
    }

    [Fact]
    public void TheCoverTable_AgreesWithTheShippedFigureMesh()
    {
        Assert.Empty(CoverTableProblems(FigureSurfacePoints()));
    }

    [Fact]
    public void TheCoverTableGate_FailsWhenTheFigureDriftsFromTheTable()
    {
        // Control: the gate must be able to fail. Widen the head 20% and push the torso front out 0.03: both
        // directions of the rule (past the table) have to be reported.
        Dictionary<string, List<Vector3>> figure = FigureSurfacePoints()
            .ToDictionary(kv => kv.Key, kv => kv.Value.Select(v => kv.Key == "head" ? new Vector3(v.X * 1.2f, v.Y, v.Z) : kv.Key == "torso" ? new Vector3(v.X, v.Y, v.Z > 0.1f ? v.Z + 0.03f : v.Z) : v).ToList());
        List<string> problems = CoverTableProblems(figure);
        Assert.Contains(problems, p => p.StartsWith("Head[0] max x", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Torso[0] max z", StringComparison.Ordinal));

        // And the other side: a figure that shrinks away from the table leaves the pieces covering air.
        Dictionary<string, List<Vector3>> thin = FigureSurfacePoints()
            .ToDictionary(kv => kv.Key, kv => kv.Value.Select(v => kv.Key == "leg-left" || kv.Key == "leg-right" ? new Vector3(v.X * 0.6f, v.Y, v.Z) : v).ToList());
        Assert.Contains(CoverTableProblems(thin), p => p.StartsWith("Legs[0] max x", StringComparison.Ordinal));
    }

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

    // A glowing material is the light facet of its piece: its emissive is added after the gain, so it is bright by
    // construction, while its lit body is kept dark on purpose (a bright body clips; see the palette test).
    private static bool IsLight(ArmourMaterial material) =>
        Luma(material) >= 0.30f || ArmourMeshCatalog.Emissive(material) != Vector3.Zero;

    private static bool IsDark(ArmourMaterial material) => Luma(material) <= 0.18f;

    [Theory]
    [MemberData(nameof(EveryId))]
    // The budget rose from 8 to 10 when the pieces were re-sculpted: form first (the shell, the skirt,
    // the cuff), then the secondary detail that reads at 28x101 px (a rim, a barb, a buckle, a ring row).
    // Fine texture is the shader's job, not a part's, so the ceiling stays low.
    public void EveryExpectedId_IsKnown_AndHasThreeToTenParts(string id)
    {
        Assert.True(ArmourMeshCatalog.IsKnown(id));
        Assert.Contains(id, ArmourMeshCatalog.KnownVisualIds);
        ArmourModel model = Model(id);

        Assert.Equal(id, model.VisualId);
        Assert.InRange(model.Parts.Count, 3, 10);
        Assert.All(model.Parts, p => Assert.True(p.Size.X > 0f && p.Size.Y > 0f && p.Size.Z > 0f));
    }

    [Theory]
    [MemberData(nameof(EveryId))]
    public void EveryPart_HasAFiniteSize_ADeclaredMaterial_AndADeclaredShape(string id)
    {
        foreach (ArmourPart part in Model(id).Parts)
        {
            Assert.True(float.IsFinite(part.Size.X) && float.IsFinite(part.Size.Y) && float.IsFinite(part.Size.Z), $"{id} {part.Center} has a non-finite size");
            Assert.True(part.Size.X > 0f && part.Size.Y > 0f && part.Size.Z > 0f, $"{id} {part.Center} has a non-positive size");
            Assert.True(float.IsFinite(part.Center.X) && float.IsFinite(part.Center.Y) && float.IsFinite(part.Center.Z));
            Assert.True(float.IsFinite(part.PitchDegrees) && float.IsFinite(part.RollDegrees) && float.IsFinite(part.YawDegrees));
            Assert.Contains(part.Material, Enum.GetValues<ArmourMaterial>());
            Assert.Contains(part.Shape, Enum.GetValues<EquipmentShape>());
        }
    }

    // The sculpt gate. Before the re-authoring every part of every piece was one axis-aligned cube, which
    // is what made the armour read as Minecraft blocks; this is what stops it silently going back. Both
    // halves matter: a ceiling on plain boxes, and a named shape each family must actually use, so the
    // boxes cannot be traded for a single shape used everywhere.
    [Fact]
    public void NoWornPartIsAPlainBox_AndEachFamilyUsesItsOwnShape()
    {
        ArmourPart[] every = [.. All.SelectMany(id => Model(id).Parts)];
        Assert.Empty(every.Where(p => p.Shape == EquipmentShape.Box));

        (string What, EquipmentShape Shape)[] families =
        [
            ("a helm skull or a knee cop is a Dome", EquipmentShape.Dome),
            ("a cuirass, pauldron, greave or bracer is a Plate", EquipmentShape.Plate),
            ("a cloak, tabard or aventail is a Panel", EquipmentShape.Panel),
            ("a collar, mail row or cuff rim is a Ring", EquipmentShape.Ring),
            ("a stud or barb is a Spike", EquipmentShape.Spike),
            ("a nasal bar or a sabaton toe is a Taper", EquipmentShape.Taper),
            ("a lame, belt or buckle is a BevelBox", EquipmentShape.BevelBox),
            ("an arcane stone is a Gem", EquipmentShape.Gem),
            ("a wing spar is a Shaft", EquipmentShape.Shaft)
        ];
        foreach ((string what, EquipmentShape shape) in families)
        {
            Assert.True(every.Any(p => p.Shape == shape), what);
        }

        // A Plate opens toward its own -Z, so a piece worn on the side of a limb has to be yawed to put
        // the opening against the body. At least one piece does, or no plate is wrapping anything.
        Assert.Contains(every, p => p.Shape == EquipmentShape.Plate && p.YawDegrees != 0f);
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
            string shape = string.Join(";", Model(id).Parts.Select(p => $"{p.Center}{p.Size}{p.Material}{p.PitchDegrees}{p.RollDegrees}{p.YawDegrees}{p.Shape}"));
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

    // A part that is not tilted far from level presents its TOP face to a camera pitched 50 degrees down, and an
    // up-facing normal takes the sky ambient before any light reaches it, so a level light part is the brightest
    // thing the figure can own. This is its area, which is what decides whether it reads as a rim or as a field.
    private static bool IsLevel(ArmourPart part) =>
        MathF.Abs(part.PitchDegrees) <= 20f && MathF.Abs(part.RollDegrees) <= 20f;

    private static float TopFootprint(ArmourPart part) => part.Size.X * part.Size.Z;

    // The other half of the facet rule above, and the fix for what the engine captures showed: the facet has to
    // be an ACCENT. The first sculpted pass put Steel and Bone on the big up-facing surfaces - helm domes, cuff
    // plates, a ring at every joint - and the figure came back pale, a stack of cream hoops down each leg, when
    // the art direction is dark metal with bright edges. The palette was never the problem and is not what
    // changed: WHERE the light materials sit is. A Ring lies in the XZ plane and shows its whole top face, so
    // the Rings are held to one per piece, and the one large level light surface left in the catalog is named.
    [Fact]
    public void LightMaterialsAreRimsAndEdges_NotFields()
    {
        foreach (string id in All)
        {
            ArmourPart[] rings = [.. Model(id).Parts.Where(p => p.Shape == EquipmentShape.Ring && IsLight(p.Material) && IsLevel(p))];
            Assert.True(rings.Length <= 1, $"{id} stacks {rings.Length} light level rings; a piece gets at most one bright rim");
            Assert.All(rings, r => Assert.True(r.Size.X <= 0.34f, $"{id} has a {r.Size.X} wide light ring: a rim, not a lid"));
        }

        // The deliberate exception, named rather than left to a threshold: the plate cuirass's breastplate is a
        // Plate, a vertical arc about Y, so the camera sees its glancing front and only the EDGE of its top rim.
        // Nothing else in the catalog puts a light material on a large level surface.
        (string Id, Vector3 Size)[] fields =
        [
            .. All.SelectMany(id => Model(id).Parts.Where(p => IsLight(p.Material) && IsLevel(p) && TopFootprint(p) > 0.12f)
                .Select(p => (id, p.Size)))
        ];
        Assert.All(fields, f =>
        {
            Assert.Equal("worn.body.plate-cuirass", f.Id);
            Assert.True(f.Size.Y > f.Size.X, $"a large light field must at least be a standing arc, not a lid ({f.Size})");
        });
    }

    public static IEnumerable<object[]> LegAndFootIds() => Legs.Concat(Feet).Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(LegAndFootIds))]
    public void EachLegAndFootPiece_ShowsAtMostOneBrightBand(string id)
    {
        // The single worst thing the captures showed: the legs are the lowest and nearest part of the figure, a
        // leg piece and a boot are drawn one above the other, and each used to carry two or three light bands of
        // its own. Stacked, they read as bandages rather than as armour. Small fittings (a buckle, a strap stud,
        // a narrow shin ridge) are not bands and are not counted - a band is a level light part with real top
        // area; everything else down a leg is DarkIron, or is turned so it shows its side instead of its top.
        ArmourPart[] bands = [.. Model(id).Parts.Where(p => IsLight(p.Material) && IsLevel(p) && TopFootprint(p) >= 0.02f)];
        Assert.True(bands.Length <= 1, $"{id} shows {bands.Length} bright bands down one limb");
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
    public void EveryFittedPiece_StaysInsideItsFitWindow(string id)
    {
        // The window has both sides. Ceiling: a piece wider than ~1.5-1.6x the body reads as a block that
        // swallows the figure, however well it covers; for the head and torso depth is bounded as well, so
        // the same plank cannot be rotated. Floor (head and torso, the re-authored pieces): the piece must
        // stand at least one pixel proud of the body's half-width, or equipping it does not change the outline.
        ArmourModel model = Model(id);
        (Vector3 min, Vector3 max) = Union(model);
        Assert.Null(FitProblem(id, model.Mount, min, max));
    }

    private static string? FitProblem(string id, ArmourMount mount, Vector3 min, Vector3 max)
    {
        FitWindow window = Fit[mount];
        float half = MathF.Max(max.X, -min.X);
        float depth = MathF.Max(max.Z, -min.Z);
        if (half > window.MaxHalfWidth + Eps)
        {
            return $"{id} half-width {half} exceeds {window.MaxHalfWidth} ({window.MaxHalfWidth / window.BaseHalfWidth:F2}x {window.BaseHalfWidth})";
        }

        if (depth > window.MaxHalfDepth + Eps)
        {
            return $"{id} half-depth {depth} exceeds {window.MaxHalfDepth}";
        }

        if (-min.X < window.MinHalfWidth - Eps || max.X < window.MinHalfWidth - Eps)
        {
            return $"{id} does not stand a pixel proud of the body ({window.BaseHalfWidth}) on both sides (x {min.X}..{max.X}, floor {window.MinHalfWidth})";
        }

        return null;
    }

    // The numbers the catalog's own section comments state, asserted straight off the parts rather than
    // through the FitWindow table: "nothing wider than 0.18" on the head, "nothing past 0.416" on the
    // torso, and the depth bounds that stop a plank being turned 90 degrees to dodge the width rule.
    [Theory]
    [InlineData(ArmourMount.Head, 0.18f, 0.195f)]
    [InlineData(ArmourMount.Torso, 0.416f, 0.24f)]
    [InlineData(ArmourMount.Arms, 0.207f, float.MaxValue)]
    [InlineData(ArmourMount.Legs, 0.187f, float.MaxValue)]
    [InlineData(ArmourMount.Feet, 0.198f, float.MaxValue)]
    public void EveryPieceOfAMount_StandsWithinTheSectionCommentsCeiling(ArmourMount mount, float maxHalfWidth, float maxHalfDepth)
    {
        string[] ids = [.. All.Where(id => Model(id).Mount == mount)];
        Assert.NotEmpty(ids);
        foreach (string id in ids)
        {
            (Vector3 min, Vector3 max) = Union(Model(id));
            Assert.True(MathF.Max(max.X, -min.X) <= maxHalfWidth + Eps, $"{id} half-width {MathF.Max(max.X, -min.X)} over {maxHalfWidth}");
            Assert.True(MathF.Max(max.Z, -min.Z) <= maxHalfDepth + Eps, $"{id} half-depth {MathF.Max(max.Z, -min.Z)} over {maxHalfDepth}");
        }
    }

    [Fact]
    public void TheFitFloor_RejectsAPieceThatIsExactlyTheBody()
    {
        // Control: the window has to be able to fail on each side. A model precisely as wide as the body (the
        // measured leather-vest before its yoke, 0.26 against 0.26), a plank rotated to be deep, and a block.
        Assert.NotNull(FitProblem("exact-body", ArmourMount.Torso, new(-0.26f, 0f, -0.15f), new(0.26f, 0.9f, 0.15f)));
        Assert.NotNull(FitProblem("exact-head", ArmourMount.Head, new(-0.12f, 0f, -0.135f), new(0.12f, 0.4f, 0.135f)));
        Assert.NotNull(FitProblem("sandwich-board", ArmourMount.Head, new(-0.17f, 0f, -0.25f), new(0.17f, 0.5f, 0.25f)));
        Assert.NotNull(FitProblem("block", ArmourMount.Torso, new(-0.5f, 0f, -0.15f), new(0.5f, 0.9f, 0.15f)));
        Assert.Null(FitProblem("fits", ArmourMount.Head, new(-0.17f, 0f, -0.17f), new(0.17f, 0.5f, 0.17f)));
    }

    [Fact]
    public void TheFitTable_AgreesWithTheCoverTable()
    {
        foreach ((ArmourMount mount, FitWindow window) in Fit)
        {
            float coverHalf = Covers[mount].Max(c => MathF.Max(c.Max.X, -c.Min.X));
            Assert.True(MathF.Abs(coverHalf - window.BaseHalfWidth) < Eps, $"{mount}: Fit base {window.BaseHalfWidth} vs cover {coverHalf}");
        }
    }

    public static IEnumerable<object[]> CloakIds() => Cloaks.Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(CloakIds))]
    public void EveryCloak_StaysWithinItsFitCeiling_AndHangsOffTheBody(string id)
    {
        ArmourModel model = Model(id);
        float shoulderCeiling = CloakBaseHalfWidth * TorsoCeilingMultiplier;
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

        // The panels hang at more than one depth, so the lengths do not merge into one flat plane. Distinct
        // rounded depths are not enough (0.01 apart satisfies that and the boxes still interpenetrate): the
        // consecutive tiers must be at least half a panel thickness apart, so no tier overlaps the next.
        float thickness = panels.Max(p => p.Size.Z);
        float[] tiers = [.. panels.Select(p => MathF.Round(p.Center.Z, 3)).Distinct().OrderByDescending(z => z)];
        Assert.True(tiers.Length >= 3, $"{id} panels share a depth");
        for (int i = 1; i < tiers.Length; i++)
        {
            Assert.True(tiers[i - 1] - tiers[i] >= (0.5f * thickness) - Eps, $"{id} panel tiers {tiers[i - 1]} and {tiers[i]} are closer than half their {thickness} thickness and interpenetrate");
        }
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

    [Theory]
    [InlineData("worn.back.travel-cloak", -0.45f)]
    [InlineData("worn.back.hooded-cloak", -1.00f)]
    [InlineData("worn.back.grave-mantle", -0.50f)]
    public void EveryCloak_ReachesItsContractLength(string id, float hemAtMost)
    {
        // An absolute length, stated as a contract and not read off the baked geometry: every relative check
        // survives scaling a cloak's y by half, which turns a calf-length cloak into a waist cape.
        (Vector3 min, _) = Union(Model(id));
        Assert.True(min.Y <= hemAtMost, $"{id} hem is at {min.Y}, above its contract length {hemAtMost}");
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

        // Albedo, not final pixel colour: the shader multiplies by a gain of up to ~2.2 with no tonemap, so a lit
        // channel above ~0.45 clips to a flat patch. Pieces differ by value and material, not by saturation.
        Assert.Equal(new Vector4(0.30f, 0.315f, 0.345f, 1f), ArmourMeshCatalog.BaseColor(ArmourMaterial.Steel));
        Assert.Equal(new Vector4(0.07f, 0.078f, 0.10f, 1f), ArmourMeshCatalog.BaseColor(ArmourMaterial.ClothCold));
        Assert.Equal(new Vector4(0.17f, 0.162f, 0.145f, 1f), ArmourMeshCatalog.BaseColor(ArmourMaterial.ClothAsh));
        Assert.Equal(new Vector4(0.20f, 0.055f, 0.045f, 1f), ArmourMeshCatalog.BaseColor(ArmourMaterial.ClothRed));
        foreach (ArmourMaterial material in Enum.GetValues<ArmourMaterial>())
        {
            Vector4 c = ArmourMeshCatalog.BaseColor(material);
            Assert.True(MathF.Max(c.X, MathF.Max(c.Y, c.Z)) <= 0.44f, $"{material} albedo {c} would clip under the actor gain");
        }
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
    public void PartMatrix_TurnsAboutYawLast_AndYawLeavesTheHeightAlone()
    {
        // (0.5, 0.5, 0.5) scaled to (1, 2, 3), yawed 90 degrees about Y to (3, 2, -1), moved by (1, 2, 3).
        ArmourPart yawed = new(new Vector3(1f, 2f, 3f), new Vector3(2f, 4f, 6f), ArmourMaterial.Steel, YawDegrees: 90f);
        Vector3 corner = Vector4.TransformRow(new Vector4(0.5f, 0.5f, 0.5f, 1f), yawed.ToMatrix()).Xyz;
        Assert.Equal(4f, corner.X, 3);
        Assert.Equal(4f, corner.Y, 3);
        Assert.Equal(2f, corner.Z, 3);

        // Yaw is about Y, so it can never change a part's height: the sculpt is free to use it without
        // moving any piece inside its mount's y limit.
        foreach (string id in All)
        {
            foreach (ArmourPart part in Model(id).Parts.Where(p => p.YawDegrees != 0f))
            {
                ArmourPart flat = part with { YawDegrees = 0f };
                Assert.Equal(PartBounds(flat).Min.Y, PartBounds(part).Min.Y, 4);
                Assert.Equal(PartBounds(flat).Max.Y, PartBounds(part).Max.Y, 4);
            }
        }
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
