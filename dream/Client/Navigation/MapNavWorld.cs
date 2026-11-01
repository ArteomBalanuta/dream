using Dream.Simulation.World;

namespace Dream.Client.Navigation;

public sealed class MapNavWorld(Map map) : INavWorld
{
    private readonly Map _map = map;

    public int Width => throw new NotImplementedException();

    public int Height => throw new NotImplementedException();

    public int Depth => throw new NotImplementedException();

    public bool IsBlocked(int x, int y, int z) => throw new NotImplementedException();

    public bool IsSupport(int x, int y, int z) => throw new NotImplementedException();
}
