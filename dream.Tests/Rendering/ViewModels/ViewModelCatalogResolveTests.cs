using Xunit;
using Dream.Rendering.ViewModels;

namespace Dream.Tests.Rendering.ViewModels;

// Wave H3 (H-D16): a held slot never throws on an id this client does not know.
public class ViewModelCatalogResolveTests
{
    [Fact]
    public void Resolve_ReturnsARegisteredHeldItem()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();

        Assert.Equal(ViewModelAssets.BladeToolDefinition, catalog.Resolve("held.tool.blade"));
    }

    [Theory]
    [InlineData("visual.nobody-registered")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("hands.debug")]
    public void Resolve_FallsBackToTheDebugItem_ForAnythingNotARegisteredHeldItem(string? id)
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();

        Assert.Equal(ViewModelAssets.DebugHeldItemDefinition, catalog.Resolve(id));
    }

    [Fact]
    public void GetDefinition_StillThrows_ForAnUnregisteredId()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();

        Assert.Throws<KeyNotFoundException>(() => catalog.GetDefinition("visual.nobody-registered"));
    }

    [Fact]
    public void DefaultCatalog_HasTheEmptyHeldItem_WithNoModel()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();

        ViewModelDefinition none = catalog.Resolve(ViewModelAssets.EmptyHeldItemId);

        Assert.Equal("held.none", none.Id);
        Assert.Equal(string.Empty, none.ModelPath);
        Assert.True(ViewModelRenderer.IsEmptySlot(new ViewModelSlotState(none, ViewModelAnimationRequest.Use, default)));
    }

    [Fact]
    public void EveryPreloadedDefinition_HasAModelPath()
    {
        // PreloadModels requests each built-in path; held.none stays out of that list.
        Assert.All(ViewModelAssets.BuiltInDefinitions, definition => Assert.False(string.IsNullOrEmpty(definition.ModelPath)));
    }
}
