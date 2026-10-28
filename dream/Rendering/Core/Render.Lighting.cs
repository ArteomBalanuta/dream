using Dream.Rendering.Lighting;

namespace Dream.Rendering.Core;

public partial class Render
{
    private FrameLighting _frameLighting = FrameLighting.ReferenceNoon;

    // Lighting the next world pass uploads. The scene sets it each frame before DrawWorld.
    public void SetFrameLighting(in FrameLighting lighting) => _frameLighting = lighting;

    public ref readonly FrameLighting CurrentFrameLighting => ref _frameLighting;
}
