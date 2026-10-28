using Dream.Rendering.Lighting;

namespace Dream.Rendering.Core;

// Stub (wave T2 C0). T2a adds the LightList uniform buffer and the debug light ring here.
public partial class Render
{
    public LightCollector Lights { get; } = new();
}
