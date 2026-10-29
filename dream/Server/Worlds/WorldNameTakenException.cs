namespace Dream.Server.Worlds;

// Another world already has this name (names are unique ignoring case).
public sealed class WorldNameTakenException(string name)
    : InvalidOperationException($"A world named '{name}' already exists.")
{
    public string WorldName { get; } = name;
}
