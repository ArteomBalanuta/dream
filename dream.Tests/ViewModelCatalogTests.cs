using Xunit;

public class ViewModelCatalogTests
{
    [Fact]
    public void DefaultLoadout_UsesRegisteredHandsAndHeldItem()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        ViewModelLoadout loadout = catalog.DefaultLoadout;

        ViewModelDefinition hands = catalog.GetDefinition(loadout.HandsId);
        ViewModelDefinition heldItem = catalog.GetDefinition(loadout.HeldItemId);

        Assert.Equal(ViewModelSlot.Hands, hands.Slot);
        Assert.Equal(ViewModelSlot.HeldItem, heldItem.Slot);
    }

    [Fact]
    public void GetDefinition_ThrowsForUnknownId()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();

        Assert.Throws<KeyNotFoundException>(() => catalog.GetDefinition("missing"));
    }

    [Fact]
    public void Catalog_RejectsSlotMismatchInDefaultLoadout()
    {
        var hands = new ViewModelDefinition(
            "hands.debug",
            ViewModelSlot.Hands,
            "hands.gltf",
            "Idle",
            "Use",
            new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
            new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
            1f);

        var held = new ViewModelDefinition(
            "held.debug",
            ViewModelSlot.Hands,
            "held.gltf",
            "Idle",
            "Use",
            new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
            new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
            1f);

        Assert.Throws<ArgumentException>(() =>
            new ViewModelCatalog(
                new[] { hands, held },
                new ViewModelLoadout("hands.debug", "held.debug")));
    }

    [Fact]
    public void Catalog_RejectsUnregisteredDefaultLoadoutId()
    {
        var hands = new ViewModelDefinition(
            "hands.debug",
            ViewModelSlot.Hands,
            "hands.gltf",
            "Idle",
            "Use",
            new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
            new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
            1f);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new ViewModelCatalog(
                new[] { hands },
                new ViewModelLoadout("hands.debug", "held.debug")));

        Assert.Contains("not registered", exception.Message);
    }

    [Fact]
    public void Catalog_RejectsDuplicateDefinitionIds()
    {
        var first = new ViewModelDefinition(
            "shared.debug",
            ViewModelSlot.Hands,
            "hands.gltf",
            "Idle",
            "Use",
            new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
            new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
            1f);

        var second = new ViewModelDefinition(
            "shared.debug",
            ViewModelSlot.HeldItem,
            "held.gltf",
            "Idle",
            "Use",
            new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
            new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
            1f);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new ViewModelCatalog(
                new[] { first, second },
                new ViewModelLoadout("shared.debug", "shared.debug")));

        Assert.Contains("Duplicate viewmodel id", exception.Message);
    }
}
