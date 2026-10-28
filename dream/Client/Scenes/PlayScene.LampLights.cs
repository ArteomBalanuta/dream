using Dream.Client.Presentation;
using Dream.Rendering.Lighting;
using Dream.Rendering.World;

namespace Dream.Client.Scenes;

// Wave T2c: the lamp blocks' point lights, collected each frame before the world draw
// (PlayScene.Lights.cs). LampLightIndex rescans static lamps only when the static content changes.
public partial class PlayScene
{
    private readonly LampLightIndex _lampLights = new();

    partial void CollectLampLights(LightCollector lights, in WorldSessionPresentationState presentation)
    {
        _lampLights.Collect(presentation.VisibleWorld, lights);
    }
}
