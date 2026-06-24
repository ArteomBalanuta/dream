using OpenTK.Mathematics;

// Temporary compile placeholder for the Task 1 seam; later tasks replace this
// with the dedicated frame-state model in its own file.
public readonly record struct ViewModelFrameState;

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
