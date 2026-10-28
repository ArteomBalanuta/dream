using Dream.Client.Presentation;
using Dream.Rendering.Lighting;

namespace Dream.Client.Scenes;

// The frame's point lights. CollectHeldLights (PlayScene.HeldLights.cs) runs first: there are at
// most a few held lights, one per lit player, and they must never lose their slots in the 1,024-slot
// LightCollector to lamps. CollectLampLights (PlayScene.LampLights.cs) fills the rest with the lamps
// near the camera. Unimplemented partials compile away.
public partial class PlayScene
{
    private void CollectFrameLights(in WorldSessionPresentationState presentation, IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        LightCollector lights = _render.Lights;
        lights.Clear();
        CollectHeldLights(lights, in presentation, remotePlayers);
        CollectLampLights(lights, in presentation);
    }

    partial void CollectLampLights(LightCollector lights, in WorldSessionPresentationState presentation);

    partial void CollectHeldLights(LightCollector lights, in WorldSessionPresentationState presentation, IReadOnlyList<RemotePlayerPresentationState> remotePlayers);
}
