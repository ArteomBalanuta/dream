using Dream.Client.Presentation;

namespace Dream.Rendering.Core;

// TEMPORARY SEAM, DELETE AT INTEGRATION. Workstream D2 owns Render.Shadows.cs and its PrepareShadows; this branch
// (D3) only needs the call in PlayScene.Render.cs to compile. When D2's file lands this declaration becomes a duplicate
// member and the build fails on purpose: delete this whole file then. If D2 spelled the signature differently, the call
// site in PlayScene.Render.cs binds to this stub instead and the first frame throws, rather than rendering with the
// shadows silently off.
public partial class Render
{
    public void PrepareShadows(WorldSessionPresentationState presentation) =>
        throw new NotImplementedException("Render.PrepareShadows is workstream D2's; delete Render.ShadowsSeam.cs when it lands.");
}
