using System.Collections.ObjectModel;

public class ViewModelCatalog
{
    private readonly IReadOnlyDictionary<string, ViewModelDefinition> _definitions;

    public ViewModelCatalog(IEnumerable<ViewModelDefinition> definitions, ViewModelLoadout defaultLoadout)
    {
        Dictionary<string, ViewModelDefinition> map = definitions.ToDictionary(d => d.Id, d => d);

        ValidateSlot(map, defaultLoadout.HandsId, ViewModelSlot.Hands);
        ValidateSlot(map, defaultLoadout.HeldItemId, ViewModelSlot.HeldItem);

        _definitions = new ReadOnlyDictionary<string, ViewModelDefinition>(map);
        DefaultLoadout = defaultLoadout;
    }

    public ViewModelLoadout DefaultLoadout { get; }

    public ViewModelDefinition GetDefinition(string id)
    {
        if (!_definitions.TryGetValue(id, out ViewModelDefinition definition))
        {
            throw new KeyNotFoundException($"Unknown viewmodel id '{id}'.");
        }

        return definition;
    }

    public static ViewModelCatalog CreateDefault()
    {
        return new ViewModelCatalog(
            new[]
            {
                ViewModelAssets.DebugHandsDefinition,
                ViewModelAssets.DebugHeldItemDefinition
            },
            new ViewModelLoadout(
                ViewModelAssets.DebugHandsDefinition.Id,
                ViewModelAssets.DebugHeldItemDefinition.Id));
    }

    private static void ValidateSlot(
        IReadOnlyDictionary<string, ViewModelDefinition> map,
        string id,
        ViewModelSlot expectedSlot)
    {
        if (!map.TryGetValue(id, out ViewModelDefinition definition))
        {
            throw new ArgumentException($"Default viewmodel id '{id}' is not registered.");
        }

        if (definition.Slot != expectedSlot)
        {
            throw new ArgumentException(
                $"Viewmodel '{id}' must be registered as slot '{expectedSlot}' but was '{definition.Slot}'.");
        }
    }
}
