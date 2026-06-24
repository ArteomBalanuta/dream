public class WorldRenderer
{
    private readonly Render _render;

    public WorldRenderer(Render render)
    {
        _render = render;
    }

    public void DrawWorld(Camera camera, IEnumerable<Cube> cubes)
    {
        foreach (Cube cube in cubes)
        {
            _render.DrawCube(camera, cube);
        }
    }
}
