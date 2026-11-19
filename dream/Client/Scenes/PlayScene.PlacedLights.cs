using Dream.Client.Presentation;
using Dream.Rendering.Lighting;

namespace Dream.Client.Scenes;

// The world's authored brazier lights, collected each frame after the held lights and before the
// lamps (PlayScene.Lights.cs). The index lives on Render (Render.PlacedLights) so the props that
// follow the lights read the same placements; it rescans only when the static content changes.
public partial class PlayScene
{
    partial void CollectPlacedLights(LightCollector lights, in WorldSessionPresentationState presentation)
    {
        _render.PlacedLights.Collect(
            presentation.VisibleWorld, presentation.Camera.Position, lights, _render.CurrentFrameLighting.SunDirection.W,
            FireClockSeconds);
    }
}
