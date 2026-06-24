using OpenTK.Mathematics;

public class ViewModelRenderer
{
    private readonly Render _render;

    public ViewModelRenderer(Render render)
    {
        _render = render;
    }

    public void Draw(Camera camera, ViewModelFrameState? frameState)
    {
        if (frameState is null)
        {
            return;
        }

        // Intentionally empty in Task 1. The seam must exist before feature logic lands.
    }
}
