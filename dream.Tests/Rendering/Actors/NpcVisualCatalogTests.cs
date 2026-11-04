using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Actors;

// ClientNpc.VisualKey comes off the wire, so the resolver is treated like EnemyVisualCatalog's:
// null, unknown, cased, padded and absurd keys all draw the default villager and nothing throws.
public sealed class NpcVisualCatalogTests
{
    [Fact]
    public void TheVendor_IsTheDefault()
    {
        NpcVisual vendor = NpcVisualCatalog.Resolve(NpcVisualCatalog.VendorKey);

        Assert.Equal(NpcAssetDefinition.VendorModelPath, vendor.ModelPath);
        Assert.Equal(NpcAssetDefinition.IdleClip, vendor.IdleClip);
        Assert.Equal(NpcAssetDefinition.WalkClip, vendor.WalkClip);
        Assert.Equal(NpcVisualCatalog.Default, vendor);
    }

    [Fact]
    public void TheSmithAndTheWarden_HaveTheirOwnModels()
    {
        NpcVisual smith = NpcVisualCatalog.Resolve(NpcVisualCatalog.SmithKey);
        NpcVisual warden = NpcVisualCatalog.Resolve(NpcVisualCatalog.WardenKey);

        Assert.Equal(NpcAssetDefinition.SmithModelPath, smith.ModelPath);
        Assert.Equal(NpcAssetDefinition.WardenModelPath, warden.ModelPath);
        Assert.NotEqual(smith.ModelPath, warden.ModelPath);
        Assert.NotEqual(NpcAssetDefinition.VendorModelPath, smith.ModelPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("npc.unknown")]
    [InlineData("NPC.SMITH")]
    [InlineData("npc.smith ")]
    [InlineData("npc.warde")]
    public void UnknownOrNullKeys_DrawTheDefaultVillager(string? key)
    {
        Assert.Equal(NpcVisualCatalog.Default, NpcVisualCatalog.Resolve(key));
    }

    [Fact]
    public void AnOversizedKey_DrawsTheDefault_AndDoesNotThrow()
    {
        string huge = new('n', 10_000);

        NpcVisual visual = default;
        Exception? thrown = Record.Exception(() => visual = NpcVisualCatalog.Resolve(huge));

        Assert.Null(thrown);
        Assert.Equal(NpcVisualCatalog.Default, visual);
    }

    [Fact]
    public void Resolving_AllocatesNothing()
    {
        Dream.Tests.Utilities.AllocationAssert.AtMost(0, () =>
        {
            _ = NpcVisualCatalog.Resolve(NpcVisualCatalog.SmithKey);
            _ = NpcVisualCatalog.Resolve(null);
            _ = NpcVisualCatalog.Resolve("nope");
        });
    }

    // The scale is measured from the GLB on first use. Until the asset is forged the measurement
    // must not throw inside a draw: a zero authored height resolves to a uniform scale of 1.
    [Fact]
    public void ResolvingTheScale_NeverThrows_EvenBeforeTheAssetIsForged()
    {
        float scale = 0f;
        Exception? thrown = Record.Exception(() => scale = NpcVisualCatalog.Default.ResolveScale());

        Assert.Null(thrown);
        Assert.True(scale > 0f);
    }

    [Fact]
    public void TheModelMatrix_ScalesThenYawsThenPlacesAtTheFeet()
    {
        NpcVisual vendor = NpcVisualCatalog.Default;
        Vector3 position = new(3f, 1.5f, -2f);

        Matrix4 matrix = NpcModelRenderer.BuildModelMatrix(in vendor, position, Vector3.UnitX);

        Assert.Equal(position.X, matrix.M41, 3);
        Assert.Equal(position.Y, matrix.M42, 3);
        Assert.Equal(position.Z, matrix.M43, 3);

        Vector3 forward = Vector4.TransformRow(
            new Vector4(NpcAssetDefinition.AuthoredForwardAxis, 0f), matrix).Xyz.Normalized();
        Assert.Equal(1f, forward.X, 3);
        Assert.Equal(0f, forward.Z, 3);

        float scale = vendor.ResolveScale();
        Assert.Equal(scale, new Vector3(matrix.M11, matrix.M12, matrix.M13).Length, 3);
    }

    [Fact]
    public void ADegenerateFacing_DoesNotCollapseTheMatrix()
    {
        Matrix4 matrix = NpcModelRenderer.BuildModelMatrix(in Default, Vector3.Zero, Vector3.Zero);

        Assert.True(MathF.Abs(matrix.Determinant) > 1e-6f);
    }

    private static readonly NpcVisual Default = NpcVisualCatalog.Default;
}
