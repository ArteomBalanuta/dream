namespace Dream.Server.Worlds;

// No world matches the selector (an id that does not exist).
public sealed class WorldNotFoundException(string selector)
    : InvalidOperationException($"World '{selector}' does not exist.")
{
    public string Selector { get; } = selector;
}
