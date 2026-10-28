using Dream.Rendering.Lighting;
using Dream.Simulation.Players;

namespace Dream.Rendering.Core;

public partial class Render
{
    private FrameLighting _frameLighting = FrameLighting.ReferenceNoon;

    // The FrameLighting uniform buffer. Created with the shaders, disposed with them.
    private FrameLightingBuffer? _frameLightingBuffer;

    // Lighting the next world pass uploads. The scene sets it each frame before DrawWorld.
    public void SetFrameLighting(in FrameLighting lighting) => _frameLighting = lighting;

    public ref readonly FrameLighting CurrentFrameLighting => ref _frameLighting;

    // Uploads CurrentFrameLighting for this frame (world and view-model ranges) and binds the
    // world range. WorldRenderer.DrawWorld calls it once per frame, before any world draw.
    public void UploadFrameLighting(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        _frameLightingBuffer?.Upload(in _frameLighting, camera.Position, camera.GetViewMatrix());
    }

    public void BindWorldLighting() => _frameLightingBuffer?.BindWorld();

    public void BindViewModelLighting() => _frameLightingBuffer?.BindViewModel();

    // Creates the buffer and fills both ranges with the current lighting (camera at the origin),
    // so a world program drawn before the first DrawWorld still reads defined values.
    private void InitializeFrameLighting()
    {
        _frameLightingBuffer = new FrameLightingBuffer();
        _frameLightingBuffer.Upload(in _frameLighting, OpenTK.Mathematics.Vector3.Zero, OpenTK.Mathematics.Matrix4.Identity);
    }

    private void DisposeFrameLighting()
    {
        _frameLightingBuffer?.Dispose();
        _frameLightingBuffer = null;
    }
}
