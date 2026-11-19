using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Actors;

namespace Dream.Tests.Rendering.Actors;

public class WeaponMeshCatalogTests
{
    public static readonly string[] Weapons =
    [
        "held.weapon.dagger", "held.weapon.sword", "held.weapon.greatsword", "held.weapon.axe",
        "held.weapon.mace", "held.weapon.hammer", "held.weapon.spear", "held.weapon.bow", "held.weapon.staff",
        "held.weapon.short-sword", "held.weapon.iron-blade", "held.weapon.hand-axe", "held.weapon.short-bow",
        "held.weapon.bone-staff"
    ];

    public static readonly string[] Shields = ["held.shield.buckler", "held.shield.kite", "held.shield.tower"];

    public static readonly string[] Tools =
        ["held.tool.builder", "held.tool.blade", "held.tool.maul", "held.tool.lantern", "held.tool.torch", "held.gear.ward-lantern"];

    public static IEnumerable<object[]> EveryKnownId() =>
        Weapons.Concat(Shields).Concat(Tools).Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(EveryKnownId))]
    public void EveryItemKindHasItsOwnMesh(string id)
    {
        Assert.True(WeaponMeshCatalog.IsKnown(id));
        WeaponModel model = WeaponMeshCatalog.Resolve(id);

        Assert.Equal(id, model.VisualId);
        Assert.True(model.Parts.Count >= 3, "a recognisable mesh needs several parts");
        // The ornament budget: form, then secondary detail, then stop. Fine texture is the shader's job.
        Assert.True(model.Parts.Count <= 14, $"{id} has {model.Parts.Count} parts; the budget is 14");
        Assert.NotSame(WeaponMeshCatalog.Resolve("held.unknown"), model);
    }

    [Fact]
    public void NoTwoItemsShareAShape()
    {
        string[] ids = Weapons.Concat(Shields).Concat(Tools).ToArray();
        HashSet<string> shapes = [];
        foreach (string id in ids)
        {
            string shape = string.Join(";", WeaponMeshCatalog.Resolve(id).Parts.Select(p => $"{p.Center}{p.Size}{p.Material}{p.PitchDegrees}{p.RollDegrees}{p.YawDegrees}{p.Shape}"));
            Assert.True(shapes.Add(shape), $"{id} has the same mesh as another item");
        }
    }

    // The id set, pinned. Every other test here reads the catalog, so a typo that dropped an item would
    // only shrink what the suite looks at; this is the one assertion that notices.
    [Fact]
    public void TheCatalogHoldsExactlyTheseTwentyThreeIds()
    {
        string[] expected = [.. Weapons.Concat(Shields).Concat(Tools)];
        Assert.Equal(23, expected.Length);
        Assert.Equal(23, expected.Distinct().Count());
        Assert.Equal(
            expected.OrderBy(s => s, StringComparer.Ordinal),
            WeaponMeshCatalog.KnownVisualIds.OrderBy(s => s, StringComparer.Ordinal));
        Assert.False(WeaponMeshCatalog.IsKnown(WeaponMeshCatalog.GenericVisualId));
    }

    [Theory]
    [MemberData(nameof(EveryKnownId))]
    public void EveryPart_HasAFiniteSize_ADeclaredMaterial_AndADeclaredShape(string id)
    {
        foreach (WeaponPart part in WeaponMeshCatalog.Resolve(id).Parts)
        {
            Assert.True(float.IsFinite(part.Size.X) && float.IsFinite(part.Size.Y) && float.IsFinite(part.Size.Z), $"{id} {part.Center} has a non-finite size");
            Assert.True(part.Size.X > 0f && part.Size.Y > 0f && part.Size.Z > 0f, $"{id} {part.Center} has a non-positive size");
            Assert.True(float.IsFinite(part.Center.X) && float.IsFinite(part.Center.Y) && float.IsFinite(part.Center.Z));
            Assert.True(float.IsFinite(part.PitchDegrees) && float.IsFinite(part.RollDegrees) && float.IsFinite(part.YawDegrees));
            Assert.Contains(part.Material, Enum.GetValues<WeaponMaterial>());
            Assert.Contains(part.Shape, Enum.GetValues<EquipmentShape>());
        }
    }

    // The sculpt gate. Every part of every held model used to be one axis-aligned cube, which is what made
    // the weapons read as stacked blocks; this is what stops them silently going back. A ceiling on plain
    // boxes AND a named shape per family, so the boxes cannot be traded for one shape used everywhere.
    [Fact]
    public void NoHeldPartIsAPlainBox_AndEachFamilyUsesItsOwnShape()
    {
        WeaponPart[] every =
        [
            .. Weapons.Concat(Shields).Concat(Tools).Append(WeaponMeshCatalog.GenericVisualId)
                .SelectMany(id => WeaponMeshCatalog.Resolve(id).Parts)
        ];
        Assert.Empty(every.Where(p => p.Shape == EquipmentShape.Box));

        (string What, EquipmentShape Shape)[] families =
        [
            ("a blade is a Blade", EquipmentShape.Blade),
            ("a haft, shaft or string is a Shaft", EquipmentShape.Shaft),
            ("an axe bit or a swept guard is a Crescent", EquipmentShape.Crescent),
            ("a pommel, boss or nock is a Dome", EquipmentShape.Dome),
            ("a pommel ring, collar or binding is a Ring", EquipmentShape.Ring),
            ("a ferrule, quillon or socket is a Taper", EquipmentShape.Taper),
            ("a stud, flange or finial is a Spike", EquipmentShape.Spike),
            ("a tassel or a shield's cloth is a Panel", EquipmentShape.Panel),
            ("an arcane or ember stone is a Gem", EquipmentShape.Gem),
            ("a curved shield board is a Plate", EquipmentShape.Plate),
            ("a fuller, plank or iron band is a BevelBox", EquipmentShape.BevelBox)
        ];
        foreach ((string what, EquipmentShape shape) in families)
        {
            Assert.True(every.Any(p => p.Shape == shape), what);
        }

        // Each weapon family's defining shape, named per item, so a re-sculpt cannot quietly turn a sword
        // back into a stick that merely happens to use a Blade somewhere else in the catalog.
        (string Id, EquipmentShape Shape)[] defining =
        [
            ("held.weapon.dagger", EquipmentShape.Blade),
            ("held.weapon.short-sword", EquipmentShape.Blade),
            ("held.weapon.sword", EquipmentShape.Blade),
            ("held.weapon.greatsword", EquipmentShape.Blade),
            ("held.weapon.iron-blade", EquipmentShape.Blade),
            ("held.weapon.axe", EquipmentShape.Crescent),
            ("held.weapon.hand-axe", EquipmentShape.Crescent),
            ("held.weapon.mace", EquipmentShape.Spike),
            ("held.weapon.hammer", EquipmentShape.BevelBox),
            ("held.weapon.spear", EquipmentShape.Blade),
            ("held.weapon.staff", EquipmentShape.Gem),
            ("held.weapon.bone-staff", EquipmentShape.Gem),
            ("held.weapon.bow", EquipmentShape.Taper),
            ("held.weapon.short-bow", EquipmentShape.Taper),
            ("held.shield.buckler", EquipmentShape.Dome),
            ("held.shield.kite", EquipmentShape.Plate),
            ("held.shield.tower", EquipmentShape.BevelBox),
            ("held.tool.torch", EquipmentShape.Gem),
            ("held.gear.ward-lantern", EquipmentShape.Gem)
        ];
        foreach ((string id, EquipmentShape shape) in defining)
        {
            Assert.Contains(WeaponMeshCatalog.Resolve(id).Parts, p => p.Shape == shape);
        }

        // Every haft and grip is a Shaft: this is the single most common blocky read in the old catalog.
        foreach (string id in Weapons.Concat(Tools))
        {
            Assert.Contains(WeaponMeshCatalog.Resolve(id).Parts, p => p.Shape == EquipmentShape.Shaft);
        }
    }

    // The blunt and hafted families are a stick plus a lump at the shipped 28x101 px actor, and a lump that is
    // the same front and back carries no information at all: it cannot say which way the weapon faces or which
    // end strikes. Left-right symmetry is natural and stays; FRONT-BACK symmetry is the defect. The old heads
    // were built on four equal flanges or talons at the compass points, so every part had a mirror twin across
    // z and the outline was a knob. At least one part of each head must now have no such twin - a longer rear
    // pick, a lower front beak, a jutting jaw, one talon swept further than the rest.
    [Theory]
    [InlineData("held.weapon.mace")]
    [InlineData("held.weapon.hammer")]
    [InlineData("held.weapon.staff")]
    [InlineData("held.weapon.bone-staff")]
    public void BluntAndHaftedHeads_AreNotTheSameFrontAndBack(string id)
    {
        WeaponPart[] parts = [.. WeaponMeshCatalog.Resolve(id).Parts];

        static bool SameSize(Vector3 a, Vector3 b) =>
            MathF.Abs(a.X - b.X) < 1e-4f && MathF.Abs(a.Y - b.Y) < 1e-4f && MathF.Abs(a.Z - b.Z) < 1e-4f;

        bool HasMirrorTwin(WeaponPart part) => parts.Any(other =>
            MathF.Abs(other.Center.X - part.Center.X) < 1e-4f
            && MathF.Abs(other.Center.Y - part.Center.Y) < 1e-4f
            && MathF.Abs(other.Center.Z + part.Center.Z) < 1e-4f
            && SameSize(other.Size, part.Size));

        Assert.Contains(parts, p => !HasMirrorTwin(p));
    }

    [Fact]
    public void ShieldsAreStrappedToTheArm_AndOnlyTheBowsAreRanged()
    {
        foreach (string id in Shields)
        {
            Assert.True(WeaponMeshCatalog.IsShield(id));
            Assert.Equal(WeaponGrip.Shield, WeaponMeshCatalog.Resolve(id).Grip);
        }

        foreach (string id in Weapons.Concat(Tools))
        {
            Assert.False(WeaponMeshCatalog.IsShield(id));
            Assert.Equal(id is "held.weapon.bow" or "held.weapon.short-bow", WeaponMeshCatalog.IsRanged(id));
        }
    }

    [Fact]
    public void WeaponLengthsReadAsTheirKind()
    {
        float Length(string id) => WeaponMeshCatalog.Resolve(id).Parts.Max(p => p.Center.Y + (p.Size.Y / 2f))
            - WeaponMeshCatalog.Resolve(id).Parts.Min(p => p.Center.Y - (p.Size.Y / 2f));

        Assert.True(Length("held.weapon.dagger") < Length("held.weapon.sword"));
        Assert.True(Length("held.weapon.dagger") < Length("held.weapon.short-sword"));
        Assert.True(Length("held.weapon.short-sword") < Length("held.weapon.sword"));
        Assert.True(Length("held.weapon.hand-axe") < Length("held.weapon.axe"));
        Assert.True(Length("held.weapon.short-bow") < Length("held.weapon.bow"));
        Assert.True(Length("held.weapon.sword") < Length("held.weapon.greatsword"));
        Assert.True(Length("held.weapon.sword") < Length("held.weapon.spear"));
        Assert.True(Length("held.weapon.axe") < Length("held.weapon.staff"));
    }

    [Fact]
    public void UnknownHeldItems_DrawAsAPlainStick()
    {
        WeaponModel model = WeaponMeshCatalog.Resolve("held.something.new");

        Assert.Equal(WeaponMeshCatalog.GenericVisualId, model.VisualId);
        Assert.NotEmpty(model.Parts);
        Assert.False(WeaponMeshCatalog.IsKnown("held.something.new"));
    }

    [Fact]
    public void Materials_AreDark_AndOnlyGlowingOnesEmit()
    {
        foreach (WeaponMaterial material in Enum.GetValues<WeaponMaterial>())
        {
            Vector4 color = WeaponMeshCatalog.BaseColor(material);
            Assert.InRange(color.X, 0f, 1f);
            Assert.Equal(1f, color.W);
            bool glows = WeaponMeshCatalog.Emissive(material) != Vector3.Zero;
            Assert.Equal(material is WeaponMaterial.Arcane or WeaponMaterial.Ember, glows);
            if (!glows)
            {
                Assert.True((color.X + color.Y + color.Z) / 3f < 0.7f, $"{material} is too bright for the dark palette");
            }
        }
    }

    [Fact]
    public void Palette_IsAlbedoNotPixelColour_AndTheMaterialsStayApartByValue()
    {
        // Albedo, not final pixel colour: the shader multiplies by a gain of up to ~2.2 with no tonemap, so a lit
        // channel above ~0.45 clips to a flat patch (the starter torch was a yellow head, the axe a cyan blade).
        Assert.Equal(new Vector4(0.33f, 0.34f, 0.365f, 1f), WeaponMeshCatalog.BaseColor(WeaponMaterial.Steel));
        Assert.Equal(new Vector4(0.11f, 0.11f, 0.125f, 1f), WeaponMeshCatalog.BaseColor(WeaponMaterial.DarkIron));
        Assert.Equal(new Vector4(0.20f, 0.125f, 0.07f, 1f), WeaponMeshCatalog.BaseColor(WeaponMaterial.Wood));
        Assert.Equal(new Vector4(0.13f, 0.08f, 0.06f, 1f), WeaponMeshCatalog.BaseColor(WeaponMaterial.Leather));
        Assert.Equal(new Vector4(0.24f, 0.10f, 0.04f, 1f), WeaponMeshCatalog.BaseColor(WeaponMaterial.Ember));
        Assert.Equal(new Vector4(0.10f, 0.06f, 0.17f, 1f), WeaponMeshCatalog.BaseColor(WeaponMaterial.Arcane));
        Assert.Equal(new Vector4(0.40f, 0.36f, 0.29f, 1f), WeaponMeshCatalog.BaseColor(WeaponMaterial.Bone));

        foreach (WeaponMaterial material in Enum.GetValues<WeaponMaterial>())
        {
            Vector4 c = WeaponMeshCatalog.BaseColor(material);
            Assert.True(MathF.Max(c.X, MathF.Max(c.Y, c.Z)) <= 0.44f, $"{material} albedo {c} would clip under the actor gain");
        }

        // Steel is a desaturated blue-grey, never a cyan: no channel more than ~12% off the mean.
        Vector4 steel = WeaponMeshCatalog.BaseColor(WeaponMaterial.Steel);
        float mean = (steel.X + steel.Y + steel.Z) / 3f;
        Assert.True(MathF.Max(steel.X, MathF.Max(steel.Y, steel.Z)) / mean < 1.12f);

        // Distinguished by value: blackened iron < leather < worn wood < steel.
        static float Value(WeaponMaterial m) => WeaponMeshCatalog.BaseColor(m).X;
        Assert.True(Value(WeaponMaterial.DarkIron) <= Value(WeaponMaterial.Leather));
        Assert.True(Value(WeaponMaterial.Leather) < Value(WeaponMaterial.Wood));
        Assert.True(Value(WeaponMaterial.Wood) < Value(WeaponMaterial.Steel));
    }

    [Fact]
    public void TorchFlameGlows_AndItsBodyIsDark()
    {
        WeaponModel torch = WeaponMeshCatalog.Resolve("held.tool.torch");
        Assert.Contains(torch.Parts, p => p.Material == WeaponMaterial.Ember);
        foreach (WeaponPart part in torch.Parts.Where(p => p.Material != WeaponMaterial.Ember))
        {
            Assert.Equal(Vector3.Zero, WeaponMeshCatalog.Emissive(part.Material));
        }

        // The glow rides on a dark body (light on a dark piece); the flame's own albedo is not what lights it.
        Assert.True(WeaponMeshCatalog.BaseColor(WeaponMaterial.Ember).X < 0.4f);
        Assert.True(WeaponMeshCatalog.Emissive(WeaponMaterial.Ember).X > 0.8f);
    }

    [Fact]
    public void PartMatrix_ScalesThenTurnsThenMoves()
    {
        WeaponPart part = new(new Vector3(1f, 2f, 3f), new Vector3(2f, 4f, 6f), WeaponMaterial.Steel, 0f, 90f);

        Vector3 corner = Vector4.TransformRow(new Vector4(0.5f, 0.5f, 0.5f, 1f), part.ToMatrix()).Xyz;

        // (0.5,0.5,0.5) scaled to (1,2,3), rolled 90 degrees about Z to (-2,1,3), moved by (1,2,3).
        Assert.Equal(-1f, corner.X, 3);
        Assert.Equal(3f, corner.Y, 3);
        Assert.Equal(6f, corner.Z, 3);
    }

    [Fact]
    public void PartMatrix_TurnsAboutYawLast_AndYawLeavesTheDrawnLengthAlone()
    {
        // (0.5,0.5,0.5) scaled to (1,2,3), yawed 90 degrees about Y to (3,2,-1), moved by (1,2,3).
        WeaponPart part = new(new Vector3(1f, 2f, 3f), new Vector3(2f, 4f, 6f), WeaponMaterial.Steel, YawDegrees: 90f);

        Vector3 corner = Vector4.TransformRow(new Vector4(0.5f, 0.5f, 0.5f, 1f), part.ToMatrix()).Xyz;

        Assert.Equal(4f, corner.X, 3);
        Assert.Equal(4f, corner.Y, 3);
        Assert.Equal(2f, corner.Z, 3);

        // Yaw turns about Y, so it cannot change the Y extent HeldItemScale measures a weapon's length by.
        foreach (string id in Weapons.Concat(Shields).Concat(Tools))
        {
            WeaponPart[] parts = [.. WeaponMeshCatalog.Resolve(id).Parts];
            WeaponPart[] unyawed = [.. parts.Select(p => p with { YawDegrees = 0f })];
            Assert.Equal(HeldItemScale.RotatedExtentModelUnits(parts), HeldItemScale.RotatedExtentModelUnits(unyawed), 4);
        }
    }

    [Fact]
    public void Grip_TurnsTheWeaponLengthForwardOnAHangingArm_AndTheShieldFaceOutward()
    {
        Matrix4 handGrip = HeldWeaponRenderer.CreateGrip(WeaponGrip.Hand, offHand: false);
        Vector3 tip = Vector4.TransformRow(new Vector4(0f, 1f, 0f, 0f), handGrip).Xyz;
        Assert.Equal(1f, tip.Z, 3);
        Assert.Equal(-HeldWeaponRenderer.FistOffsetX, Vector4.TransformRow(new Vector4(0f, 0f, 0f, 1f), handGrip).X, 3);

        Vector3 rightFace = Vector4.TransformRow(new Vector4(0f, 0f, 1f, 0f), HeldWeaponRenderer.CreateGrip(WeaponGrip.Shield, offHand: false)).Xyz;
        Vector3 leftFace = Vector4.TransformRow(new Vector4(0f, 0f, 1f, 0f), HeldWeaponRenderer.CreateGrip(WeaponGrip.Shield, offHand: true)).Xyz;
        Assert.Equal(-1f, rightFace.X, 3);
        Assert.Equal(1f, leftFace.X, 3);
    }
}
