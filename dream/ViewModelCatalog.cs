using System.Collections.ObjectModel;

public class ViewModelCatalog
{
    private readonly IReadOnlyDictionary<string, ViewModelDefinition> _definitions;

    public ViewModelCatalog(IEnumerable<ViewModelDefinition> definitions, ViewModelLoadout defaultLoadout)
    {
        Dictionary<string, ViewModelDefinition> map = BuildDefinitionMap(definitions);

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
        return new ViewModelCatalog(ViewModelAssets.BuiltInDefinitions, ViewModelAssets.DefaultLoadout);
    }

    private static Dictionary<string, ViewModelDefinition> BuildDefinitionMap(
        IEnumerable<ViewModelDefinition> definitions)
    {
        Dictionary<string, ViewModelDefinition> map = new();

        foreach (ViewModelDefinition definition in definitions)
        {
            if (!map.TryAdd(definition.Id, definition))
            {
                throw new ArgumentException($"Duplicate viewmodel id '{definition.Id}' is not allowed.");
            }
        }

        return map;
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
