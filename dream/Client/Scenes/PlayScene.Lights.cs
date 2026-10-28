using Dream.Client.Presentation;
using Dream.Rendering.Lighting;

namespace Dream.Client.Scenes;

// Frozen seam (wave T2 C0). T2c implements CollectLampLights in PlayScene.LampLights.cs, and
// T2b implements CollectHeldLights in PlayScene.HeldLights.cs. Unimplemented partials compile away.
public partial class PlayScene
{
    private void CollectFrameLights(in WorldSessionPresentationState presentation, IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        LightCollector lights = _render.Lights;
        lights.Clear();
        CollectLampLights(lights, in presentation);
        CollectHeldLights(lights, in presentation, remotePlayers);
    }

    partial void CollectLampLights(LightCollector lights, in WorldSessionPresentationState presentation);

    partial void CollectHeldLights(LightCollector lights, in WorldSessionPresentationState presentation, IReadOnlyList<RemotePlayerPresentationState> remotePlayers);
}
