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
        Assert.NotSame(WeaponMeshCatalog.Resolve("held.unknown"), model);
    }

    [Fact]
    public void NoTwoItemsShareAShape()
    {
        string[] ids = Weapons.Concat(Shields).Concat(Tools).ToArray();
        HashSet<string> shapes = [];
        foreach (string id in ids)
        {
            string shape = string.Join(";", WeaponMeshCatalog.Resolve(id).Parts.Select(p => $"{p.Center}{p.Size}{p.Material}{p.PitchDegrees}{p.RollDegrees}"));
            Assert.True(shapes.Add(shape), $"{id} has the same mesh as another item");
        }
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
