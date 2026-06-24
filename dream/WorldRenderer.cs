public class WorldRenderer
{
    private readonly Render _render;

    public WorldRenderer(Render render)
    {
        _render = render;
    }

    public void DrawWorld(Camera camera, IEnumerable<Cube> cubes)
    {
        List<Cube> visibleCubes = new();
        foreach (Cube cube in cubes)
        {
            if (!WorldVisibility.IsCubeVisible(camera.Position, camera.Front, cube))
            {
                continue;
            }

            visibleCubes.Add(cube);
        }

        foreach (WorldRenderBatch batch in WorldRenderBatchPlanner.CreateBatches(visibleCubes))
        {
            if (batch.Animation != null)
            {
                _render.DrawAnimatedCubeBatch(camera, batch.Animation, batch.ShaderName, batch.Cubes);
                continue;
            }

            foreach (Cube cube in batch.Cubes)
            {
                _render.DrawCube(camera, cube);
            }
        }
    }
}
