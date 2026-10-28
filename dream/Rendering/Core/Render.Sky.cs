using Dream.Rendering.Sky;

namespace Dream.Rendering.Core;

public partial class Render
{
    // The client-local debug time of day, edited in the Settings window. The scene resolves the
    // replicated time through it before evaluating the sky; nothing here reaches the network.
    public TimeOfDayOverride TimeOfDayOverride { get; } = new();
}
